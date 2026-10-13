# A GNOME desktop with Orca in a VM. Orca's speech is written to
# /run/lab/spoken.txt instead of the sound card, so tests can read exactly what a
# screen-reader user hears. Screenshots, the spoken text and the accessibility
# tree are copied to the test's output.
{ pkgs, self }:
let
  userAccount = "${pkgs.path}/nixos/tests/common/user-account.nix";
in
pkgs.testers.runNixOSTest {
  name = "nix-manager-desktop-a11y";
  enableOCR = true;

  nodes.machine =
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

      # Chromium joins the accessibility bus only when the desktop says
      # accessibility is on; Orca says so too, but tests also read the tree
      # without it.
      programs.dconf.profiles.user.databases = [
        { settings."org/gnome/desktop/interface".toolkit-accessibility = true; }
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
          GenericLanguage "en" "en"
          GenericLanguage "de" "de"
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
      # Not /tmp: protected_regular stops root from opening files there that
      # the user's speech-dispatcher created.
      systemd.tmpfiles.rules = [ "d /run/lab 0777 root root -" ];
    };

  testScript = ''
    from datetime import timedelta

    def speech():
        return machine.succeed("cat /run/lab/spoken.txt")

    def settle():
        # Orca reads a new page aloud and moves its caret while it does; wait
        # until the transcript stops growing.
        machine.wait_until_succeeds(
            "a=$(wc -c < /run/lab/spoken.txt); sleep 3; [ $a = $(wc -c < /run/lab/spoken.txt) ]",
            timeout=timedelta(seconds=120),
        )

    def tree(app, file):
        machine.succeed(f"lab-user lab-atspi {app} > /run/lab/{file} 2>&1")
        machine.copy_from_machine(f"/run/lab/{file}")

    with subtest("GNOME starts"):
        machine.wait_for_unit("display-manager.service")
        machine.wait_for_file("/run/user/1000/wayland-0")
        machine.wait_for_unit("default.target", "alice")
        # Shell.Eval answers (true, 'false') once start-up finished.
        machine.wait_until_succeeds(
            "lab-user gdbus call --session -d org.gnome.Shell -o /org/gnome/Shell"
            " -m org.gnome.Shell.Eval Main.layoutManager._startingUp | grep -q 'true,..false'"
        )
        machine.send_key("esc")

    with subtest("Orca speaks into the transcript"):
        machine.succeed("lab-user gsettings set org.gnome.desktop.a11y.applications screen-reader-enabled true")
        machine.wait_until_succeeds("grep -q 'Screen reader on' /run/lab/spoken.txt", timeout=timedelta(seconds=120))
        print(speech())

    with subtest("Nix Manager opens in Chromium"):
        machine.succeed("truncate -s 0 /run/lab/spoken.txt")
        machine.succeed("lab-user systemd-run --user --unit nix-manager nix-manager ui")
        try:
            machine.wait_until_succeeds("lab-user lab-atspi chrom | grep -q 'Set up this computer'", timeout=timedelta(seconds=180))
        except Exception:
            machine.screenshot("window-missing")
            print(machine.execute("lab-user lab-atspi 2>&1 | head -60")[1])
            print(machine.execute("lab-user journalctl --user -u nix-manager --no-pager -n 120")[1])
            raise
        machine.screenshot("window")
        tree("chrom", "tree-window.txt")
        print(speech())

    with subtest("Tab moves through the page"):
        settle()
        machine.succeed("truncate -s 0 /run/lab/spoken.txt")
        expected = ["entry 'Folder'", "button 'Create my configuration'"] * 2
        for press, want in enumerate(expected, 1):
            machine.send_key("tab")
            machine.sleep(2)
            focused = machine.execute("lab-user lab-atspi chrom | grep -w focused")[1].strip()
            print(f"Tab {press}: {focused or 'nothing focused in Chromium'}")
            assert want in focused, f"Tab {press} focused {focused!r}, expected {want}"
        machine.screenshot("after-tabs")
        spoken = speech()
        print(spoken)
        machine.copy_from_machine("/run/lab/spoken.txt")
        machine.copy_from_machine("/run/lab/orca-debug.out")
        for phrase in ["Folder", "Create my configuration", "button", "A new, empty folder in your home folder."]:
            assert phrase in spoken, f"Orca did not say {phrase!r}"

    with subtest("Typing reaches the folder field"):
        machine.send_key("shift-tab")
        # Orca switches to focus mode; before that it takes letters as commands.
        machine.sleep(2)
        machine.send_key("ctrl-a")
        machine.send_chars("~/lab-config")
        machine.wait_until_succeeds(
            "lab-user lab-atspi chrom | grep -F \"entry 'Folder' value='~/lab-config'\"",
            timeout=timedelta(seconds=30),
        )
  '';
}
