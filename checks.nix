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
      ./runic/package.nix
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
  # The tray service and command of the Runic port, turned on explicitly.
  runicOn =
    (self.inputs.nixpkgs.lib.nixosSystem {
      inherit system;
      modules = [
        self.nixosModules.default
        {
          system.stateVersion = "26.05";
          programs.nix-manager = {
            enable = true;
            runic.enable = true;
            tray.enable = true;
          };
        }
      ];
    }).config;
  # The tray service and command of the Bun version, with the Runic port turned off.
  bunOnly =
    (self.inputs.nixpkgs.lib.nixosSystem {
      inherit system;
      modules = [
        self.nixosModules.default
        {
          system.stateVersion = "26.05";
          programs.nix-manager = {
            enable = true;
            runic.enable = false;
            tray.enable = true;
          };
        }
      ];
    }).config;
  trayStart = config: config.systemd.user.services.nix-manager-tray.serviceConfig.ExecStart;
  # The Runic port's program without its wrapper, whose PATH prefix would put the real nix before the mocked one,
  # as the Bun check runs main.js without its wrapper.
  runicCommand = lib.escapeShellArgs [
    "env"
    "DOTNET_ROOT=${pkgs.dotnetCorePackages.sdk_10_0.aspnetcore}/share/dotnet"
    "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"
    "${self.packages.${system}.runic}/lib/nix-manager-runic/nix-manager"
  ];
in
{
  nix-manager = manager;
}
// lib.optionalAttrs (self.packages.${system} ? runic) {
  # The Runic port, built and unit-tested in the sandbox, and the Bun version's CLI regression suite run
  # against its command line.
  runic = self.packages.${system}.runic;
  cli-runic =
    pkgs.runCommand "nix-manager-runic-cli-tests"
      {
        nativeBuildInputs = with pkgs; [
          python3
          bash
          coreutils
        ];
        NIX_MANAGER_TEST_COMMAND = runicCommand;
        # The software commands are ported (W300-033); their draft needs the edit engine of the same merge.
        NIX_MANAGER_TEST_SOFTWARE = "1";
      }
      ''
        python3 ${./test/test-manager.py}
        touch $out
      '';
  # The daemon of D025 (W300-031) as processes: a daemon or a dashboard, and command lines that hand it their
  # operations, with mocked build tools and the dashboard served instead of shown.
  daemon-runic =
    pkgs.runCommand "nix-manager-runic-daemon-tests"
      {
        nativeBuildInputs = with pkgs; [
          python3
          bash
          coreutils
        ];
        NIX_MANAGER_TEST_COMMAND = runicCommand;
      }
      ''
        python3 ${./test/test-daemon.py}
        touch $out
      '';
}
// {
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
    assert
      !(lib.hasInfix "--flake" first.systemd.user.services.nix-manager-tray.serviceConfig.ExecStart);
    # The tray service runs the Bun tray by default and the Runic daemon when the port is turned on.
    assert lib.hasPrefix "${lib.getExe manager} tray --interval 360" (trayStart first);
    assert
      (self.packages.${system} ? runic)
      -> lib.hasPrefix "${lib.getExe self.packages.${system}.runic} tray --interval 360" (
        trayStart runicOn
      );
    assert lib.hasPrefix "${lib.getExe manager} tray --interval 360" (trayStart bunOnly);
    assert builtins.elem manager bunOnly.environment.systemPackages;
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
