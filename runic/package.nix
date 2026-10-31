# Nix Manager: the backend and command line, the dashboard Window on Runic Desktop and the daemon with its status
# icon, built in the Nix sandbox with the Runic SDK's buildRunicApplication from the committed NuGet and Bun lock
# files, and tested there. The package is complete for the NixOS module: the program, its Polkit policy, a
# desktop entry with its icon, and shell completions. The program is published with NativeAOT: the tray shows its
# icon and the command line answers without starting a runtime or compiling.
{
  lib,
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
    # when they are out of date). Bun installs only the system's optional packages, so they differ; `nix run
    # .#default.fixedOutputHashes` prints both, and the tools' hashes below.
    hash = {
      x86_64-linux = "sha256-FpmTda37JrInRjXBmSf0ibbYYh81z9/6V0OQmdLrBtU=";
      aarch64-linux = "sha256-hZhlUHeouu4khsDvLBMUuuwQjYDJW14S8cCAILDbYOo=";
    };
  };
  host = "desktop";
  # The tools of .config/dotnet-tools.json: the frontend's Vite build runs the Translations compiler.
  dotnetTools.hashes = {
    dotnet-runic = "sha256-dph317p4TvE4aetyMv9avKwDoZWhhTblTqGtWO0yxQM=";
    dotnet-runic-translations = "sha256-rqTx4tM5QGkbR7psgzoU0agznrFVc0fe+IM5R1gmge0=";
  };
  executables = [ "nix-manager" ];
  desktopItems = [ desktop ];
  icons.scalable = ./Frontend/public/nix-manager.svg;
  iconName = "nix-manager";
  # The completion scripts the command line generates for its own catalog (`nix-manager completion SHELL`, the
  # builder's default command), in the folders NixOS links from installed packages (share/bash-completion,
  # share/zsh, share/fish).
  completions = { };
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
