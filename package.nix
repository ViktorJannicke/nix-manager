{
  lib,
  buildNpmPackage,
  bun,
  dbus,
  makeWrapper,
  makeDesktopItem,
  callPackage,
  coreutils,
  nix,
  homeManager,
  nixGrammar,
  git,
  diffutils,
  xdg-utils,
}:
let
  webui = callPackage ./webui.nix { };
  desktop = makeDesktopItem {
    name = "nix-manager";
    desktopName = "Nix Manager";
    genericName = "System and package updates";
    comment = "Manage NixOS and Home Manager configurations";
    exec = "nix-manager ui";
    icon = "nix-manager";
    categories = [
      "System"
      "PackageManager"
    ];
    terminal = false;
    actions.Tray = {
      name = "Start in system tray";
      exec = "nix-manager tray";
    };
  };
in
buildNpmPackage {
  pname = "nix-manager";
  version = "1.0.0";
  src = lib.fileset.toSource {
    root = ./.;
    fileset = lib.fileset.unions [
      ./package.json
      ./package-lock.json
      ./bun.lock
      ./tsconfig.json
      ./tsconfig.ui.json
      ./svelte.config.js
      ./vite.config.ts
      ./components.json
      ./build.ts
      ./src
      ./ui
      ./test
    ];
  };
  npmDepsHash = "sha256-hMfYWE+M4ha4G+icDFjZ7DnEgMJD2/33dvkwaIpCoZg=";
  npmFlags = [ "--ignore-scripts" ];
  nativeBuildInputs = [
    bun
    dbus
    git
    makeWrapper
  ];
  doCheck = true;
  checkPhase = ''
    runHook preCheck
    export NIX_MANAGER_WEBUI_LIBRARY=${webui}/lib/libwebui-2.so
    export NIX_MANAGER_NIX_GRAMMAR=${nixGrammar}/parser.wasm
    npm run typecheck
    bun test
    runHook postCheck
  '';
  installPhase = ''
    runHook preInstall
    mkdir -p $out/share/nix-manager $out/bin
    cp dist/main.js $out/share/nix-manager/main.js
    # dbus-next loads optional adapters dynamically; retain its locked modules.
    npm prune --omit=dev --ignore-scripts
    cp -r node_modules $out/share/nix-manager/node_modules
    makeWrapper ${lib.getExe bun} $out/bin/nix-manager \
      --add-flags "$out/share/nix-manager/main.js" \
      --set NIX_MANAGER_EXECUTABLE "$out/bin/nix-manager" \
      --set NIX_MANAGER_WEBUI_LIBRARY ${webui}/lib/libwebui-2.so \
      --set NIX_MANAGER_NIX_GRAMMAR ${nixGrammar}/parser.wasm \
      --prefix PATH : ${
        lib.makeBinPath [
          coreutils
          nix
          homeManager
          git
          diffutils
          xdg-utils
        ]
      }
    cp -r ${desktop}/share/applications $out/share/
    install -Dm644 ui/public/nix-manager.svg $out/share/icons/hicolor/scalable/apps/nix-manager.svg
    runHook postInstall
  '';
  passthru = { inherit webui nixGrammar; };
  meta = {
    description = "Bun and Effect application for NixOS and Home Manager, with CLI, WebUI and system tray";
    homepage = "https://github.com/ViktorJannicke/nix-manager";
    mainProgram = "nix-manager";
    platforms = lib.platforms.linux;
  };
}
