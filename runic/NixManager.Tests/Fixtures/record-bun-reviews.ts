/**
 * Records review records and undo contents the Bun version writes (Fixtures/bun-reviews), so the Runic port is
 * tested against them: an edited review, its applied operation with the .undo contents, the undo review of that
 * change, a generation review, and a review kept for the next boot.
 *
 * Run from the repository root in the development shell, with the Bun dependencies installed:
 *
 *     nix develop -c bun runic/NixManager.Tests/Fixtures/record-bun-reviews.ts
 *
 * Every command runs through a fake runner in a temporary checkout; nothing is built or activated. The checkout
 * path is written as /home/user/my-flakes.
 */
import { cpSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Effect } from "effect";
import { request, type Command, type Config } from "../../../src/domain";
import { writeDraft } from "../../../src/draft";
import { Manager } from "../../../src/manager";
import { runProcess } from "../../../src/process";

const recorded = "/home/user/my-flakes";
const target = join(import.meta.dir, "bun-reviews");

const flake = realpathSync(mkdtempSync(join(tmpdir(), "nix-manager-reviews-")));
try {
  writeFileSync(join(flake, "flake.nix"), "{}");
  writeFileSync(join(flake, "flake.lock"), '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a","lastModified":1}}}}');
  mkdirSync(join(flake, "modules"));
  writeFileSync(join(flake, "modules/apps.nix"), "{ pkgs, ... }: {\n  home.packages = with pkgs; [\n    git\n  ];\n}\n");
  const stateDir = join(flake, "state");
  const homeProfile = join(stateDir, "nix/profiles/home-manager");
  mkdirSync(join(stateDir, "nix/profiles"), { recursive: true });
  // The home profile points at generation 5 before anything is applied.
  symlinkSync("home-manager-5-link", homeProfile);
  const config: Config = {
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
  const snapshot = join(flake, "snapshot");
  cpSync(join(flake, "modules"), join(snapshot, "modules"), { recursive: true });
  writeFileSync(join(snapshot, "flake.nix"), "{}");
  const outputs = { home: join(flake, "home-output"), system: join(flake, "system-output") };
  mkdirSync(outputs.home);
  mkdirSync(outputs.system);
  writeFileSync(join(outputs.system, "nixos-version"), "26.11.20261014.55bfb4d\n");
  symlinkSync("/nix/store/00000000000000000000000000000000-linux-7.2.10/bzImage", join(outputs.system, "kernel"));
  const package_ = (name: string) => ({ out: `/nix/store/${name}`, name, version: "1", description: null });
  const manager = new Manager(config, undefined, (command: Command) => {
    const [program, verb, sub] = command.argv;
    if (program === "cp") return runProcess(command);
    return Effect.sync(() => {
      if (verb === "flake" && sub === "metadata") {
        const ref = command.argv[4];
        return { code: 0, stdout: JSON.stringify({ path: ref === flake ? snapshot : ref.slice(5) }), stderr: "" };
      }
      if (verb === "eval") {
        const source = command.argv[3].slice(5).split("#")[0];
        const items = source === snapshot ? [package_("git")] : [package_("git"), package_("btop")];
        return {
          code: 0,
          stdout: JSON.stringify({
            packages: [{ file: `${source}/modules/apps.nix`, items }],
            options: [{ exists: true, value: source !== snapshot, files: source === snapshot ? [] : [`${source}/modules/apps.nix`] }],
            resolved: ["/nix/store/btop"],
            used: [],
          }),
          stderr: "",
        };
      }
      if (command.argv.includes("--output-lock-file"))
        writeFileSync(
          command.argv[command.argv.indexOf("--output-lock-file") + 1],
          '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"b","lastModified":2,"type":"github","owner":"NixOS","repo":"nixpkgs"}}}}',
        );
      if (command.argv.includes("--out-link"))
        symlinkSync(
          command.label.includes("system") ? outputs.system : outputs.home,
          command.argv[command.argv.indexOf("--out-link") + 1],
        );
      if (command.argv[1] === "path-info")
        return { code: 0, stdout: JSON.stringify({ [command.argv[4]]: { closureSize: 123456789 } }), stderr: "" };
      if (command.argv[2] === "diff-closures")
        return { code: 0, stdout: "btop: ∅ → 1, +1.5 MiB\ngit: 2.50 → 2.51, -12.0 KiB\n", stderr: "" };
      return { code: 0, stdout: "", stderr: "" };
    });
  });
  const run = async (r: Parameters<typeof request>[0]) => {
    manager.start(request(r));
    const operation = (await manager.wait())!;
    if (operation.status !== "succeeded") throw new Error(`${r?.action} failed: ${operation.error}`);
    return operation;
  };
  const save = (name: string, file: string, text: string) => {
    mkdirSync(join(target, name), { recursive: true });
    writeFileSync(join(target, name, file), text.replaceAll(flake, recorded));
  };
  const review = (id: string) => readFileSync(join(stateDir, "reviews", id, "review.json"), "utf8");

  rmSync(target, { recursive: true, force: true });
  writeDraft(config, [
    { kind: "add-package", scope: "home", file: "modules/apps.nix", attr: "pkgs.btop" },
    { kind: "set-option", scope: "home", file: "modules/apps.nix", option: "programs.git.enable", value: true },
  ]);
  await run({ action: "review-build", reviewId: "e0e0e0e0-0000-4000-8000-000000000001", scope: "home", edits: true });
  save("edits", "review.json", review("e0e0e0e0-0000-4000-8000-000000000001"));

  const applied = await run({ action: "review-apply", reviewId: "e0e0e0e0-0000-4000-8000-000000000001", scope: "home" });
  const operations = join(stateDir, "operations");
  save("applied", `${applied.id}.json`, readFileSync(join(operations, `${applied.id}.json`), "utf8"));
  save("applied", `${applied.id}.undo`, readFileSync(join(operations, `${applied.id}.undo`), "utf8"));

  await run({ action: "review-build", reviewId: "b0b0b0b0-0000-4000-8000-000000000002", scope: "home", undo: applied.id });
  save("undo", "review.json", review("b0b0b0b0-0000-4000-8000-000000000002"));

  await run({ action: "review-build", reviewId: "c0c0c0c0-0000-4000-8000-000000000003", scope: "home", generation: 5 });
  save("generation", "review.json", review("c0c0c0c0-0000-4000-8000-000000000003"));

  await run({ action: "review-build", reviewId: "a0a0a0a0-0000-4000-8000-000000000004", inputs: ["nixpkgs"] });
  save("inputs", "review.json", review("a0a0a0a0-0000-4000-8000-000000000004"));
  await run({ action: "review-apply", reviewId: "a0a0a0a0-0000-4000-8000-000000000004", scope: "all", activation: "boot" });
  save("after-boot", "review.json", review("a0a0a0a0-0000-4000-8000-000000000004"));
  console.log(`Recorded the Bun version's reviews into ${target}.`);
} finally {
  rmSync(flake, { recursive: true, force: true });
}
