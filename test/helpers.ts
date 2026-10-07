import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { Config } from "../src/domain";

export function fixture(): Config {
  const flake = mkdtempSync(join(tmpdir(), "nix-manager-test-"));
  writeFileSync(join(flake, "flake.nix"), "{}");
  const stateDir = join(flake, "state"),
    homeProfile = join(stateDir, "nix/profiles/home-manager");
  mkdirSync(join(stateDir, "nix/profiles"), { recursive: true });
  writeFileSync(homeProfile, "");
  return {
    flake,
    stateDir,
    homeProfile,
    homeProfileExists: true,
    hostKey: "test",
    hosts: {
      test: {
        system: "x86_64-linux",
        userName: "test",
        nixosHostName: "test-machine",
        homeConfiguration: "user-test",
        nixosConfiguration: "test-machine",
      },
    },
    privileged: ["pkexec", "/immutable/bin/nix-manager", "--privileged"],
  };
}
