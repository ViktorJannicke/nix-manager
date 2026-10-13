# nix run .#lab -- start | stop | status | <request> ...
# Boots the desktop-a11y VM in the background and drives it one request at a
# time; see the usage text below. Nothing opens on the host desktop.
{ pkgs, test }:
pkgs.writeShellApplication {
  name = "nix-manager-lab";
  runtimeInputs = [
    pkgs.coreutils
    pkgs.jq
    pkgs.util-linux
  ];
  text = ''
    lab=''${LAB_DIR:-''${XDG_RUNTIME_DIR:-/tmp}/nix-manager-lab}
    usage() {
      cat <<'EOF'
    Usage: nix run .#lab -- COMMAND

      start                 boot the VM and wait for the GNOME desktop
      stop                  shut the VM down
      status                say whether the VM is running
      key KEY...            press keys, such as tab, ret, ctrl-alt-delete
      type TEXT             type text
      shot [NAME]           save a screenshot; prints the PNG path
      tree [APP]            print the accessibility tree of apps matching APP
      focused [APP]         print the focused element
      speech [--clear]      print what Orca said since the last clear
      sh COMMAND            run a command as root in the VM
      user COMMAND          run a command in alice's desktop session
      copy PATH             copy a file out of the VM; prints its path

    Orca: user gsettings set org.gnome.desktop.a11y.applications screen-reader-enabled true
    With Orca on, wait a second after moving focus into a field before typing:
    until Orca switches to focus mode, it takes letters as browse-mode commands.
    Files and the driver log are in $LAB_DIR (default: $XDG_RUNTIME_DIR/nix-manager-lab).
    EOF
    }

    running() { [ -f "$lab/pid" ] && kill -0 "$(cat "$lab/pid")" 2>/dev/null; }

    case "''${1:-}" in
      start)
        if running; then echo "The lab is already running."; exit 0; fi
        rm -rf "$lab"
        mkdir -p "$lab/out"
        LAB_DIR=$lab setsid ${test.driver}/bin/nixos-test-driver --no-interactive \
          --test-script ${./serve.py} -o "$lab/out" >"$lab/driver.log" 2>&1 </dev/null &
        echo $! >"$lab/pid"
        until [ -e "$lab/ready" ]; do
          if ! running; then
            tail -n 30 "$lab/driver.log"
            echo "The lab stopped while starting; see $lab/driver.log." >&2
            exit 1
          fi
          sleep 1
        done
        echo "The lab is ready. Output files go to $lab/out."
        ;;
      status)
        if running; then echo "running"; else echo "stopped"; exit 1; fi
        ;;
      stop)
        if ! running; then echo "The lab is not running."; exit 0; fi
        jq -n '{kind: "stop", args: []}' >"$lab/request.json"
        for _ in $(seq 120); do running || break; sleep 1; done
        if running; then kill "$(cat "$lab/pid")"; fi
        rm -f "$lab/pid"
        echo "The lab stopped."
        ;;
      key | type | shot | tree | focused | speech | sh | user | copy)
        running || { echo "The lab is not running; start it first." >&2; exit 1; }
        exec 9>"$lab/lock"
        flock 9
        rm -f "$lab/response.json"
        jq -n --arg kind "$1" '{kind: $kind, args: $ARGS.positional}' --args -- "''${@:2}" >"$lab/request.json.tmp"
        mv "$lab/request.json.tmp" "$lab/request.json"
        until [ -e "$lab/response.json" ]; do
          running || { echo "The lab stopped; see $lab/driver.log." >&2; exit 1; }
          sleep 0.1
        done
        jq -j .output "$lab/response.json"
        exit "$(jq .status "$lab/response.json")"
        ;;
      *)
        usage
        if [ -n "''${1:-}" ]; then exit 2; fi
        ;;
    esac
  '';
}
