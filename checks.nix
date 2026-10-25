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
  # A configuration with a registry, the tray, and the shells whose completions the package installs.
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
          programs.fish.enable = true;
          programs.zsh.enable = true;
        }
      ];
    }).config;
  first = mkConfig { desktop = host; };
  second = mkConfig {
    laptop = host // {
      nixosHostName = "another-machine";
    };
  };
  trayStart = config: config.systemd.user.services.nix-manager-tray.serviceConfig.ExecStart;
  # The program without its wrapper, whose PATH prefix would put the real nix before the mocked one. It is the
  # NativeAOT executable, which loads ICU by its store path.
  command = "${manager}/lib/nix-manager/nix-manager";
  regression =
    name: script: extra:
    pkgs.runCommand name (
      {
        nativeBuildInputs = with pkgs; [
          python3
          bash
          coreutils
        ];
        NIX_MANAGER_TEST_COMMAND = command;
      }
      // extra
    ) "python3 ${script}\ntouch $out\n";
in
{
  # Built and unit-tested in the sandbox.
  nix-manager = manager;
  # The command line's regression suite.
  cli = regression "nix-manager-cli-tests" ./test/test-manager.py { };
  # The daemon as processes: a daemon or a dashboard, and command lines that hand it their operations, with
  # mocked build tools and the dashboard served instead of shown.
  daemon = regression "nix-manager-daemon-tests" ./test/test-daemon.py { };
  module =
    # Host metadata is runtime configuration: the application's store path does not change with it.
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
    assert !(lib.hasInfix "--flake" (trayStart first));
    assert lib.hasPrefix "${lib.getExe manager} tray --interval 360" (trayStart first);
    # A switch leaves the tray running; it compares its revision with the installed one and restarts with 75.
    assert lib.hasInfix "X-RestartIfChanged=false"
      first.systemd.user.units."nix-manager-tray.service".text;
    assert
      first.systemd.user.services.nix-manager-tray.environment.NIX_MANAGER_TRAY_REVISION
      == first.environment.etc."nix-manager/tray-revision".text;
    assert
      first.environment.etc."nix-manager/tray-revision".text
      == second.environment.etc."nix-manager/tray-revision".text;
    assert first.systemd.user.services.nix-manager-tray.serviceConfig.RestartForceExitStatus == 75;
    assert builtins.elem manager first.environment.systemPackages;
    # What the configuration links into /run/current-system/sw: one command, one policy whose program is that
    # command, the desktop entry and icon, and the completions of every shell.
    pkgs.runCommand "nix-manager-module-tests" { nativeBuildInputs = [ pkgs.libxml2 ]; } ''
      path=${first.system.path}
      command=${lib.getExe manager}
      [[ $(readlink -f $path/bin/nix-manager) == $(readlink -f $command) ]]
      policies=($path/share/polkit-1/actions/*nix-manager*)
      [[ ''${#policies[@]} == 1 && ''${policies[0]##*/} == org.nixos.nix-manager.policy ]]
      [[ $(xmllint --xpath '//action[@id="org.nixos.nix-manager"]/annotate[@key="org.freedesktop.policykit.exec.path"]/text()' ''${policies[0]}) == "$command" ]]
      grep -qx 'Exec=nix-manager ui' $path/share/applications/nix-manager.desktop
      grep -qx 'Icon=nix-manager' $path/share/applications/nix-manager.desktop
      grep -qx 'StartupNotify=true' $path/share/applications/nix-manager.desktop
      grep -qx 'Exec=nix-manager tray' $path/share/applications/nix-manager.desktop
      [[ -e $path/share/icons/hicolor/scalable/apps/nix-manager.svg ]]
      grep -q "^complete -F .* 'nix-manager'" $path/share/bash-completion/completions/nix-manager.bash
      grep -qx '#compdef nix-manager' $path/share/zsh/site-functions/_nix-manager
      grep -q "^complete -c 'nix-manager'" $path/share/fish/vendor_completions.d/nix-manager.fish
      touch $out
    '';
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
