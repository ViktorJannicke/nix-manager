# nix run .#compare -- [OUTPUT-DIRECTORY]
#   COMPARE_REPEAT (10), COMPARE_IDLE_SECONDS (30), COMPARE_LOAD_SECONDS (10)
#
# Measures the Runic port against the Bun version in the same way: command
# line start-up, the background process (the Bun tray, the port's daemon)
# until its status icon is registered, its memory after idling, its memory
# with the dashboard Window open, and the size of each package's closure.
# The processes run in nix/hyprland-sandbox: headless Hyprland and Waybar on
# the GPU render node, a private session bus and runtime directory, no
# network and no nix-daemon, with fresh state directories and a fixture
# checkout. Nothing opens on the host desktop, and a running Nix Manager is
# never reached. See runic/COMPARISON.md for the method and the results.
{
  pkgs,
  runic,
  bun,
  sandbox,
}:
let
  # The browser the Bun version's WebUI opens its Window in; the port's
  # Window is WebKitGTK, which its package brings.
  chromium = pkgs.chromium.override {
    commandLineArgs = [
      "--ozone-platform=wayland"
      "--no-first-run"
      "--password-store=basic"
      # bubblewrap's user namespace leaves Chromium no room for its own.
      "--no-sandbox"
    ];
  };
  python = pkgs.python3;
  # Runs in the sandbox, which clears the environment; the settings come as
  # NAME=VALUE arguments.
  inner = pkgs.writeShellScript "nix-manager-compare-inner" ''
    set -eu
    for setting in "$@"; do export "$setting"; done
    export PATH=${chromium}/bin:$PATH
    mkdir -p /out/logs
    ${python}/bin/python3 ${./measure.py} runic ${runic} /out/runic.json
    ${python}/bin/python3 ${./measure.py} bun ${bun} /out/bun.json
  '';
in
pkgs.writeShellApplication {
  name = "nix-manager-compare";
  runtimeInputs = [
    pkgs.coreutils
    pkgs.jq
    python
  ];
  passthru = { inherit chromium inner; };
  text = ''
    out=''${1:-/tmp/nix-manager-compare-$(date +%Y%m%d-%H%M%S)}
    mkdir -p "$out"
    out=$(realpath "$out")
    echo "Results go to $out." >&2

    # Package sizes: the package itself, its closure, and the closure's
    # largest members, from the store's own metadata (the host's nix).
    for name in runic bun; do
      case $name in runic) path=${runic} ;; bun) path=${bun} ;; esac
      nix path-info --json --json-format 1 --recursive --size --closure-size "$path" > "$out/$name-closure.json"
    done
    nix path-info --json --json-format 1 --recursive --size --closure-size ${chromium} > "$out/chromium-closure.json"

    ${sandbox}/bin/hyprland-sandbox --out "$out/sandbox" --timeout 900 -- ${inner} \
      "COMPARE_REPEAT=''${COMPARE_REPEAT:-10}" \
      "COMPARE_IDLE_SECONDS=''${COMPARE_IDLE_SECONDS:-30}" \
      "COMPARE_LOAD_SECONDS=''${COMPARE_LOAD_SECONDS:-10}"

    python3 ${./report.py} "$out" ${runic} ${bun} ${chromium} | tee "$out/report.md"
  '';
}
