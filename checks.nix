{
  pkgs,
  self,
  system,
}:
let
  inherit (pkgs) lib;
  manager = self.packages.${system}.default;
  source = lib.fileset.toSource {
    root = ./.;
    fileset = lib.fileset.unions [
      ./flake.nix
      ./package.nix
      ./webui.nix
      ./nixos-module.nix
      ./checks.nix
    ];
  };
  host = {
    inherit system;
    nixosConfiguration = "test-machine";
    nixosHostName = "test-machine";
    homeConfiguration = "test-user";
  };
  mkConfig =
    hosts:
    (self.inputs.nixpkgs.lib.nixosSystem {
      inherit system;
      modules = [
        self.nixosModules.default
        {
          system.stateVersion = "26.05";
          programs.nix-manager = {
            enable = true;
            inherit hosts;
            tray.enable = true;
          };
        }
      ];
    }).config;
  first = mkConfig { desktop = host; };
  second = mkConfig {
    laptop = host // {
      nixosHostName = "another-machine";
    };
  };
in
{
  nix-manager = manager;
  cli =
    pkgs.runCommand "nix-manager-cli-tests"
      {
        nativeBuildInputs = with pkgs; [
          python3
          bash
          bun
          coreutils
        ];
        NIX_MANAGER_TEST_SCRIPT = "${manager}/share/nix-manager/main.js";
      }
      ''
        python3 ${./test/test-manager.py}
        touch $out
      '';
  module =
    assert first.programs.nix-manager.package.drvPath == second.programs.nix-manager.package.drvPath;
    assert first.programs.nix-manager.package.drvPath == manager.drvPath;
    assert
      first.environment.etc."nix-manager/hosts.json".text
      != second.environment.etc."nix-manager/hosts.json".text;
    assert first.environment.variables.NIX_MANAGER_HOSTS == "/etc/nix-manager/hosts.json";
    assert
      first.systemd.user.services.nix-manager-tray.environment.NIX_MANAGER_HOSTS
      == "/etc/nix-manager/hosts.json";
    assert first.security.wrappers.pkexec.enable;
    pkgs.runCommand "nix-manager-module-tests" { } "touch $out";
  formatting =
    pkgs.runCommand "nix-manager-nix-formatting"
      {
        nativeBuildInputs = [
          pkgs.nixfmt
          pkgs.findutils
        ];
      }
      ''
        find ${source} -name '*.nix' -print0 | xargs -0 nixfmt --check
        touch $out
      '';
}
