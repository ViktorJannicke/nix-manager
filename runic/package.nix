# Nix Manager: the backend and command line, the dashboard Window on Runic Desktop and the daemon with its status
# icon, built in the Nix sandbox with the Runic SDK's buildRunicApplication from the committed NuGet and Bun lock
# files, and tested there. The package is complete for the NixOS module: the program, its Polkit policy, a
# desktop entry with its icon, and shell completions. The program is published with NativeAOT: the tray shows its
# icon and the command line answers without starting a runtime or compiling.
{
  lib,
  fetchzip,
  makeDesktopItem,
  buildRunicApplication,
  coreutils,
  diffutils,
  xdg-utils,
  nix,
  homeManager,
  git,
  tree-sitter,
}:
let
  # StartupNotify lets the desktop pass an activation
  # token (XDG_ACTIVATION_TOKEN), which `nix-manager ui` forwards to the running daemon, so the Window it raises
  # gets focus.
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
    startupNotify = true;
    actions.Tray = {
      name = "Start in system tray";
      exec = "nix-manager tray";
    };
  };
  # tree-sitter's Nix grammar, built as a native library, and the tree-sitter runtime (NixSyntax).
  parserEnvironment = {
    NIX_MANAGER_NIX_PARSER = "${tree-sitter.builtGrammars.tree-sitter-nix}/parser";
    NIX_MANAGER_TREE_SITTER = "${tree-sitter}/lib/libtree-sitter.so";
  };
  # The Translations compiler of .config/dotnet-tools.json, which the frontend's Vite build runs. The builder's
  # dotnetTools would restore it offline, but it reads the manifest from the filtered src at evaluation, which
  # `nix flake check --no-build` cannot (Runic-Artifex/runic-sdk#371); vite.config.ts runs this copy instead.
  translationsTool = fetchzip {
    url = "https://api.nuget.org/v3-flatcontainer/dotnet-runic-translations/0.7.0-preview.3/dotnet-runic-translations.0.7.0-preview.3.nupkg";
    extension = "zip";
    stripRoot = false;
    hash = "sha256-bOIKeVkJjVe34VB2ZM8O6fjNr2u1aG49tsoNo84rr/w=";
  };
in
buildRunicApplication {
  pname = "nix-manager";
  version = "1.0.0";
  src = lib.fileset.toSource {
    root = ../.;
    fileset = lib.fileset.unions [
      (lib.fileset.difference ./. (
        lib.fileset.unions [
          ./package.nix
          # Local tools are for development; the build runs translationsTool.
          (lib.fileset.maybeMissing ./.config)
          # Documentation.
          (lib.fileset.fileFilter (file: file.hasExt "md") ./.)
        ]
      ))
      # Nix error output, which the explanation tests read.
      ../test/fixtures/nix-errors
    ];
  };
  projectFile = "runic/NixManager.csproj";
  testProjectFile = "runic/NixManager.Tests/NixManager.Tests.csproj";
  # One lock file per runtime and project; see NixManager.csproj. The application restores with PublishAot, the
  # tests without.
  nugetLockFiles = {
    linux-x64 = [
      ./packages.aot.lock.json
      ./NixManager.Tests/packages.lock.json
    ];
    linux-arm64 = [
      ./packages.linux-arm64.aot.lock.json
      ./NixManager.Tests/packages.linux-arm64.lock.json
    ];
  };
  # One native executable without the .NET runtime: no JIT at start-up, and the ASP.NET Core runtime leaves the
  # closure. The tests still run under the JIT.
  nativeAot = true;
  frontend = {
    packageManager = "bun";
    # The hash of the frontend's node_modules for each system; update both with Frontend/bun.lock (the build fails
    # when they are out of date). Bun installs only the system's optional packages, so they differ; `nix build
    # .#default.frontendNodeModules.fetchedFor.aarch64-linux` computes the aarch64-linux one on any system.
    hash = {
      x86_64-linux = "sha256-lmXlaTaJYgGjKJexg34GZvCfljFTyc8nBrgc2hOQgb4=";
      aarch64-linux = "sha256-QTEYl9rq0R5RGb/UFWGzNKznwd2I+gQzjV2ivpb9ypY=";
    };
  };
  host = "desktop";
  preBuild = ''
    export NIX_MANAGER_TRANSLATIONS_TOOL=${translationsTool}/tools/net10.0/any/dotnet-runic-translations.dll
  '';
  executables = [ "nix-manager" ];
  desktopItems = [ desktop ];
  icons.scalable = ./Frontend/public/nix-manager.svg;
  iconName = "nix-manager";
  # The completion scripts the command line generates for its own catalog (`--completions SHELL`, the Bun
  # version's option), in the folders NixOS links from installed packages (share/bash-completion, share/zsh,
  # share/fish).
  completions.command = [ "--completions" ];
  doCheck = true;
  # TUnit's tests are a Microsoft.Testing.Platform application, which the builder runs with these options.
  testRunner = "Microsoft.Testing.Platform";
  dotnetTestFlags = [
    "--no-progress"
    "--no-ansi"
  ];
  # The change history tests commit in temporary Git repositories.
  nativeCheckInputs = [ git ];
  # The tests read Nix files with the same grammar as the program.
  preCheck = ''
    export NIX_MANAGER_NIX_PARSER=${parserEnvironment.NIX_MANAGER_NIX_PARSER}
    export NIX_MANAGER_TREE_SITTER=${parserEnvironment.NIX_MANAGER_TREE_SITTER}
  '';
  makeWrapperArgs = [
    "--set-default"
    "NIX_MANAGER_NIX_PARSER"
    parserEnvironment.NIX_MANAGER_NIX_PARSER
    "--set-default"
    "NIX_MANAGER_TREE_SITTER"
    parserEnvironment.NIX_MANAGER_TREE_SITTER
    "--prefix"
    "PATH"
    ":"
    (lib.makeBinPath [
      coreutils
      # Package updates show the recipe they change with diff -u (PackageUpdater).
      diffutils
      # `ui --browser` opens the served dashboard with xdg-open.
      xdg-utils
      nix
      homeManager
      git
    ])
  ];
  meta = {
    description = "Manage NixOS and Home Manager configurations: command line, dashboard and tray";
    mainProgram = "nix-manager";
    platforms = [
      "x86_64-linux"
      "aarch64-linux"
    ];
  };
}
