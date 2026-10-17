# The desktop window under Orca: screenshots, the accessibility tree and
# what Orca says are copied to the test output; see machine.nix.
{
  pkgs,
  self,
  runic ? false,
}:
let
  userAccount = pkgs.path + "/nixos/tests/common/user-account.nix";
in
pkgs.testers.runNixOSTest {
  name = "nix-manager-desktop-a11y" + pkgs.lib.optionalString runic "-runic";
  enableOCR = true;

  nodes.machine = import ./machine.nix { inherit self userAccount runic; };

  testScript = ''
    from datetime import timedelta

    def speech():
        return machine.succeed("cat /run/lab/spoken.txt")

    def settle():
        # Orca reads a new page aloud and moves its caret while it does; wait
        # until the transcript stops growing.
        machine.wait_until_succeeds(
            "a=$(wc -c < /run/lab/spoken.txt); sleep 3; [ $a = $(wc -c < /run/lab/spoken.txt) ]",
            timeout=timedelta(seconds=120),
        )

    def tree(app, file):
        machine.succeed(f"lab-user lab-atspi {app} > /run/lab/{file} 2>&1")
        machine.copy_from_machine(f"/run/lab/{file}")

    with subtest("GNOME starts"):
        machine.wait_for_unit("display-manager.service")
        machine.wait_for_file("/run/user/1000/wayland-0")
        machine.wait_for_unit("default.target", "alice")
        # Shell.Eval answers (true, 'false') once start-up finished.
        machine.wait_until_succeeds(
            "lab-user gdbus call --session -d org.gnome.Shell -o /org/gnome/Shell"
            " -m org.gnome.Shell.Eval Main.layoutManager._startingUp | grep -q 'true,..false'"
        )
        machine.send_key("esc")

    with subtest("Orca speaks into the transcript"):
        machine.succeed("lab-user gsettings set org.gnome.desktop.a11y.applications screen-reader-enabled true")
        machine.wait_until_succeeds("grep -q 'Screen reader on' /run/lab/spoken.txt", timeout=timedelta(seconds=120))
        print(speech())

  ''
  + (
    if runic then
      ''
        import re

        def cores():
            # The web process aborts in WebKitGTK's AT-SPI code while Orca reads
            # the dashboard (see README.md); count its core dumps.
            return int(machine.succeed("coredumpctl list --no-legend 2>/dev/null | grep -c WebKitWebProces || true").strip() or 0)

        def focused():
            # The deepest focused node in the port's process: the web view's
            # element, or a GTK widget when focus leaves the web view.
            lines = machine.execute("lab-user lab-atspi nix-manager | grep -w focused")[1].strip().splitlines()
            return lines[-1].strip() if lines else ""

        def walk(limit=24):
            # Tab until focus comes back to the first stop; returns the stops.
            stops = []
            for press in range(1, limit + 1):
                machine.send_key("tab")
                machine.sleep(2)
                stop = focused()
                print(f"Tab {press}: {stop or 'nothing focused'}")
                if stops and stop == stops[0]:
                    return stops
                stops.append(stop)
                assert cores() == before, f"WebKitWebProcess aborted after Tab {press}; see the core dump list and README.md"
            raise AssertionError(f"focus did not come back to {stops[0]!r} within {limit} presses: {stops}")

        def names(stops):
            return [m.group(1) for m in (re.search(r"'([^']*)'", stop) for stop in stops) if m]

        def cycle(stops):
            # The stops of one Tab cycle, starting at the skip link.
            order = names(stops)
            start = order.index("Skip to content") if "Skip to content" in order else 0
            return order[start:] + order[:start]

        def tab_to(name, limit=24):
            for _ in range(limit):
                if re.search(f"'{re.escape(name)}'", focused()):
                    return
                machine.send_key("tab")
                machine.sleep(1)
            raise AssertionError(f"Tab never reached {name!r}")

        with subtest("The Runic port is installed"):
            program = machine.succeed("readlink -f /run/current-system/sw/bin/nix-manager").strip()
            assert "nix-manager-runic" in program, program
            policy = machine.succeed("pkaction --action-id org.nixos.nix-manager --verbose")
            assert f"org.freedesktop.policykit.exec.path -> {program}" in policy, policy
            # pkexec maps the program to the action (no other rule lets alice
            # run it) and runs it as root, which refuses an unknown generation.
            refused = machine.fail(f"lab-user /run/wrappers/bin/pkexec {program} --privileged activate-generation 999 2>&1")
            assert "Invalid system generation." in refused, refused
            machine.succeed("grep -qx 'StartupNotify=true' /run/current-system/sw/share/applications/nix-manager.desktop")
            machine.succeed("test -e /run/current-system/sw/share/bash-completion/completions/nix-manager.bash")

        with subtest("The setup assistant under Orca"):
            # Without a checkout, `nix-manager ui` opens the setup assistant, as the Bun version does.
            machine.succeed("lab-user mv /home/alice/my-flakes /home/alice/seeded-flakes")
            before = cores()
            settle()
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            machine.succeed("lab-user systemd-run --user --unit nix-manager-setup nix-manager ui")
            machine.wait_until_succeeds("lab-user lab-atspi nix-manager | grep -q \"entry 'Folder'\"", timeout=timedelta(seconds=180))
            machine.screenshot("setup")
            tree("nix-manager", "tree-setup.txt")
            settle()
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            setup_stops = []
            for press in range(4):
                machine.send_key("tab")
                machine.sleep(2)
                setup_stops.append(focused())
                assert cores() == before, f"WebKitWebProcess aborted after Tab {press + 1} in the setup assistant"
            print("Setup Tab order:", setup_stops)
            for want in ["entry 'Folder'", "button 'Create my configuration'"]:
                assert any(want in stop for stop in setup_stops), f"Tab never reached {want}: {setup_stops}"
            spoken = speech()
            print(spoken)
            for phrase in ["Folder", "Create my configuration", "button"]:
                assert phrase in spoken, f"Orca did not say {phrase!r} in the setup assistant"
            tab_to("Folder")
            # Orca switches to focus mode; before that it takes letters as commands.
            machine.sleep(2)
            machine.send_key("ctrl-a")
            machine.send_chars("~/lab-config")
            machine.wait_until_succeeds(
                "lab-user lab-atspi nix-manager | grep -F \"entry 'Folder' value='~/lab-config'\"",
                timeout=timedelta(seconds=30),
            )
            machine.succeed("lab-user systemctl --user stop nix-manager-setup")
            machine.succeed("lab-user mv /home/alice/seeded-flakes /home/alice/my-flakes")

        with subtest("Nix Manager opens in Runic Desktop"):
            machine.succeed("lab-user gsettings set org.gnome.desktop.a11y.applications screen-reader-enabled false")
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            machine.succeed("lab-user systemd-run --user --unit nix-manager nix-manager ui")
            try:
                machine.wait_until_succeeds("lab-user lab-atspi nix-manager | grep -q \"document web '.*Nix Manager'\"", timeout=timedelta(seconds=180))
                machine.wait_until_succeeds("lab-user lab-atspi nix-manager | grep -q \"landmark 'Sections'\"", timeout=timedelta(seconds=60))
            except Exception:
                machine.screenshot("window-missing")
                print(machine.execute("lab-user lab-atspi 2>&1 | head -60")[1])
                print(machine.execute("lab-user journalctl --user -u nix-manager --no-pager -n 120")[1])
                raise
            machine.screenshot("window")
            tree("nix-manager", "tree-window.txt")
            before = cores()

        with subtest("Tab moves through the page"):
            stops = walk()
            machine.screenshot("after-tabs")
            print("Tab order:", stops)
            order = names(stops)
            assert order[0] == "Skip to content", f"the first stop is {stops[0]!r}"
            # The navigation's buttons, in the order the page lists them; Tab visits them one after another.
            sections = re.findall(r"button '([^']*)'", machine.succeed(
                "lab-user lab-atspi nix-manager | sed -n \"/landmark 'Sections'/,/landmark '[^S]/p\""
            ))
            print("Sections:", sections)
            assert sections[:1] == ["Overview"] and "Settings" in sections, f"unexpected sections: {sections}"
            start = order.index("Overview")
            assert order[start:start + len(sections)] == sections, f"the sections are not in order: {order}"
            # G-17: Runic Desktop may add one stop outside the web view, with no name.
            silent = [stop for stop in stops if not re.search(r"'[^']+'", stop)]
            print(f"Stops without a name: {silent}")
            assert len(silent) <= 1, f"more than one stop without a name: {stops}"

        with subtest("Orca reads the stops"):
            machine.succeed("lab-user gsettings set org.gnome.desktop.a11y.applications screen-reader-enabled true")
            machine.wait_until_succeeds("grep -q 'Screen reader on' /run/lab/spoken.txt", timeout=timedelta(seconds=120))
            settle()
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            try:
                spoken_stops = walk()
            finally:
                machine.screenshot("orca-tabs")
                machine.copy_from_machine("/run/lab/spoken.txt")
                machine.copy_from_machine("/run/lab/orca-debug.out")
                print(speech())
            assert cycle(spoken_stops) == cycle(stops), f"Orca's Tab order {spoken_stops} differs from {stops}"
            spoken = speech()
            for phrase in ["Skip to content", "Sections", "Overview", "Settings", "button"]:
                assert phrase in spoken, f"Orca did not say {phrase!r}"
            # The navigation is a list Orca counts, as in the Bun version, without the WebKitGTK abort (README.md).
            assert f"List with {len(sections)} items" in spoken, f"Orca did not say 'List with {len(sections)} items'"

        with subtest("Orca reads the new number of restore points to keep"):
            # The Bun version announced it through a live region, which WebKitGTK never reports; the port's field is a
            # spin button, whose value Orca reads as it changes (RetentionField.svelte).
            tab_to("Restore points")
            machine.send_key("ret")
            machine.wait_until_succeeds("lab-user lab-atspi nix-manager | grep -q \"spin button 'Keep newest'\"", timeout=timedelta(seconds=30))
            tab_to("Keep newest", limit=40)
            machine.sleep(2)
            def kept():
                line = machine.succeed("lab-user lab-atspi nix-manager | grep -m1 \"spin button 'Keep newest'\"")
                found = re.search(r"value=(\d+)", line)
                assert found, f"the spin button has no value: {line!r}"
                return int(found.group(1))
            before_value = kept()
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            machine.send_key("up")
            machine.wait_until_succeeds(f"grep -qx '{before_value + 1}' /run/lab/spoken.txt", timeout=timedelta(seconds=20))
            # Orca finishes saying it before the next key, as a listener would wait.
            settle()
            print(f"Up in the restore points to keep: {speech()!r}")
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            machine.send_key("down")
            machine.wait_until_succeeds(f"grep -qx '{before_value}' /run/lab/spoken.txt", timeout=timedelta(seconds=20))
            print(f"Down in the restore points to keep: {speech()!r}")
            assert kept() == before_value
            assert cores() == before, "WebKitWebProcess aborted on the Restore points page"

        with subtest("Orca hears the Window announce an operation"):
            # WebKitGTK reports no live region to Orca, so the page has the Window announce a running operation's
            # step and the toast at its end (DashboardViewModel.Announce). Offline, the check ends one way or another.
            tab_to("Updates", limit=80)
            machine.send_key("ret")
            machine.wait_until_succeeds("lab-user lab-atspi nix-manager | grep -q \"button 'Check for updates'\"", timeout=timedelta(seconds=30))
            tab_to("Check for updates", limit=40)
            settle()
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            machine.send_key("ret")
            machine.wait_until_succeeds(
                "grep -qE 'Check for updates failed|Everything is up to date|updates? available' /run/lab/spoken.txt",
                timeout=timedelta(minutes=3),
            )
            settle()
            spoken = speech()
            print(f"Checking for updates: {spoken!r}")
            ended = re.search(r"Check for updates failed|Everything is up to date|updates? available", spoken)
            assert ended, f"Orca did not say how the check ended: {spoken!r}"
            step = spoken.find("Check for updates: ")
            assert 0 <= step < ended.start(), "Orca did not say the running step before the operation's end"
            assert cores() == before, "WebKitWebProcess aborted on the Updates page"

        with subtest("The keyboard opens Settings"):
            tab_to("Settings", limit=80)
            machine.send_key("ret")
            machine.wait_until_succeeds("lab-user lab-atspi nix-manager | grep -q \"heading 'Settings'\"", timeout=timedelta(seconds=30))
            machine.screenshot("settings")
            tree("nix-manager", "tree-settings.txt")
            assert cores() == before, "WebKitWebProcess aborted on the Settings page"

        # The ACCESSIBILITY.md checklist on the Settings page. What Orca says is
        # recorded in checklist.txt; only a crash fails these subtests, so page
        # changes do not.
        checklist = []

        def note(label, text):
            checklist.append(f"## {label}\n{text.strip()}\n")
            print(f"{label}: {text.strip()!r}")

        def heading_size(name):
            line = machine.succeed(f"lab-user env LAB_ATSPI_EXTENTS=1 lab-atspi nix-manager | grep -m1 \"heading '{name}'\"")
            size = re.search(r"@(\d+x\d+)", line)
            return size.group(1) if size else "unknown"

        with subtest("Orca's browse mode: headings, lists and radio buttons"):
            for key, label in [("h", "H: next heading"), ("h", "H again"), ("l", "L: next list"), ("shift-l", "Shift+L: previous list")]:
                machine.succeed("truncate -s 0 /run/lab/spoken.txt")
                machine.send_key(key)
                machine.sleep(3)
                note(label, speech())
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            tab_to("System")
            machine.sleep(2)
            note("Tab to the theme's radio group", speech())
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            machine.send_key("right")
            machine.sleep(3)
            note("Right arrow in the radio group", speech())
            assert cores() == before, "WebKitWebProcess aborted in browse mode"

        with subtest("Zoom and large text"):
            normal = heading_size("Settings")
            machine.send_key("ctrl-equal")
            machine.sleep(2)
            note("Ctrl and + (G-18)", f"heading 'Settings' {normal} before, {heading_size('Settings')} after")
            machine.send_key("ctrl-0")
            machine.succeed("lab-user gsettings set org.gnome.desktop.interface text-scaling-factor 1.5")
            machine.sleep(4)
            machine.screenshot("large-text")
            note("GNOME Large Text (1.5)", f"heading 'Settings' {normal} before, {heading_size('Settings')} after")
            machine.succeed("lab-user gsettings reset org.gnome.desktop.interface text-scaling-factor")
            machine.succeed("cat > /run/lab/checklist.txt <<'CHECKLIST'\n" + "\n".join(checklist).replace("'", "") + "\nCHECKLIST")
            machine.copy_from_machine("/run/lab/checklist.txt")
            machine.copy_from_machine("/run/lab/orca-debug.out")
            assert cores() == before, "WebKitWebProcess aborted"
      ''
    else
      ''
        with subtest("Nix Manager opens in Chromium"):
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            machine.succeed("lab-user systemd-run --user --unit nix-manager nix-manager ui")
            try:
                machine.wait_until_succeeds("lab-user lab-atspi chrom | grep -q 'Set up this computer'", timeout=timedelta(seconds=180))
            except Exception:
                machine.screenshot("window-missing")
                print(machine.execute("lab-user lab-atspi 2>&1 | head -60")[1])
                print(machine.execute("lab-user journalctl --user -u nix-manager --no-pager -n 120")[1])
                raise
            machine.screenshot("window")
            tree("chrom", "tree-window.txt")
            print(speech())

        with subtest("Tab moves through the page"):
            settle()
            machine.succeed("truncate -s 0 /run/lab/spoken.txt")
            expected = ["entry 'Folder'", "button 'Create my configuration'"] * 2
            for press, want in enumerate(expected, 1):
                machine.send_key("tab")
                machine.sleep(2)
                focused = machine.execute("lab-user lab-atspi chrom | grep -w focused")[1].strip()
                print(f"Tab {press}: {focused or 'nothing focused in Chromium'}")
                assert want in focused, f"Tab {press} focused {focused!r}, expected {want}"
            machine.screenshot("after-tabs")
            spoken = speech()
            print(spoken)
            machine.copy_from_machine("/run/lab/spoken.txt")
            machine.copy_from_machine("/run/lab/orca-debug.out")
            for phrase in ["Folder", "Create my configuration", "button", "A new, empty folder in your home folder."]:
                assert phrase in spoken, f"Orca did not say {phrase!r}"

        with subtest("Typing reaches the folder field"):
            machine.send_key("shift-tab")
            # Orca switches to focus mode; before that it takes letters as commands.
            machine.sleep(2)
            machine.send_key("ctrl-a")
            machine.send_chars("~/lab-config")
            machine.wait_until_succeeds(
                "lab-user lab-atspi chrom | grep -F \"entry 'Folder' value='~/lab-config'\"",
                timeout=timedelta(seconds=30),
            )
      ''
  );
}
