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
  useRunic = cfg.runic.enable;
  # The tray service runs the daemon of whichever version is selected.
  trayPackage = if useRunic then cfg.runic.package else cfg.package;
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
        default = runicDefault != null;
        defaultText = lib.literalMD "`true` where the flake builds the Runic port (x86_64-linux)";
        description = ''
          Use the Runic port: the tray service runs its daemon, which owns
          every operation, shows the status icon and opens the dashboard
          window; its `nix-manager` command comes first on the PATH, so
          `nix-manager ui` and command lines reach that daemon; and its
          Polkit policy replaces the Bun version's, which uses the same
          action. The Bun package stays installed with its desktop entry,
          which starts the selected command. Turn this off to run the Bun
          version alone until the port replaces it (W300-032).
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
        message = "programs.nix-manager.runic.enable needs programs.nix-manager.runic.package; the flake builds it for x86_64-linux.";
      }
    ];
    security.polkit.enable = true;
    security.wrappers.pkexec.enable = lib.mkForce true;
    # One policy per action: the Runic package brings its own, for its own program.
    environment.systemPackages = [
      cfg.package
    ]
    ++ (if useRunic then [ (lib.hiPrio cfg.runic.package) ] else [ polkitPolicy ]);
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
          [ (lib.getExe trayPackage) ]
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
