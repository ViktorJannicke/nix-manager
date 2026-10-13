# The desktop window under Orca: screenshots, the accessibility tree and
# what Orca says are copied to the test output; see machine.nix.
{ pkgs, self }:
let
  userAccount = "${pkgs.path}/nixos/tests/common/user-account.nix";
in
pkgs.testers.runNixOSTest {
  name = "nix-manager-desktop-a11y";
  enableOCR = true;

  nodes.machine = import ./machine.nix { inherit self userAccount; };

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
  '';
}
