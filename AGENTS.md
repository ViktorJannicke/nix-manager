# Development

Read `flake.nix` and `README.md` before choosing build commands. Use the locked
development environment with `nix develop`; do not install a second toolchain.

The application lives in `runic/`: C# (`NixManager.csproj`, tests in
`NixManager.Tests`) on the Runic SDK, with the dashboard's Svelte frontend in
`runic/Frontend` (Bun, `bun.lock`). Keep the Runic package versions on one SDK
release, and keep `.config/dotnet-tools.json`, the NuGet lock files
(`packages*.lock.json`) and the frontend hashes in `runic/package.nix` in sync
with them.

Use the Runic packages as their documentation describes rather than working
around them. When an SDK package cannot do something nix-manager needs, report
it upstream (Runic-Artifex) instead of adding a local replacement.

Run `nix flake check` before publishing. It builds the package with its unit
tests, and runs the command line and daemon regression suites in `test/`, the
NixOS module checks and the Nix formatting check.
