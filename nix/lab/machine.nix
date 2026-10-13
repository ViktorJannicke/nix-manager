# The lab machine: GNOME on Wayland with alice logged in, Orca ready, Nix
# Manager installed and an /etc/nixos configuration to start from. Orca's
# speech is written to /run/lab/spoken.txt instead of the sound card, so tests
# can read exactly what a screen-reader user hears.
{ self, userAccount }:
{ pkgs, ... }:
{
  imports = [
    userAccount
    self.nixosModules.default
  ];

  virtualisation = {
    memorySize = 4096;
    cores = 4;
    resolution = {
      x = 1280;
      y = 800;
    };
  };

  services.displayManager.gdm.enable = true;
  services.displayManager.autoLogin = {
    enable = true;
    user = "alice";
  };
  services.desktopManager.gnome.enable = true;

  # Shell.Eval is internal; unsafe mode lets the test ask Shell whether it
  # finished starting up, as nixpkgs' own GNOME test does.
  systemd.user.services."org.gnome.Shell@".serviceConfig.ExecStart = [
    ""
    "${pkgs.gnome-shell}/bin/gnome-shell --unsafe-mode"
  ];

  programs.dconf.profiles.user.databases = [
    {
      settings = {
        # Chromium joins the accessibility bus only when the desktop says
        # accessibility is on; Orca says so too, but tests also read the tree
        # without it.
        "org/gnome/desktop/interface".toolkit-accessibility = true;
        # Long tests and lab sessions sit idle; keep the screen on and unlocked.
        "org/gnome/desktop/session".idle-delay = pkgs.lib.gvariant.mkUint32 0;
        "org/gnome/desktop/screensaver".lock-enabled = false;
      };
    }
  ];

  # libao has no sound card in the VM; its null driver always opens.
  environment.etc."libao.conf".text = "default_driver=null\n";
  services.speechd = {
    enable = true;
    config = ''
      AddModule "transcript" "sd_generic" "transcript.conf"
      DefaultModule "transcript"
    '';
    modules.transcript = ''
      AddVoice "en" "male1" "transcript"
      AddVoice "de" "male1" "transcript"
      GenericLanguage "en" "en" "utf-8"
      GenericLanguage "de" "de" "utf-8"
      GenericExecuteSynth "echo \'$DATA\' >> /run/lab/spoken.txt"
    '';
  };

  # What a fresh install leaves behind, so the setup assistant has
  # something to start from. A mode makes a copy: the assistant skips
  # symlinks such as the store links /etc usually holds.
  environment.etc."nixos/configuration.nix" = {
    mode = "0644";
    text = ''
      { pkgs, ... }:
      {
        imports = [ ./hardware-configuration.nix ];
        boot.loader.systemd-boot.enable = true;
        networking.hostName = "machine";
        services.desktopManager.gnome.enable = true;
        services.displayManager.gdm.enable = true;
        users.users.alice = {
          isNormalUser = true;
          extraGroups = [ "wheel" ];
        };
        environment.systemPackages = [ pkgs.firefox ];
        system.stateVersion = "26.05";
      }
    '';
  };
  environment.etc."nixos/hardware-configuration.nix" = {
    mode = "0644";
    text = ''
      { modulesPath, ... }:
      {
        imports = [ (modulesPath + "/profiles/qemu-guest.nix") ];
        fileSystems."/" = {
          device = "/dev/vda";
          fsType = "ext4";
        };
      }
    '';
  };

  programs.nix-manager.enable = true;
  environment.systemPackages = [
    # The VM has no GPU Chromium accepts; render in software as nixpkgs'
    # Chromium test does, and expose web content to AT-SPI from the start.
    (pkgs.chromium.override {
      commandLineArgs = [
        "--use-gl=angle"
        "--use-angle=swiftshader"
        "--enable-unsafe-swiftshader"
        "--force-renderer-accessibility"
        # Autologin cannot unlock GNOME Keyring; its password prompt blocks.
        "--password-store=basic"
      ];
    })
    # lab-atspi [app]: print the accessibility tree of matching apps.
    (pkgs.writeShellScriptBin "lab-atspi" ''
      export GI_TYPELIB_PATH=${
        pkgs.lib.makeSearchPath "lib/girepository-1.0" [
          pkgs.at-spi2-core
          pkgs.gobject-introspection
          pkgs.glib.out
        ]
      }
      exec ${pkgs.python3.withPackages (p: [ p.pygobject3 ])}/bin/python3 ${./atspi-dump.py} "$@"
    '')
    # lab-user command...: run a command in alice's desktop session.
    (pkgs.writeShellScriptBin "lab-user" ''
      exec ${pkgs.util-linux}/bin/runuser -u alice -- env \
        DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus \
        XDG_RUNTIME_DIR=/run/user/1000 WAYLAND_DISPLAY=wayland-0 "$@"
    '')
  ];
  # GNOME's own Orca writes the debug log. Replacing a running Orca with a
  # second one crashed GNOME Shell 50 (segfault in libmutter).
  systemd.user.services.orca.serviceConfig.ExecStart = [
    ""
    "${pkgs.orca}/bin/orca --replace --debug --debug-file /run/lab/orca-debug.out"
  ];
  # Chromium renders in software here and can answer AT-SPI slowly; Orca 50
  # then blocks past its 6-second watchdog and is killed mid-sentence.
  systemd.user.services.orca.serviceConfig.WatchdogSec = "30s";
  # Not /tmp: protected_regular stops root from opening files there that
  # the user's speech-dispatcher created.
  systemd.tmpfiles.rules = [ "d /run/lab 0777 root root -" ];
}
