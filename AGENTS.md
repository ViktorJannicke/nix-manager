# Development

Read `flake.nix` and `README.md` before choosing build commands. Use the locked
development environment with `nix develop`; do not install a second toolchain.
Keep dependency versions and both JavaScript lock files in sync. This extraction
preserves the existing Effect 3 dependency set.

Run `nix flake check` before publishing. Package checks include type checking,
Bun tests, native WebUI and D-Bus tests, and packaged CLI regression tests.

# Learning more about Effect

This repository uses the Effect Typescript library.

Before writing any Effect code, first read `node_modules/effect/AGENTS.md`
**completely**, and follow the links in the file when required.

If you need to learn more about particular Effect APIs and concepts that the
guide doesn't cover, search through the source code in `node_modules/effect/src`.
