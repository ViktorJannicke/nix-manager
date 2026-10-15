# The Runic port: the backend and command line (W300-029), the dashboard Window on Runic Desktop (W300-030) and
# the daemon with its status icon (W300-031), built in the Nix sandbox with the Runic SDK's buildRunicApplication
# from the committed NuGet and Bun lock files, and tested there. The package is complete for the NixOS module
# (W300-032): the program, its Polkit policy, a desktop entry with its icon, and shell completions.
{
  lib,
  stdenv,
  fetchzip,
  makeDesktopItem,
  dotnetCorePackages,
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
  # The NuGet lock files have a section for each of these; see RuntimeIdentifiers in NixManager.csproj.
  runtimeId =
    {
      x86_64-linux = "linux-x64";
      aarch64-linux = "linux-arm64";
    }
    .${stdenv.hostPlatform.system};
  # The Bun version's entry, which launches the same commands. StartupNotify lets the desktop pass an activation
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
  # The Nix grammar the Bun version uses, built as a native library, and the tree-sitter runtime (NixSyntax).
  parserEnvironment = {
    NIX_MANAGER_NIX_PARSER = "${tree-sitter.builtGrammars.tree-sitter-nix}/parser";
    NIX_MANAGER_TREE_SITTER = "${tree-sitter}/lib/libtree-sitter.so";
  };
  # The version in .config/dotnet-tools.json.
  translationsTool = fetchzip {
    url = "https://api.nuget.org/v3-flatcontainer/dotnet-runic-translations/0.7.0-preview.2/dotnet-runic-translations.0.7.0-preview.2.nupkg";
    extension = "zip";
    stripRoot = false;
    hash = "sha256-u8YDxukErTJZHNNPxrEXLA3JzsCa9uXBCIifx7UsHNA=";
  };
in
buildRunicApplication {
  pname = "nix-manager-runic";
  version = "1.0.0";
  src = lib.fileset.toSource {
    root = ../.;
    fileset = lib.fileset.unions [
      (lib.fileset.difference ./. (
        lib.fileset.unions [
          ./package.nix
          # Local tools are for development; the build needs none of them.
          (lib.fileset.maybeMissing ./.config)
          # Documentation, such as COMPARISON.md.
          (lib.fileset.fileFilter (file: file.hasExt "md") ./.)
        ]
      ))
      # The Bun version's Nix error fixtures, which the explanation tests read.
      ../test/fixtures/nix-errors
    ];
  };
  projectFile = "runic/NixManager.csproj";
  testProjectFile = "runic/NixManager.Tests/NixManager.Tests.csproj";
  # One lock file per runtime and project; see NixManager.csproj.
  nugetLockFiles =
    {
      linux-x64 = [
        ./packages.lock.json
        ./NixManager.Tests/packages.lock.json
      ];
      linux-arm64 = [
        ./packages.linux-arm64.lock.json
        ./NixManager.Tests/packages.linux-arm64.lock.json
      ];
    }
    .${runtimeId};
  frontend = {
    packageManager = "bun";
    # The hash of the frontend's node_modules; update both with Frontend/bun.lock. Bun installs only the
    # system's optional packages, so they differ. The aarch64-linux one was computed on x86_64-linux with
    # `bun install --frozen-lockfile --ignore-scripts --backend=copyfile --os=linux --cpu=arm64` (the same
    # command with --cpu=x64 reproduces the x86_64-linux hash), not yet by a build on aarch64-linux.
    hash =
      {
        x86_64-linux = "sha256-mjzmZr8NYDcoPqaLsMx/RrqKuz2vt05q6whM4lagspo=";
        aarch64-linux = "sha256-Ah34iATxYcCK9qhJP6zdQv+IanTTUdmpZETKFppAQ1k=";
      }
      .${stdenv.hostPlatform.system};
  };
  host = "desktop";
  # The frontend's Vite build compiles translations with the project-local .NET tool, which the sandbox cannot
  # restore; vite.config.ts runs this copy of the same version instead.
  preBuild = ''
    export NIX_MANAGER_TRANSLATIONS_TOOL=${translationsTool}/tools/net10.0/any/dotnet-runic-translations.dll
  '';
  executables = [ "nix-manager" ];
  desktopItems = [ desktop ];
  icons.scalable = ./Frontend/public/nix-manager.svg;
  iconName = "nix-manager";
  # The completion scripts the command line generates for its own catalog, in the folders NixOS links from
  # installed packages (share/bash-completion, share/zsh, share/fish). The program runs without its wrapper: it
  # needs only DOTNET_ROOT (the builder's default runtime) for this, and no state or display.
  postInstall = ''
    completions=$(mktemp -d)
    for shell in bash zsh fish; do
      HOME=$completions XDG_STATE_HOME=$completions XDG_CONFIG_HOME=$completions DOTNET_ROOT=${dotnetCorePackages.sdk_10_0.aspnetcore}/share/dotnet \
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 NIX_MANAGER_DAEMON=0 \
        "$out/lib/$pname/nix-manager" --completions "$shell" > "$completions/nix-manager.$shell"
      [[ -s $completions/nix-manager.$shell ]]
    done
    install -Dm644 "$completions/nix-manager.bash" $out/share/bash-completion/completions/nix-manager
    install -Dm644 "$completions/nix-manager.zsh" $out/share/zsh/site-functions/_nix-manager
    install -Dm644 "$completions/nix-manager.fish" $out/share/fish/vendor_completions.d/nix-manager.fish
    rm -r "$completions"
  '';
  doCheck = true;
  # The change history tests commit in temporary Git repositories.
  nativeCheckInputs = [ git ];
  # Microsoft.Testing.Platform runs the test application itself; dotnet test's VSTest flags do not apply.
  checkPhase = ''
    runHook preCheck
    export NIX_MANAGER_NIX_PARSER=${parserEnvironment.NIX_MANAGER_NIX_PARSER}
    export NIX_MANAGER_TREE_SITTER=${parserEnvironment.NIX_MANAGER_TREE_SITTER}
    dotnet runic/NixManager.Tests/bin/Release/net10.0/${runtimeId}/NixManager.Tests.dll --no-progress --no-ansi
    runHook postCheck
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
      # `ui --browser` opens the served dashboard with xdg-open, as the Bun version does.
      xdg-utils
      nix
      homeManager
      git
    ])
  ];
  meta = {
    description = "Nix Manager's backend, command line, dashboard and tray on Runic";
    mainProgram = "nix-manager";
    platforms = [
      "x86_64-linux"
      "aarch64-linux"
    ];
  };
}
