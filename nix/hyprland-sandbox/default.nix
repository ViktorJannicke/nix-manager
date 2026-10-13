# nix run .#hyprland-sandbox -- [OPTIONS] [--] [COMMAND...]
#
# Hyprland on the GPU render node, rootless, in a bubblewrap sandbox with its
# own runtime directory, D-Bus session bus, HOME and PID namespace, and no
# network, host Wayland, X11 or D-Bus sockets. Only /dev/dri/renderD128 is
# passed in; no card node, so no DRM master and no real outputs.
#
# Aquamarine's headless backend has no allocator of its own and its DRM
# backend needs a KMS card node, so Hyprland runs nested on Aquamarine's
# Wayland backend inside a headless labwc (wlroots renders on the render node
# without a seat). labwc is only a parent: Hyprland's own output is a
# headless one (`hyprctl output create headless`), rendered with GLES on the
# render node that labwc advertises through linux-dmabuf feedback. labwc is
# used because sway and cage offer xdg_wm_base version 5 and Aquamarine 0.15
# binds version 6.
{ pkgs }:
let
  inherit (pkgs) lib;

  # A virtual pointer that stays alive for the whole session; see vpointer.c.
  vpointer = pkgs.stdenv.mkDerivation {
    pname = "hyprland-sandbox-vpointer";
    version = "0.1";
    src = ./vpointer.c;
    dontUnpack = true;
    strictDeps = true;
    nativeBuildInputs = [
      pkgs.pkg-config
      pkgs.wayland-scanner
    ];
    buildInputs = [ pkgs.wayland ];
    buildPhase = ''
      xml=${pkgs.wlr-protocols}/share/wlr-protocols/unstable/wlr-virtual-pointer-unstable-v1.xml
      wayland-scanner client-header $xml wlr-virtual-pointer-unstable-v1-client-protocol.h
      wayland-scanner private-code $xml wlr-virtual-pointer-unstable-v1-protocol.c
      $CC -O2 -Wall -Werror -I. -o vpointer $src wlr-virtual-pointer-unstable-v1-protocol.c \
        $(pkg-config --cflags --libs wayland-client)
    '';
    installPhase = "install -Dm755 vpointer $out/bin/vpointer";
    meta.mainProgram = "vpointer";
  };

  hyprlandConf = pkgs.writeText "hyprland.conf" ''
    # The nested window in labwc is not used; the sandbox adds HEADLESS-1.
    monitor = WAYLAND-1, disable
    monitor = , 1280x720@60, 0x0, 1
    env = XCURSOR_THEME, Adwaita
    env = XCURSOR_SIZE, 24
    debug {
      disable_logs = false
    }
    misc {
      disable_hyprland_logo = true
      disable_splash_rendering = true
      disable_watchdog_warning = true
      force_default_wallpaper = 0
    }
    ecosystem {
      no_update_news = true
      no_donation_nag = true
    }
    xwayland {
      enabled = false
    }
    animations {
      enabled = false
    }
  '';

  waybarConfig = pkgs.writeText "waybar.json" (
    builtins.toJSON {
      layer = "top";
      position = "top";
      height = 32;
      modules-left = [ "hyprland/workspaces" ];
      modules-center = [ "clock" ];
      modules-right = [ "tray" ];
      clock.format = "{:%H:%M}";
      tray = {
        icon-size = 22;
        spacing = 8;
      };
    }
  );

  waybarStyle = pkgs.writeText "waybar.css" ''
    * { font-family: "DejaVu Sans"; font-size: 13px; }
    window#waybar { background: #1e2228; color: #e6e6e6; }
    #workspaces button, #clock, #tray { padding: 0 8px; }
  '';

  fontsConf = pkgs.makeFontsConf { fontDirectories = [ pkgs.dejavu_fonts ]; };

  # Commands available inside the sandbox, for the session and for tests.
  helpers = pkgs.symlinkJoin {
    name = "hyprland-sandbox-helpers";
    paths = [
      (pkgs.writeShellScriptBin "sandbox-shot" ''
        # sandbox-shot NAME.png: screenshot of the whole layout into the output directory.
        set -eu
        case "$1" in /*) dest=$1 ;; *) dest=$SANDBOX_OUT/$1 ;; esac
        hyprctl dismissnotify >/dev/null
        grim "$dest"
        echo "$dest"
      '')
      (pkgs.writeShellScriptBin "sandbox-pointer" ''
        # sandbox-pointer move X Y | click left|right|middle | down B | up B | scroll N
        set -eu
        echo "$*" > "$XDG_RUNTIME_DIR/vpointer.in"
        IFS= read -r reply < "$XDG_RUNTIME_DIR/vpointer.out"
        [ "$reply" = ok ] || { echo "sandbox-pointer: $reply" >&2; exit 1; }
      '')
      (pkgs.writeShellScriptBin "sandbox-sni-items" ''
        # Print the StatusNotifierItems registered with Waybar's watcher, one per line.
        dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher \
          org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher \
          string:RegisteredStatusNotifierItems 2>/dev/null | sed -n 's/^ *string "\(.*\)"$/\1/p'
      '')
      (pkgs.writeShellScriptBin "sandbox-wait-sni" ''
        # sandbox-wait-sni [COUNT] [SECONDS]: wait until COUNT items are in the tray.
        want=''${1:-1} limit=''${2:-20}
        for _ in $(seq $((limit * 10))); do
          [ "$(sandbox-sni-items | wc -l)" -ge "$want" ] && exit 0
          sleep 0.1
        done
        echo "sandbox-wait-sni: fewer than $want items after ''${limit}s" >&2
        exit 1
      '')
      (pkgs.writeShellScriptBin "sandbox-wait-client" ''
        # sandbox-wait-client CLASS [SECONDS]: wait until a window of CLASS is mapped.
        limit=''${2:-20}
        for _ in $(seq $((limit * 10))); do
          hyprctl clients | grep -q "class: $1\$" && exit 0
          sleep 0.1
        done
        echo "sandbox-wait-client: no window of class $1 after ''${limit}s" >&2
        exit 1
      '')
      (pkgs.writeShellScriptBin "hyprland-sandbox-demo" ''
        # The default command: a CopyQ status icon in Waybar's tray, its
        # dbusmenu opened with a right click, and text typed into foot.
        set -eu
        out=$SANDBOX_OUT
        {
          echo "renderer: $(grep -a -m1 'Renderer:' "$SANDBOX_LOGS/hyprland.log" | sed 's/.*Renderer: //')"
          echo "/dev/dri: $(ls /dev/dri)"
          echo "network interfaces: $(sed -n 's/^ *\([^:]*\):.*/\1/p' /proc/net/dev | tr '\n' ' ')"
          echo "session bus: $DBUS_SESSION_BUS_ADDRESS"
          echo "runtime dir: $(ls -A "$XDG_RUNTIME_DIR" | tr '\n' ' ')"
          # wayland-0 is labwc's and /tmp/.X11-unix its lazy Xwayland socket,
          # both private to the sandbox.
          for p in /run/dbus/system_bus_socket "/run/user/$(id -u)/bus" /run/user/$(id -u)/pipewire-0 /etc/machine-id; do
            if [ -e "$p" ]; then echo "present: $p"; else echo "absent: $p"; fi
          done
        } > "$out/isolation.txt"
        cat "$out/isolation.txt"

        QT_QPA_PLATFORM=wayland copyq > "$SANDBOX_LOGS/copyq.log" 2>&1 &
        sandbox-wait-sni 1
        echo "tray items: $(sandbox-sni-items | tr '\n' ' ')"
        sleep 1.5
        sandbox-shot tray.png

        # The only tray icon sits at the right end of the 32 px bar.
        sandbox-pointer move $((SANDBOX_WIDTH - 21)) 16
        sleep 0.3
        sandbox-pointer click right
        sleep 1.5
        sandbox-shot tray-menu.png
        # The menu holds a popup grab; a click outside dismisses it.
        sandbox-pointer move $((SANDBOX_WIDTH / 2)) $((SANDBOX_HEIGHT / 2))
        sandbox-pointer click left
        sleep 0.5

        hyprctl dispatch exec "foot -o 'font=DejaVu Sans Mono:size=11'" >/dev/null
        sandbox-wait-client foot
        sleep 1
        wtype "echo typed in the sandbox > $out/typed.txt" -k Return
        for _ in $(seq 50); do [ -s "$out/typed.txt" ] && break; sleep 0.1; done
        sandbox-shot typed.png
        [ -s "$out/typed.txt" ] || { echo "hyprland-sandbox-demo: typing into foot failed" >&2; exit 1; }
        echo "typed.txt: $(cat "$out/typed.txt")"
      '')
    ];
  };

  tools = pkgs.buildEnv {
    name = "hyprland-sandbox-tools";
    paths = [
      helpers
      vpointer
      pkgs.bashInteractive
      pkgs.coreutils
      pkgs.findutils
      pkgs.gnugrep
      pkgs.gnused
      pkgs.procps
      pkgs.dbus
      pkgs.hyprland
      pkgs.labwc
      pkgs.waybar
      pkgs.grim
      pkgs.wtype
      pkgs.foot
      pkgs.copyq
      pkgs.adwaita-icon-theme
      pkgs.hicolor-icon-theme
    ];
    pathsToLink = [
      "/bin"
      "/share"
    ];
  };

  # Runs inside the sandbox under dbus-run-session.
  session = pkgs.writeShellScript "hyprland-sandbox-session" ''
    set -u
    logs=$SANDBOX_LOGS
    mkdir -p "$logs"
    fail() { echo "hyprland-sandbox: $*" >&2; exit 1; }

    export WLR_BACKENDS=headless WLR_RENDERER=gles2 WLR_LIBINPUT_NO_DEVICES=1
    export WLR_RENDER_DRM_DEVICE=$SANDBOX_RENDER_NODE
    labwc -s "sh -c 'XDG_CURRENT_DESKTOP=Hyprland exec Hyprland -c ${hyprlandConf} > $logs/hyprland.stdout 2>&1'" \
      > "$logs/labwc.log" 2>&1 &
    labwc_pid=$!

    stop() {
      [ -n "''${HYPRLAND_INSTANCE_SIGNATURE:-}" ] && cp "$XDG_RUNTIME_DIR"/hypr/*/hyprland.log "$logs/" 2>/dev/null
      hyprctl dispatch exit >/dev/null 2>&1
      kill "$labwc_pid" 2>/dev/null
      wait "$labwc_pid" 2>/dev/null
      # Anything left ends with the sandbox's PID namespace.
    }
    trap stop EXIT

    for _ in $(seq 100); do
      sock=$(ls "$XDG_RUNTIME_DIR"/hypr/*/.socket.sock 2>/dev/null) && break
      kill -0 "$labwc_pid" 2>/dev/null || break
      sleep 0.1
    done
    [ -n "''${sock:-}" ] || fail "Hyprland did not start; see $logs/hyprland.stdout and $logs/labwc.log"
    export HYPRLAND_INSTANCE_SIGNATURE=$(basename "$(dirname "$sock")")

    hyprctl output create headless HEADLESS-1 >/dev/null
    hyprctl keyword monitor "HEADLESS-1,''${SANDBOX_WIDTH}x''${SANDBOX_HEIGHT}@60,0x0,1" >/dev/null
    for _ in $(seq 50); do
      hyprctl -j monitors | grep -q '"HEADLESS-1"' && break
      sleep 0.1
    done
    hyprctl -j monitors | grep -q '"HEADLESS-1"' || fail "no headless output"
    cp "$XDG_RUNTIME_DIR"/hypr/*/hyprland.log "$logs/"
    renderer=$(grep -a -m1 'Renderer:' "$logs/hyprland.log" | sed 's/.*Renderer: //')
    echo "hyprland-sandbox: Hyprland $(hyprctl version | sed -n 's/^Hyprland \([^ ]*\).*/\1/p'), renderer: $renderer" >&2
    case "$renderer" in
      "" | *llvmpipe* | *softpipe*)
        [ -n "''${SANDBOX_ALLOW_SOFTWARE:-}" ] || fail "not rendering on the GPU ($renderer)" ;;
    esac

    # Hyprland's own socket is the newest wayland-N; labwc has the first.
    export WAYLAND_DISPLAY=$(ls "$XDG_RUNTIME_DIR" | grep -E '^wayland-[0-9]+$' | sort -t- -k2 -n | tail -1)
    export XDG_CURRENT_DESKTOP=Hyprland XDG_SESSION_TYPE=wayland
    unset WLR_BACKENDS WLR_RENDERER WLR_LIBINPUT_NO_DEVICES WLR_RENDER_DRM_DEVICE

    mkfifo "$XDG_RUNTIME_DIR/vpointer.in" "$XDG_RUNTIME_DIR/vpointer.out"
    exec 8<>"$XDG_RUNTIME_DIR/vpointer.in" 9<>"$XDG_RUNTIME_DIR/vpointer.out"
    vpointer <&8 >&9 2>"$logs/vpointer.log" &
    sandbox-pointer extent "$SANDBOX_WIDTH" "$SANDBOX_HEIGHT"

    waybar -c ${waybarConfig} -s ${waybarStyle} > "$logs/waybar.log" 2>&1 &
    for _ in $(seq 100); do
      dbus-send --session --print-reply --dest=org.freedesktop.DBus / org.freedesktop.DBus.NameHasOwner \
        string:org.kde.StatusNotifierWatcher 2>/dev/null | grep -q 'boolean true' && break
      sleep 0.1
    done
    sleep 0.5
    hyprctl dismissnotify >/dev/null

    if [ "$#" -eq 0 ]; then set -- hyprland-sandbox-demo; fi
    "$@"
    status=$?
    exit $status
  '';
