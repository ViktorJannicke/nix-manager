# Applies configurations for real on a disposable VM that boots through
# systemd-boot: alice has a configuration checkout, and every configuration
# the test applies is prebuilt into the VM's store; see system.nix.
{
  pkgs,
  self,
  nixpkgs,
  home-manager,
}:
let
  inherit (pkgs) lib;
  lab = self.lib.lab.${pkgs.stdenv.hostPlatform.system};
  systems = lib.mapAttrs (_: configuration: lab.nixos configuration) {
    base = ./checkout/configuration.nix;
    hello = ./checkout/variants/hello.nix;
  };
  toplevel = name: systems.${name}.config.system.build.toplevel;
  home = lab.home ./checkout/home.nix;

  # The checkout locks nixpkgs and Home Manager as nix-manager does, so
  # nixosSystem sees the same revision, and nix-manager to its store path.
  own = (builtins.fromJSON (builtins.readFile ../../flake.lock)).nodes;
  lock = {
    version = 7;
    root = "root";
    nodes = {
      root.inputs = {
        nixpkgs = "nixpkgs";
        home-manager = "home-manager";
        nix-manager = "nix-manager";
      };
      inherit (own) nixpkgs home-manager;
      nix-manager = {
        locked = {
          type = "path";
          path = self.outPath;
          inherit (self) narHash lastModified;
        };
        original = {
          type = "path";
          path = self.outPath;
        };
        inputs = {
          nixpkgs = [ "nixpkgs" ];
          home-manager = [ "home-manager" ];
        };
      };
    };
  };
  checkout = pkgs.runCommand "lab-checkout" { } ''
    mkdir $out
    cp ${./checkout/configuration.nix} $out/configuration.nix
    cp ${./checkout/home.nix} $out/home.nix
    substitute ${./checkout/flake.nix} $out/flake.nix \
      --subst-var-by nix-manager ${self} \
      --subst-var-by system ${pkgs.stdenv.hostPlatform.system}
    cp ${pkgs.writeText "flake.lock" (builtins.toJSON lock)} $out/flake.lock
  '';
  variants = ./checkout/variants;
  # These change with every edit to the repository, so they come in at test
  # time through a binary cache and the disk image only rebuilds when the
  # systems do.
  cache = pkgs.mkBinaryCache {
    rootPaths = [
      checkout
      variants
    ];
  };
  # Helpers and the checkout, shared by the test and `nix run .#lab -- start apply`.
  setup = ''
    from datetime import timedelta

    checkout = "/home/alice/nixos-config"
    system = f"{checkout}#nixosConfigurations.machine.config.system.build.toplevel"

    def current():
        return machine.succeed("readlink -f /run/current-system").strip()

    def desktop():
        machine.wait_for_unit("display-manager.service")
        machine.wait_for_file("/run/user/1000/wayland-0")
        machine.wait_for_unit("default.target", "alice")
        # GNOME opens the overview at login; close it once Shell has started,
        # so keys reach the windows.
        machine.wait_until_succeeds(
            "lab-user gdbus call --session -d org.gnome.Shell -o /org/gnome/Shell"
            " -m org.gnome.Shell.Eval Main.layoutManager._startingUp | grep -q 'true,..false'",
            timeout=timedelta(minutes=5),
        )
        machine.send_key("esc")

    def use(file, message):
        machine.succeed(f"cp {file} {checkout}/configuration.nix && chown alice:users {checkout}/configuration.nix")
        commit(message)

    def commit(message):
        machine.succeed(f"lab-user git -C {checkout} add -A")
        machine.succeed(f"lab-user git -C {checkout} -c user.name=Alice -c user.email=alice@machine commit -qm '{message}'")

    def focused():
        return machine.execute("lab-user lab-atspi chrom | grep -w focused | grep -v 'document web'")[1].strip()

    def tab_to(name, presses=40):
        for _ in range(presses):
            machine.send_key("tab")
            machine.sleep(timedelta(milliseconds=500))
            if f"'{name}'" in focused():
                return
        raise Exception(f"Tab never reached {name!r}; focus is on {focused()!r}")

    def wait_for(text):
        try:
            machine.wait_until_succeeds(f"lab-user lab-atspi chrom | grep -qF \"{text}\"", timeout=timedelta(minutes=5))
        except Exception:
            machine.screenshot("missing")
            print(machine.execute("lab-user lab-atspi chrom | head -120")[1])
            print(machine.execute("lab-user journalctl --user -u nix-manager -u orca --no-pager -n 60")[1])
            raise

    def settle():
        # Orca reads a new page aloud; wait until the transcript stops growing.
        machine.wait_until_succeeds(
            "a=$(wc -c < /run/lab/spoken.txt); sleep 3; [ $a = $(wc -c < /run/lab/spoken.txt) ]",
            timeout=timedelta(minutes=2),
        )

    def open_dashboard():
        machine.succeed("lab-user systemd-run --user --collect --unit nix-manager nix-manager ui")
        wait_for("Prepare your user and system together")
        settle()

    def back_to_overview():
        # As a user would. Stopping the app's unit instead kills Chromium mid-call,
        # which stalls Orca until its watchdog restarts it.
        tab_to("Overview")
        machine.send_key("ret")
        wait_for("Prepare your user and system together")
        settle()

    def prepare_and_confirm(action, dialog):
        tab_to("Prepare your user and system together")
        machine.send_key("ret")
        wait_for(f"button '{action}'")
        settle()
        tab_to(action)
        machine.succeed("truncate -s 0 /run/lab/spoken.txt")
        machine.send_key("ret")
        wait_for(f"dialog '{dialog}'")
        settle()
        machine.screenshot(action.lower().replace(" ", "-") + "-dialog")
        spoken = machine.succeed("cat /run/lab/spoken.txt")
        print(spoken)
        assert dialog in spoken, f"Orca did not read the dialog {dialog!r}"
        # What confirming does is part of the dialog's description.
        assert "stays as a restore point you can go back to" in spoken, "Orca did not read what confirming does"
        tab_to(action)
        before = operations()
        machine.send_key("ret")
        # The whole operation, boot entry and home activation included, not
        # just the first profile it switches.
        newest = "$(ls -t /home/alice/.local/state/nix-manager/operations/*.json | head -1)"
        machine.wait_until_succeeds(
            f"[ $(ls /home/alice/.local/state/nix-manager/operations/*.json | wc -l) -gt {before} ]"
            f" && ! grep -q '^  \"status\": \"running\"' {newest}",
            timeout=timedelta(minutes=10),
        )
        machine.succeed(f"grep -q '^  \"status\": \"succeeded\"' {newest}")

    def operations():
        return int(machine.succeed("ls /home/alice/.local/state/nix-manager/operations/*.json | wc -l").strip())

    def lab_setup():
        machine.copy_from_host("${cache}", "/tmp/lab-cache")
        machine.succeed("nix copy --no-check-sigs --from file:///tmp/lab-cache ${checkout} ${variants}")
        machine.succeed(f"cp -r ${checkout} {checkout} && chmod -R u+w {checkout} && chown -R alice:users {checkout}")
        machine.succeed(f"lab-user git -C {checkout} init -q")
        commit("Start from /etc/nixos")
        machine.succeed("lab-user mkdir -p /home/alice/.local/state/nix-manager")
        machine.succeed(f"lab-user sh -c 'echo {checkout} > /home/alice/.local/state/nix-manager/checkout'")
  '';
