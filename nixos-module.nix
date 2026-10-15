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
  # The Runic port (runic/), built where its NuGet lock files apply.
  runicDefault = self.packages.${pkgs.stdenv.hostPlatform.system}.runic or null;
  # `true` here removes the Bun version from the module (README.md, Trying the Runic port).
  useRunic = cfg.runic.enable;
  # The one version installed: its command, desktop entry, icon and completions, and the tray service's daemon.
  # The Runic package brings its own Polkit policy; the Bun version's comes from this module.
  selected = if useRunic then cfg.runic.package else cfg.package;
  registryPath = "/etc/nix-manager/hosts.json";
  polkitPolicy = pkgs.writeTextFile {
    name = "org.nixos.nix-manager.policy";
    destination = "/share/polkit-1/actions/org.nixos.nix-manager.policy";
    text = ''
      <?xml version="1.0" encoding="UTF-8"?>
      <!DOCTYPE policyconfig PUBLIC
       "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN"
       "https://specifications.freedesktop.org/PolicyKit/1.0/policyconfig.dtd">
      <policyconfig>
        <action id="org.nixos.nix-manager">
          <icon_name>system-software-update</icon_name>
          <description>Rebuild NixOS System</description>
          <message>Authentication is required to rebuild the NixOS system configuration.</message>
          <defaults>
            <allow_any>auth_admin</allow_any>
            <allow_inactive>auth_admin</allow_inactive>
            <allow_active>auth_admin_keep</allow_active>
          </defaults>
          <annotate key="org.freedesktop.policykit.exec.path">${lib.getExe cfg.package}</annotate>
        </action>
      </policyconfig>
    '';
  };
in
{
  options.programs.nix-manager = {
    enable = lib.mkEnableOption "Nix Manager CLI, desktop dashboard and tray";
    package = lib.mkOption {
      type = lib.types.package;
      default = self.packages.${pkgs.stdenv.hostPlatform.system}.default;
      description = "Nix Manager package, built with this flake's independently locked dependencies.";
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
    runic = {
      enable = lib.mkOption {
        type = lib.types.bool;
        default = false;
        description = ''
          Install the Runic port instead of the Bun version: its
          `nix-manager` command, desktop entry, icon, shell completions and
          Polkit policy (the same action), and a tray service that runs its
          daemon, which owns every operation, shows the status icon and
          opens the dashboard window. The Bun package is then not installed.
          Off by default until the port replaces the Bun version (W300-032).
        '';
      };
      package = lib.mkOption {
        type = lib.types.nullOr lib.types.package;
        default = runicDefault;
        defaultText = lib.literalExpression "nix-manager.packages.\${system}.runic";
        description = "The Runic port's package.";
      };
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
    assertions = [
      {
        assertion = !useRunic || cfg.runic.package != null;
        message = "programs.nix-manager.runic.enable needs programs.nix-manager.runic.package; the flake builds it for ${
          lib.concatStringsSep " and " (builtins.attrNames (lib.filterAttrs (_: p: p ? runic) self.packages))
        }.";
      }
    ];
    security.polkit.enable = true;
    security.wrappers.pkexec.enable = lib.mkForce true;
    # One policy per action, for the installed program.
    environment.systemPackages = [ selected ] ++ lib.optional (!useRunic) polkitPolicy;
    environment.etc."nix-manager/hosts.json" = lib.mkIf hasHosts {
      text = builtins.toJSON cfg.hosts;
    };
    environment.variables.NIX_MANAGER_HOSTS = lib.mkIf hasHosts registryPath;
    systemd.user.services.nix-manager-tray = lib.mkIf cfg.tray.enable {
      description =
        if useRunic then
          "Nix Manager: operations, tray and update notifications"
        else
          "Nix Manager tray and update notifications";
      wantedBy = [ "graphical-session.target" ];
      partOf = [ "graphical-session.target" ];
      after = [ "graphical-session.target" ];
      environment = lib.optionalAttrs hasHosts { NIX_MANAGER_HOSTS = registryPath; };
      serviceConfig = {
        ExecStart = lib.concatStringsSep " " (
          [ (lib.getExe selected) ]
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
        Restart = "on-failure";
        RestartSec = 30;
      };
    };
  };
}
