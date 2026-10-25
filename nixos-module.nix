{ self }:
{
  lib,
  config,
  pkgs,
  ...
}:
let
  cfg = config.programs.nix-manager;
  hasHosts = cfg.hosts != { };
  registryPath = "/etc/nix-manager/hosts.json";
  trayEnvironment = lib.optionalAttrs hasHosts { NIX_MANAGER_HOSTS = registryPath; };
  trayStart = lib.concatStringsSep " " (
    [ (lib.getExe cfg.package) ]
    ++ lib.optionals (cfg.tray.flakePath != null) [
      "--flake"
      (lib.escapeShellArg cfg.tray.flakePath)
    ]
    ++ [
      "tray"
      "--interval"
      (toString cfg.tray.intervalMinutes)
    ]
  );
  # Changes with anything the service runs; the tray compares it with the installed one.
  trayRevision = builtins.hashString "sha256" (
    builtins.toJSON {
      start = trayStart;
      environment = trayEnvironment;
    }
  );
in
{
  imports = [
    (lib.mkRenamedOptionModule
      [ "programs" "nix-manager" "runic" "package" ]
      [ "programs" "nix-manager" "package" ]
    )
    (lib.mkRemovedOptionModule [ "programs" "nix-manager" "runic" "enable" ] ''
      Nix Manager is now only the Runic application; the Bun version was removed. Remove this setting.
    '')
  ];

  options.programs.nix-manager = {
    enable = lib.mkEnableOption "Nix Manager CLI, desktop dashboard and tray";
    package = lib.mkOption {
      type = lib.types.package;
      default = self.packages.${pkgs.stdenv.hostPlatform.system}.default;
      description = ''
        Nix Manager package, built with this flake's independently locked dependencies: its `nix-manager`
        command, desktop entry, icon, shell completions and Polkit policy, and the daemon the tray service runs.
      '';
    };
    hosts = lib.mkOption {
      type = lib.types.attrsOf (lib.types.attrsOf lib.types.str);
      default = { };
      description = ''
        Runtime host registry. Each host needs system, nixosConfiguration,
        nixosHostName and homeConfiguration strings; userName is optional.
        An empty registry uses data/hosts.nix from the selected checkout, or
        detects the host from its nixosConfigurations and homeConfigurations.
        Changing this setting does not rebuild the application.
      '';
    };
    tray = {
      enable = lib.mkEnableOption "Nix Manager tray in graphical sessions";
      flakePath = lib.mkOption {
        type = lib.types.nullOr lib.types.str;
        default = null;
        example = "%h/nixos-config";
        description = ''
          Checkout used by the tray; %h expands to the user's home directory.
          Null uses the same checkout as the window: the one the setup
          assistant created, or ~/my-flakes.
        '';
      };
      intervalMinutes = lib.mkOption {
        type = lib.types.ints.between 5 10080;
        default = 360;
        description = "Minutes between read-only update checks.";
      };
    };
  };

  config = lib.mkIf cfg.enable {
    security.polkit.enable = true;
    security.wrappers.pkexec.enable = lib.mkForce true;
    # The package brings its own Polkit policy for its program.
    environment.systemPackages = [ cfg.package ];
    environment.etc."nix-manager/hosts.json" = lib.mkIf hasHosts {
      text = builtins.toJSON cfg.hosts;
    };
    environment.variables.NIX_MANAGER_HOSTS = lib.mkIf hasHosts registryPath;
    environment.etc."nix-manager/tray-revision" = lib.mkIf cfg.tray.enable { text = trayRevision; };
    systemd.user.services.nix-manager-tray = lib.mkIf cfg.tray.enable {
      description = "Nix Manager: operations, tray and update notifications";
      wantedBy = [ "graphical-session.target" ];
      partOf = [ "graphical-session.target" ];
      after = [ "graphical-session.target" ];
      # The daemon may be running the switch itself: stopping it there would close the dashboard and wait for
      # the switch until systemd kills it. It restarts into the new revision once it is idle instead.
      restartIfChanged = false;
      environment = trayEnvironment // {
        NIX_MANAGER_TRAY_REVISION = trayRevision;
      };
      serviceConfig = {
        ExecStart = trayStart;
        Restart = "on-failure";
        # Restarts start almost at once and slow down to 30 seconds when they repeat.
        RestartSec = 1;
        RestartSteps = 5;
        RestartMaxDelaySec = 30;
        # Exit status 75 (EX_TEMPFAIL): a newer revision is installed, so the tray ended for it to start.
        SuccessExitStatus = 75;
        RestartForceExitStatus = 75;
      };
    };
  };
}