in
pkgs.testers.runNixOSTest {
  name = "nix-manager-desktop-apply";

  nodes.machine = {
    imports = [
      lab.module
      ./checkout/configuration.nix
    ];
    virtualisation.additionalPaths = [
      (toplevel "base")
      (toplevel "hello")
      home.activationPackage
      "${nixpkgs}"
      "${home-manager}"
    ];
  };

  passthru.labSetup = pkgs.writeText "lab-setup.py" setup;

  testScript = setup + ''
    with subtest("GNOME starts from the bootloader"):
        desktop()
        machine.succeed("bootctl status >&2")

    with subtest("alice has a checkout that evaluates to prebuilt outputs"):
        lab_setup()
        version = machine.succeed(f"lab-user nix eval --raw {checkout}#nixosConfigurations.machine.config.system.nixos.version")
        print(f"NixOS version in the VM: {version}, on the host: ${systems.base.config.system.nixos.version}")
        planned = machine.succeed(f"lab-user nix build --dry-run {system} 2>&1")
        print(planned)
        assert "will be built" not in planned, "the VM would build the base system"
        machine.succeed(f"lab-user nix eval --raw {system}.outPath | grep -qx ${toplevel "base"}")

    with subtest("The dashboard applies now, with Orca reading along"):
        machine.succeed("lab-user gsettings set org.gnome.desktop.a11y.applications screen-reader-enabled true")
        machine.wait_until_succeeds("grep -q 'Screen reader on' /run/lab/spoken.txt", timeout=timedelta(minutes=2))
        open_dashboard()
        prepare_and_confirm("Apply now", "Apply these changes now?")
        machine.wait_until_succeeds("readlink -f /run/current-system | grep -qx ${toplevel "base"}", timeout=timedelta(minutes=10))
        # alice's first home generation, in the folder Home Manager uses for new users.
        machine.wait_until_succeeds(
            "readlink -f /home/alice/.local/state/nix/profiles/home-manager | grep -qx ${home.activationPackage}",
            timeout=timedelta(minutes=5),
        )
        machine.screenshot("applied-now")

    with subtest("The dashboard applies on next boot, which waits for the reboot"):
        use("${variants}/hello.nix", "Add GNU Hello")
        planned = machine.succeed(f"lab-user nix build --dry-run {system} 2>&1")
        assert "will be built" not in planned, planned
        back_to_overview()
        prepare_and_confirm("Apply on next boot", "Apply on next boot?")
        machine.wait_until_succeeds(
            "readlink -f /nix/var/nix/profiles/system | grep -qx ${toplevel "hello"}", timeout=timedelta(minutes=10)
        )
        assert current() == "${toplevel "base"}", f"running {current()} before the reboot"
        machine.fail("hello")
        machine.shutdown()
        machine.start()
        desktop()
        assert current() == "${toplevel "hello"}", f"running {current()} after the reboot"
        machine.succeed("hello")

    with subtest("software apply removes a package and keeps the change in the history"):
        machine.succeed("lab-user nix-manager software remove --plain --system hello >&2")
        machine.succeed("lab-user nix-manager software prepare --plain >&2", timeout=timedelta(minutes=10))
        machine.succeed("lab-user nix-manager software apply --plain >&2", timeout=timedelta(minutes=10))
        assert current() == "${toplevel "base"}", f"running {current()}"
        machine.fail("test -e /run/current-system/sw/bin/hello")
        subject = machine.succeed(f"lab-user git -C {checkout} log -1 --format=%s").strip()
        print(f"Kept in the history: {subject}")
        assert "hello" in subject.lower(), f"the last commit is {subject!r}"
        machine.succeed(f"lab-user git -C {checkout} diff --quiet")

    with subtest("Cleanup keeps three generations and deletes the rest"):
        first = machine.succeed("readlink -f /nix/var/nix/profiles/system-1-link").strip()
        machine.succeed("lab-user nix-manager --plain system gc >&2", timeout=timedelta(minutes=10))
        generations = machine.succeed("nix-env --profile /nix/var/nix/profiles/system --list-generations")
        print(generations)
        assert len(generations.strip().splitlines()) == 3, "cleanup did not keep exactly three generations"
        machine.fail(f"test -e {first}")
        machine.fail("test -e ${checkout}")
        # Every boot menu entry must still start a system that exists.
        systems = machine.succeed("grep -ho 'init=[^ ]*' /boot/loader/entries/*.conf").split()
        print(f"Boot entries after cleanup: {systems}")
        assert len(systems) == 3, "the boot menu does not list the three kept generations"
        for entry in systems:
            machine.succeed(f"test -e {entry.removeprefix('init=')}")
        machine.shutdown()
        machine.start()
        desktop()
        assert current() == "${toplevel "base"}", f"running {current()} after cleanup and a reboot"
  '';
}
