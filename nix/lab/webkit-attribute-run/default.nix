# The WebKitGTK attribute-run abort (REPORT.md) in both of Runic Desktop's Linux hosts: GTK 3 with
# webkit2gtk-4.1, which Nix Manager uses, and GTK 4 with webkitgtk-6.0. Each page in pages/ opens in that
# flavour's MiniBrowser on GNOME, and probe.py asks every text object for its attribute runs at every offset.
# The test records which pages abort the web process, with gdb's backtrace of the abort in each flavour, in
# results.txt, and fails when a page behaves differently than expected below. Slow and outside `nix flake check`:
#
#   nix build .#webkit-attribute-run
{ pkgs }:
let
  userAccount = pkgs.path + "/nixos/tests/common/user-account.nix";
  flavours = {
    "webkit2gtk-4.1" = "${pkgs.webkitgtk_4_1}/libexec/webkit2gtk-4.1/MiniBrowser";
    "webkitgtk-6.0" = "${pkgs.webkitgtk_6_0}/libexec/webkitgtk-6.0/MiniBrowser";
  };
  python = pkgs.python3.withPackages (p: [ p.pygobject3 ]);
  typelibs = pkgs.lib.makeSearchPath "lib/girepository-1.0" [
    pkgs.at-spi2-core
    pkgs.gobject-introspection
    pkgs.glib.out
  ];
in
pkgs.testers.runNixOSTest {
  name = "webkit-attribute-run";

  nodes.machine = {
    imports = [ userAccount ];
    virtualisation = {
      memorySize = 4096;
      cores = 4;
    };
    services.displayManager.gdm.enable = true;
    services.displayManager.autoLogin = {
      enable = true;
      user = "alice";
    };
    services.desktopManager.gnome.enable = true;
    systemd.user.services."org.gnome.Shell@".serviceConfig.ExecStart = [
      ""
      "${pkgs.gnome-shell}/bin/gnome-shell --unsafe-mode"
    ];
    # WebKitGTK joins the accessibility bus only when the desktop says accessibility is on.
    programs.dconf.profiles.user.databases = [
      {
        settings = {
          "org/gnome/desktop/interface".toolkit-accessibility = true;
          "org/gnome/desktop/session".idle-delay = pkgs.lib.gvariant.mkUint32 0;
          "org/gnome/desktop/screensaver".lock-enabled = false;
        };
      }
    ];
    environment.systemPackages = [
      pkgs.gdb
      (pkgs.writeShellScriptBin "lab-user" ''
        exec ${pkgs.util-linux}/bin/runuser -u alice -- env \
          DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus \
          XDG_RUNTIME_DIR=/run/user/1000 WAYLAND_DISPLAY=wayland-0 "$@"
      '')
      (pkgs.writeShellScriptBin "probe" ''
        export GI_TYPELIB_PATH=${typelibs}
        exec ${python}/bin/python3 ${./probe.py} "$@"
      '')
    ];
  };

  testScript = ''
    from datetime import timedelta

    flavours = ${builtins.toJSON flavours}
    # Whether each page aborts the web process (REPORT.md).
    expected = {
        "crash": True,
        "roles": True,
        "nav": True,
        "nav-tight": True,
        "no-whitespace": False,
        "nav-workaround": False,
    }

    machine.wait_for_unit("display-manager.service")
    machine.wait_for_file("/run/user/1000/wayland-0")
    machine.wait_for_unit("default.target", "alice")
    machine.wait_until_succeeds(
        "lab-user gdbus call --session -d org.gnome.Shell -o /org/gnome/Shell"
        " -m org.gnome.Shell.Eval Main.layoutManager._startingUp | grep -q 'true,..false'"
    )
    machine.send_key("esc")
    machine.succeed(
        "lab-user systemd-run --user --unit pages --working-directory=${./pages}"
        " ${pkgs.python3}/bin/python3 -m http.server 8123 --bind 127.0.0.1"
    )
    machine.wait_for_open_port(8123)

    def stop():
        machine.execute("pkill -9 -f MiniBrowser; pkill -9 -f WebKitWebProcess; lab-user systemctl --user reset-failed", timeout=60)
        machine.sleep(1)

    def open_page(browser, page):
        stop()
        # Without the web process's sandbox, gdb can read its libraries' symbols.
        machine.succeed(
            "lab-user systemd-run --user --unit minibrowser --collect"
            f" --setenv=WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1 {browser} http://127.0.0.1:8123/{page}.html"
        )

    results, unexpected = [], []
    for flavour, browser in flavours.items():
        for page, aborting in expected.items():
            open_page(browser, page)
            status, output = machine.execute("lab-user timeout 200 probe minibrowser 90", timeout=260)
            line = f"{flavour} {page}.html: {'aborted' if status == 1 else 'answered' if status == 0 else 'not probed'}"
            print(line)
            print(output)
            results.append(line + "\n" + output)
            if status == 2 or (status == 1) != aborting:
                unexpected.append(line)
        # The backtrace of the abort, from gdb attached to the web process before the probe.
        open_page(browser, "crash")
        machine.sleep(10)
        process = machine.succeed("pgrep -n -f WebKitWebProcess").strip()
        machine.succeed(
            "systemd-run --unit backtrace --collect -p StandardOutput=file:/tmp/bt.txt -p StandardError=file:/tmp/bt.txt"
            f" ${pkgs.coreutils}/bin/timeout 200 ${pkgs.gdb}/bin/gdb -p {process} -batch"
            " -ex 'handle SIGUSR1 SIGUSR2 SIGPIPE nostop noprint' -ex continue -ex 'bt 8'"
        )
        machine.sleep(8)
        machine.execute("lab-user timeout 200 probe minibrowser 90", timeout=260)
        machine.wait_until_succeeds("grep -q '^#7' /tmp/bt.txt", timeout=timedelta(seconds=120))
        trace = machine.succeed("grep -E '^#|received signal' /tmp/bt.txt")
        print(trace)
        results.append(f"Backtrace of the web process ({flavour}, crash.html):\n{trace}")
        stop()

    machine.succeed("cat > /tmp/results.txt <<'RESULTS'\n" + "\n".join(results).replace("'", "") + "\nRESULTS")
    machine.copy_from_machine("/tmp/results.txt")
    assert not unexpected, f"pages behaved differently than expected: {unexpected}"
  '';
}
