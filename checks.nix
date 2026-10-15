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
  # The Runic port, turned on explicitly, with a registry and the shells whose completions it installs.
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
            hosts.desktop = host;
            tray.enable = true;
          };
          programs.fish.enable = true;
          programs.zsh.enable = true;
        }
      ];
    }).config;
  hasRunic = self.packages.${system} ? runic;
  runic = self.packages.${system}.runic;
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
  # as the Bun check runs main.js without its wrapper. It is the NativeAOT executable; without the wrapper's
  # library path it finds no ICU.
  runicCommand = lib.escapeShellArgs [
    "env"
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
    assert hasRunic -> lib.hasPrefix "${lib.getExe runic} tray --interval 360" (trayStart runicOn);
    assert lib.hasPrefix "${lib.getExe manager} tray --interval 360" (trayStart bunOnly);
    assert builtins.elem manager bunOnly.environment.systemPackages;
    # With the port turned on, only its package is installed, and the registry is the same.
    assert
      hasRunic
      -> (
        builtins.elem runic runicOn.environment.systemPackages
        && !(builtins.elem manager runicOn.environment.systemPackages)
        &&
          runicOn.environment.etc."nix-manager/hosts.json".text
          == first.environment.etc."nix-manager/hosts.json".text
        && runicOn.environment.variables.NIX_MANAGER_HOSTS == "/etc/nix-manager/hosts.json"
        &&
          runicOn.systemd.user.services.nix-manager-tray.environment.NIX_MANAGER_HOSTS
          == "/etc/nix-manager/hosts.json"
        && runicOn.security.wrappers.pkexec.enable
      );
    # What each configuration links into /run/current-system/sw: one command, one policy whose program is that
    # command, the desktop entry and icon, and (for the port) the completions of every shell.
    pkgs.runCommand "nix-manager-module-tests"
      {
        nativeBuildInputs = [ pkgs.libxml2 ];
        bun = bunOnly.system.path;
        bunCommand = lib.getExe manager;
      }
      (
        ''
          check() {
            local path=$1 command=$2
            [[ $(readlink -f $path/bin/nix-manager) == $(readlink -f $command) ]]
            policies=($path/share/polkit-1/actions/*nix-manager*)
            [[ ''${#policies[@]} == 1 && ''${policies[0]##*/} == org.nixos.nix-manager.policy ]]
            [[ $(xmllint --xpath '//action[@id="org.nixos.nix-manager"]/annotate[@key="org.freedesktop.policykit.exec.path"]/text()' ''${policies[0]}) == "$command" ]]
            grep -qx 'Exec=nix-manager ui' $path/share/applications/nix-manager.desktop
            grep -qx 'Icon=nix-manager' $path/share/applications/nix-manager.desktop
            [[ -e $path/share/icons/hicolor/scalable/apps/nix-manager.svg ]]
          }
          check $bun $bunCommand
        ''
        + lib.optionalString hasRunic ''
          check ${runicOn.system.path} ${lib.getExe runic}
          grep -qx 'StartupNotify=true' ${runicOn.system.path}/share/applications/nix-manager.desktop
          grep -qx 'Exec=nix-manager tray' ${runicOn.system.path}/share/applications/nix-manager.desktop
          grep -q "^complete -F .* 'nix-manager'" ${runicOn.system.path}/share/bash-completion/completions/nix-manager
          grep -qx '#compdef nix-manager' ${runicOn.system.path}/share/zsh/site-functions/_nix-manager
          grep -q "^complete -c 'nix-manager'" ${runicOn.system.path}/share/fish/vendor_completions.d/nix-manager.fish
        ''
        + "touch $out"
      );
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
