import { afterAll, afterEach, beforeAll, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Effect } from "effect";
import { request, type Command } from "../src/domain";
import { changeMessage, readHistory, recordChange } from "../src/history";
import { Manager } from "../src/manager";
import { writePreferences } from "../src/preferences";
import { runnerLayer } from "../src/process";
import { fixture } from "./helpers";

// Neither the global nor the system Git configuration, such as commit signing, applies here.
const saved = { global: process.env.GIT_CONFIG_GLOBAL, system: process.env.GIT_CONFIG_NOSYSTEM };
beforeAll(() => {
  process.env.GIT_CONFIG_GLOBAL = "/dev/null";
  process.env.GIT_CONFIG_NOSYSTEM = "1";
});
afterAll(() => {
  if (saved.global === undefined) delete process.env.GIT_CONFIG_GLOBAL;
  else process.env.GIT_CONFIG_GLOBAL = saved.global;
  if (saved.system === undefined) delete process.env.GIT_CONFIG_NOSYSTEM;
  else process.env.GIT_CONFIG_NOSYSTEM = saved.system;
});
const cleanups: (() => void)[] = [];
afterEach(() => cleanups.splice(0).forEach((f) => f()));
const setup = () => {
  const config = fixture();
  cleanups.push(() => rmSync(config.flake, { recursive: true, force: true }));
  return config;
};
const git = (cwd: string, ...args: string[]) => {
  const result = Bun.spawnSync(["git", "-C", cwd, ...args], { stderr: "pipe" });
  if (result.exitCode !== 0) throw new Error(result.stderr.toString());
  return result.stdout.toString().trim();
};
const record = (config: ReturnType<typeof setup>, paths: string[], changes: string[], backup = false) =>
  Effect.runPromise(recordChange(config, paths, changes, backup).pipe(Effect.provide(runnerLayer())));

test("a message names the first change and lists all of them", () => {
  expect(changeMessage(["Install steam"])).toBe("Install steam");
  expect(changeMessage(["Install steam", "Bluetooth: hardware.bluetooth.enable true"])).toBe(
    "Install steam and 1 more\n\n- Install steam\n- Bluetooth: hardware.bluetooth.enable true",
  );
});

test("an applied change is committed alone, with a readable message, and backed up", async () => {
  const config = setup();
  expect(await record(config, ["apps.nix"], ["Install steam"])).toContain("not in a Git repository");

  git(config.flake, "init", "--quiet", "--initial-branch=main");
  git(config.flake, "-c", "user.name=Someone", "-c", "user.email=someone@example.org", "commit", "--quiet", "--allow-empty", "-m", "Start");
  writeFileSync(join(config.flake, "apps.nix"), "{ }\n");
  writeFileSync(join(config.flake, "notes.txt"), "work in progress\n");
  // Without a Git identity the commit is made in Nix Manager's name.
  expect(await record(config, ["apps.nix"], ["Install steam", "Remove vlc"])).toBe('Kept "Install steam and 1 more" in the history.');
  expect(git(config.flake, "show", "--name-only", "--format=%an", "HEAD").split("\n")).toEqual(["Nix Manager", "", "apps.nix"]);
  // Other work stays uncommitted.
  expect(git(config.flake, "status", "--porcelain")).toBe("?? flake.nix\n?? notes.txt\n?? state/");
  const [latest, first] = await readHistory(config.flake);
  expect(latest).toMatchObject({ subject: "Install steam and 1 more", body: "- Install steam\n- Remove vlc", author: "Nix Manager", applied: true });
  expect(first).toMatchObject({ subject: "Start", author: "Someone", applied: false });

  // Files the change left as they were need no commit.
  expect(await record(config, ["apps.nix"], ["Install steam"])).toBeUndefined();

  writeFileSync(join(config.flake, "apps.nix"), "{ programs.steam.enable = true; }\n");
  expect(await record(config, ["apps.nix"], ["Install steam"], true)).toContain("the branch has no remote");
  const remote = mkdtempSync(join(tmpdir(), "nm-remote-"));
  cleanups.push(() => rmSync(remote, { recursive: true, force: true }));
  git(remote, "init", "--quiet", "--bare");
  git(config.flake, "remote", "add", "origin", remote);
  git(config.flake, "push", "--quiet", "--set-upstream", "origin", "main");
  writeFileSync(join(config.flake, "apps.nix"), "{ }\n");
  expect(await record(config, ["apps.nix"], ["Remove steam"], true)).toBe('Kept "Remove steam" in the history. Backed up to origin/main.');
  expect(git(remote, "log", "-1", "--format=%s", "main")).toBe("Remove steam");
});

test("applying a review keeps its files in the history when that is turned on", async () => {
  const config = setup();
  writeFileSync(join(config.flake, "flake.lock"), '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a"}}}}');
  mkdirSync(join(config.flake, "home-output"));
  const calls: Command[] = [];
  const manager = new Manager(config, undefined, (command) =>
    Effect.sync(() => {
      calls.push(command);
      if (command.argv.includes("--output-lock-file"))
        writeFileSync(command.argv[command.argv.indexOf("--output-lock-file") + 1], '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"b"}}}}');
      if (command.argv.includes("--out-link"))
        symlinkSync(join(config.flake, "home-output"), command.argv[command.argv.indexOf("--out-link") + 1]);
      // The staged lock differs from the last commit.
      if (command.argv[0] === "git" && command.argv[3] === "diff") return { code: 1, stdout: "", stderr: "" };
      return { code: 0, stdout: "", stderr: "" };
    }),
  );
  const apply = async (id: string) => {
    manager.start(request({ action: "review-build", reviewId: id, scope: "home", inputs: ["nixpkgs"] }));
    expect((await manager.wait())?.status).toBe("succeeded");
    calls.length = 0;
    manager.start(request({ action: "review-apply", reviewId: id, scope: "home" }));
    expect((await manager.wait())?.status).toBe("succeeded");
    return calls.filter((command) => command.argv[0] === "git").map((command) => command.argv.slice(3));
  };
  writePreferences(config, { detail: "detailed" });
  expect(await apply("aaaaaaaa")).toEqual([]);
  writePreferences(config, { history: true });
  writeFileSync(join(config.flake, "flake.lock"), '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a"}}}}');
  const commands = await apply("bbbbbbbb");
  expect(commands).toContainEqual(expect.arrayContaining(["commit", "--only", "Update nixpkgs", "--", "flake.lock"]));
});
