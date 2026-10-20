# Builds lab systems and homes. The lab VM's configuration checkout calls these
# through nix-manager's flake, and the host calls them on the same files to
# prebuild every configuration a test applies, so the VM evaluates them to
# outputs it already has and builds nothing offline.
{
  nixpkgs,
  home-manager,
  self,
  system,
}:
let
  # What every lab system needs to keep running as a test VM: the driver
  # reaches it through test-instrumentation, before and after a reboot.
  module =
    { lib, modulesPath, ... }:
    {
      imports = [
        "${modulesPath}/virtualisation/qemu-vm.nix"
        "${modulesPath}/testing/test-instrumentation.nix"
        (import ./machine.nix {
          inherit self;
          # desktop-apply.nix writes alice's checkout itself.
          seeded = false;
          userAccount = "${modulesPath}/../tests/common/user-account.nix";
        })
      ];

      # Boot from the disk through systemd-boot, so Apply on next boot and a
      # reboot pick the generation the bootloader lists.
      virtualisation = {
        useBootLoader = true;
        useEFIBoot = true;
        memorySize = lib.mkForce 6144;
        diskSize = 20480;
        fileSystems."/".autoResize = true;
      };
      boot.growPartition = true;
      boot.loader.systemd-boot.enable = true;
      boot.loader.efi.canTouchEfiVariables = true;

      # Offline: everything a test applies is already in the store.
      nix.settings = {
        substituters = lib.mkForce [ ];
        connect-timeout = 1;
        experimental-features = [
          "nix-command"
          "flakes"
        ];
      };
      networking.hostName = "machine";
      # NetworkManager asks for DHCP on the test network, which has none; when
      # that times out during a switch, the failed unit fails the whole apply.
      systemd.services.NetworkManager-wait-online.enable = false;

      # Tests drive the app without a password dialog. A test that checks the
      # dialog itself removes this rule.
      security.polkit.extraConfig = ''
        polkit.addRule(function (action, subject) {
          if (action.id == "org.nixos.nix-manager" && subject.user == "alice") {
            return polkit.Result.YES;
          }
        });
      '';
    };
in
{
  inherit module;
  nixos =
    configuration:
    nixpkgs.lib.nixosSystem {
      modules = [
        configuration
        module
        { nixpkgs.hostPlatform = system; }
      ];
    };
  home =
    configuration:
    home-manager.lib.homeManagerConfiguration {
      pkgs = nixpkgs.legacyPackages.${system};
      modules = [ configuration ];
    };
}
