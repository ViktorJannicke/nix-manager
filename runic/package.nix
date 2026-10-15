# The Runic port: the backend and command line (W300-029) and the dashboard Window on Runic Desktop (W300-030),
# built in the Nix sandbox with the Runic SDK's buildRunicApplication from the committed NuGet and Bun lock files,
# and tested there. Packaging for daily use, the NixOS module and the desktop entry follow in W300-032.
{
  lib,
  fetchzip,
  buildRunicApplication,
  coreutils,
  nix,
  homeManager,
  git,
  tree-sitter,
}:
let
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
        ]
      ))
      # The Bun version's Nix error fixtures, which the explanation tests read.
      ../test/fixtures/nix-errors
    ];
  };
  projectFile = "runic/NixManager.csproj";
  testProjectFile = "runic/NixManager.Tests/NixManager.Tests.csproj";
  nugetLockFiles = [
    ./packages.lock.json
    ./NixManager.Tests/packages.lock.json
  ];
  frontend = {
    packageManager = "bun";
    # The hash of the frontend's node_modules; update it with Frontend/bun.lock.
    hash = "sha256-mjzmZr8NYDcoPqaLsMx/RrqKuz2vt05q6whM4lagspo=";
  };
  host = "desktop";
  # The frontend's Vite build compiles translations with the project-local .NET tool, which the sandbox cannot
  # restore; vite.config.ts runs this copy of the same version instead.
  preBuild = ''
    export NIX_MANAGER_TRANSLATIONS_TOOL=${translationsTool}/tools/net10.0/any/dotnet-runic-translations.dll
  '';
  executables = [ "nix-manager" ];
  doCheck = true;
  # The change history tests commit in temporary Git repositories.
  nativeCheckInputs = [ git ];
  # Microsoft.Testing.Platform runs the test application itself; dotnet test's VSTest flags do not apply.
  checkPhase = ''
    runHook preCheck
    export NIX_MANAGER_NIX_PARSER=${parserEnvironment.NIX_MANAGER_NIX_PARSER}
    export NIX_MANAGER_TREE_SITTER=${parserEnvironment.NIX_MANAGER_TREE_SITTER}
    dotnet runic/NixManager.Tests/bin/Release/net10.0/linux-x64/NixManager.Tests.dll --no-progress --no-ansi
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
      nix
      homeManager
      git
    ])
  ];
  meta = {
    description = "Nix Manager's backend, command line and dashboard on Runic (port in progress)";
    mainProgram = "nix-manager";
    platforms = [ "x86_64-linux" ];
  };
}
