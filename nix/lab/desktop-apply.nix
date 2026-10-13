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

  testScript = ''
    from datetime import timedelta

    checkout = "/home/alice/nixos-config"
    system = f"{checkout}#nixosConfigurations.machine.config.system.build.toplevel"

    def current():
        return machine.succeed("readlink -f /run/current-system").strip()

    def desktop():
        machine.wait_for_unit("display-manager.service")
        machine.wait_for_file("/run/user/1000/wayland-0")
        machine.wait_for_unit("default.target", "alice")

    def use(file, message):
        machine.succeed(f"cp {file} {checkout}/configuration.nix && chown alice:users {checkout}/configuration.nix")
        commit(message)

    def commit(message):
        machine.succeed(f"lab-user git -C {checkout} add -A")
        machine.succeed(f"lab-user git -C {checkout} -c user.name=Alice -c user.email=alice@machine commit -qm '{message}'")

    with subtest("GNOME starts from the bootloader"):
        desktop()
        machine.succeed("bootctl status >&2")

    with subtest("alice has a checkout that evaluates to prebuilt outputs"):
        machine.copy_from_host("${cache}", "/tmp/lab-cache")
        machine.succeed("nix copy --no-check-sigs --from file:///tmp/lab-cache ${checkout} ${variants}")
        machine.succeed(f"cp -r ${checkout} {checkout} && chmod -R u+w {checkout} && chown -R alice:users {checkout}")
        machine.succeed(f"lab-user git -C {checkout} init -q")
        commit("Start from /etc/nixos")
        machine.succeed("lab-user mkdir -p /home/alice/.local/state/nix-manager")
        machine.succeed(f"lab-user sh -c 'echo {checkout} > /home/alice/.local/state/nix-manager/checkout'")
        version = machine.succeed(f"lab-user nix eval --raw {checkout}#nixosConfigurations.machine.config.system.nixos.version")
        print(f"NixOS version in the VM: {version}, on the host: ${systems.base.config.system.nixos.version}")
        planned = machine.succeed(f"lab-user nix build --dry-run {system} 2>&1")
        print(planned)
        assert "will be built" not in planned, "the VM would build the base system"
        machine.succeed(f"lab-user nix eval --raw {system}.outPath | grep -qx ${toplevel "base"}")

    with subtest("Apply now switches to the checkout's system"):
        machine.succeed("lab-user nix-manager --plain system switch >&2", timeout=timedelta(minutes=10))
        assert current() == "${toplevel "base"}", f"running {current()}"

    with subtest("Apply on next boot waits for the reboot"):
        use("${variants}/hello.nix", "Add GNU Hello")
        planned = machine.succeed(f"lab-user nix build --dry-run {system} 2>&1")
        assert "will be built" not in planned, planned
        machine.succeed("lab-user nix-manager --plain system boot >&2", timeout=timedelta(minutes=10))
        assert current() == "${toplevel "base"}", f"running {current()} before the reboot"
        machine.fail("hello")
        machine.shutdown()
        machine.start()
        desktop()
        assert current() == "${toplevel "hello"}", f"running {current()} after the reboot"
        machine.succeed("hello")

    with subtest("Cleanup keeps three generations and deletes the rest"):
        use("${checkout}/configuration.nix", "Remove GNU Hello")
        machine.succeed("lab-user nix-manager --plain system switch >&2", timeout=timedelta(minutes=10))
        assert current() == "${toplevel "base"}", f"running {current()}"
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
