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
          <annotate key="org.freedesktop.policykit.exec.allow_gui">false</annotate>
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
        An empty registry uses data/hosts.nix from the selected checkout.
        Changing this setting does not rebuild the application.
      '';
    };
    tray = {
      enable = lib.mkEnableOption "Nix Manager tray in graphical sessions";
      flakePath = lib.mkOption {
        type = lib.types.str;
        default = "%h/my-flakes";
        description = "Checkout used by the tray; %h expands to the user's home directory.";
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
    environment.systemPackages = [
      cfg.package
      polkitPolicy
    ];
    environment.etc."nix-manager/hosts.json" = lib.mkIf hasHosts {
      text = builtins.toJSON cfg.hosts;
    };
    environment.variables.NIX_MANAGER_HOSTS = lib.mkIf hasHosts registryPath;
    systemd.user.services.nix-manager-tray = lib.mkIf cfg.tray.enable {
      description = "Nix Manager tray and update notifications";
      wantedBy = [ "graphical-session.target" ];
      partOf = [ "graphical-session.target" ];
      after = [ "graphical-session.target" ];
      environment = lib.optionalAttrs hasHosts { NIX_MANAGER_HOSTS = registryPath; };
      serviceConfig = {
        ExecStart = "${lib.getExe cfg.package} --flake ${lib.escapeShellArg cfg.tray.flakePath} tray --interval ${toString cfg.tray.intervalMinutes}";
        Restart = "on-failure";
        RestartSec = 30;
      };
    };
  };
}
