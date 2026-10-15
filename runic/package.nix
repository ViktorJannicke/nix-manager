# The Runic port (W300-029): the backend and command line, built in the Nix sandbox with the Runic SDK's
# buildRunicApplication from the committed NuGet lock files, and tested there. It has no Window yet, so no
# host; packaging for daily use, the NixOS module and the desktop entry follow in W300-032.
{
  lib,
  buildRunicApplication,
  coreutils,
  nix,
  homeManager,
  git,
}:
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
  host = null;
  executables = [ "nix-manager" ];
  doCheck = true;
  # Microsoft.Testing.Platform runs the test application itself; dotnet test's VSTest flags do not apply.
  checkPhase = ''
    runHook preCheck
    dotnet runic/NixManager.Tests/bin/Release/net10.0/linux-x64/NixManager.Tests.dll --no-progress --no-ansi
    runHook postCheck
  '';
  makeWrapperArgs = [
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
    description = "Nix Manager's backend and command line on Runic (port in progress)";
    mainProgram = "nix-manager";
    platforms = [ "x86_64-linux" ];
  };
}