in
pkgs.writeShellApplication {
  name = "hyprland-sandbox";
  runtimeInputs = [
    pkgs.bubblewrap
    pkgs.coreutils
  ];
  passthru = { inherit vpointer tools session; };
  meta.description = "Run Hyprland and Waybar headless on the GPU render node in a rootless sandbox";
  text = ''
    usage() {
      cat <<'EOF'
    Usage: nix run .#hyprland-sandbox -- [OPTIONS] [--] [COMMAND [ARG...]]

    Starts Hyprland headless on the GPU render node with Waybar (tray enabled)
    in a rootless bubblewrap sandbox, runs COMMAND inside it, then stops
    everything. Without COMMAND it runs hyprland-sandbox-demo: a CopyQ status
    icon in the tray, its menu opened with a right click, and typing in foot.

      --out DIR          screenshots and logs (default ./hyprland-sandbox-out)
      --size WxH         headless output size (default 1280x720)
      --bind SRC[:DST]   bind a host path read-write (DST defaults to SRC)
      --ro-bind SRC[:DST] bind a host path read-only
      --timeout SECONDS  stop the whole sandbox after this long (default 300)
      --render-node DEV  default /dev/dri/renderD128

    Inside, COMMAND sees WAYLAND_DISPLAY, HYPRLAND_INSTANCE_SIGNATURE and a
    private DBUS_SESSION_BUS_ADDRESS, with SANDBOX_OUT=/out. Helpers on PATH:
      sandbox-shot NAME.png            screenshot into the output directory
      sandbox-pointer move X Y | click left|right|middle | down B | up B | scroll N
      sandbox-wait-sni [COUNT] [SECS]  wait for tray items; sandbox-sni-items lists them
      sandbox-wait-client CLASS [SECS] wait for a window
      hyprctl, wtype, grim, dbus-send, foot, copyq
    Nix store paths are visible read-only; nothing else from the host is,
    unless bound. There is no network.
    EOF
    }

    out=$PWD/hyprland-sandbox-out
    size=1280x720
    timeout=300
    node=/dev/dri/renderD128
    binds=()
    while [ "$#" -gt 0 ]; do
      case "$1" in
        --out) out=$2; shift 2 ;;
        --size) size=$2; shift 2 ;;
        --timeout) timeout=$2; shift 2 ;;
        --render-node) node=$2; shift 2 ;;
        --bind | --ro-bind)
          src=''${2%%:*} dst=''${2#*:}
          [ "$dst" = "$2" ] && dst=$src
          binds+=("$1" "$(realpath -e "$src")" "$dst")
          shift 2 ;;
        -h | --help) usage; exit 0 ;;
        --) shift; break ;;
        -*) echo "hyprland-sandbox: unknown option $1" >&2; usage >&2; exit 2 ;;
        *) break ;;
      esac
    done

    case "$size" in
      [0-9]*x[0-9]*) width=''${size%x*} height=''${size#*x} ;;
      *) echo "hyprland-sandbox: --size must be WxH" >&2; exit 2 ;;
    esac
    case "$(basename "$node")" in
      renderD*) ;;
      *) echo "hyprland-sandbox: $node is not a render node; card nodes are never passed in" >&2; exit 2 ;;
    esac
    if [ ! -r "$node" ] || [ ! -w "$node" ]; then
      echo "hyprland-sandbox: cannot open $node read-write" >&2
      exit 1
    fi

    mkdir -p "$out"
    out=$(realpath "$out")
    etc=$(mktemp -d "''${TMPDIR:-/tmp}/hyprland-sandbox.XXXXXX")
    trap 'rm -rf "$etc"' EXIT
    uid=$(id -u) gid=$(id -g)
    echo "sandbox:x:$uid:$gid::/home/sandbox:/bin/sh" > "$etc/passwd"
    echo "sandbox:x:$gid:" > "$etc/group"

    tools=${tools}
    status=0
    timeout --kill-after=10 "$timeout" \
      bwrap \
        --unshare-all --die-with-parent --new-session --clearenv \
        --ro-bind /nix/store /nix/store \
        --ro-bind /sys /sys \
        --proc /proc \
        --dev /dev \
        --dev-bind "$node" "$node" \
        --tmpfs /tmp \
        --tmpfs /run \
        --symlink ${pkgs.mesa} /run/opengl-driver \
        --dir "/run/user/$uid" --chmod 0700 "/run/user/$uid" \
        --tmpfs /home/sandbox \
        --ro-bind "$etc/passwd" /etc/passwd \
        --ro-bind "$etc/group" /etc/group \
        --symlink "$tools/bin/sh" /bin/sh \
        --symlink "$tools/bin/env" /usr/bin/env \
        --bind "$out" /out \
        "''${binds[@]}" \
        --setenv HOME /home/sandbox \
        --setenv USER sandbox \
        --setenv LANG C.UTF-8 \
        --setenv PATH "$tools/bin" \
        --setenv XDG_RUNTIME_DIR "/run/user/$uid" \
        --setenv XDG_DATA_DIRS "$tools/share" \
        --setenv XCURSOR_PATH "$tools/share/icons" \
        --setenv FONTCONFIG_FILE ${fontsConf} \
        --setenv SANDBOX_OUT /out \
        --setenv SANDBOX_LOGS /out/logs \
        --setenv SANDBOX_RENDER_NODE "$node" \
        --setenv SANDBOX_WIDTH "$width" \
        --setenv SANDBOX_HEIGHT "$height" \
        "$tools/bin/dbus-run-session" --config-file="$tools/share/dbus-1/session.conf" -- \
        ${session} "$@" || status=$?
    [ "$status" -eq 124 ] && echo "hyprland-sandbox: stopped after ''${timeout}s" >&2
    echo "hyprland-sandbox: output in $out" >&2
    exit "$status"
  '';
}
