import { afterEach, expect, test } from "bun:test";
import { cpSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { Effect } from "effect";
import { plan, request, reviewRoot, type Command } from "../src/domain";
import { diffLines, importPath, readDraft, renderDraft, writeDraft, type Change } from "../src/draft";
import { remaining, splitAttr, writeSources } from "../src/edits";
import type { ReviewEdits } from "../src/contracts";
import { Manager } from "../src/manager";
import { Runner, runProcess } from "../src/process";
import { listReviews } from "../src/reviews";
import { fixture } from "./helpers";

const cleanups: (() => void)[] = [];
afterEach(() => cleanups.splice(0).forEach((f) => f()));
const setup = () => {
  const config = fixture();
  cleanups.push(() => rmSync(config.flake, { recursive: true, force: true }));
  mkdirSync(join(config.flake, "modules"));
  writeFileSync(join(config.flake, "modules/apps.nix"), "{ pkgs, ... }: {\n  home.packages = with pkgs; [\n    git\n  ];\n}\n");
  return config;
};

const btop: Change = { kind: "add-package", scope: "home", file: "modules/apps.nix", attr: "pkgs.btop" };

test("attribute paths split on dots outside quotes", () => {
  expect(splitAttr("pkgs.kdePackages.kate")).toEqual(["pkgs", "kdePackages", "kate"]);
  expect(splitAttr('pkgs."7zz"')).toEqual(["pkgs", "7zz"]);
  expect(splitAttr("pkgs..x")).toBeUndefined();
  expect(splitAttr("pkgs.${x}")).toBeUndefined();
});

test("line diffs group changes into hunks with context", () => {
  const before = ["a", "b", "c", "d", "e", "f", "g", "h"].join("\n");
  const after = ["a", "B", "c", "d", "e", "f", "g", "h", "i"].join("\n");
  expect(diffLines(before, after, 1)).toEqual([
    { line: 1, lines: [{ kind: " ", text: "a" }, { kind: "-", text: "b" }, { kind: "+", text: "B" }, { kind: " ", text: "c" }] },
    { line: 8, lines: [{ kind: " ", text: "h" }, { kind: "+", text: "i" }] },
  ]);
});

test("drafts apply changes in order and report the ones that cannot apply", async () => {
  const config = setup();
  const { files, problems } = await renderDraft(config.flake, [
    btop,
    { ...btop, attr: "pkgs.git", kind: "add-package" },
    { kind: "set-option", scope: "home", file: "modules/apps.nix", option: "programs.bat.enable", value: true },
    { ...btop, file: "../outside.nix" },
  ]);
  expect(files[0].after).toBe(
    "{ pkgs, ... }: {\n  home.packages = with pkgs; [\n    git\n    btop\n  ];\n  programs.bat.enable = true;\n}\n",
  );
  expect(problems.map((problem) => problem.index)).toEqual([1, 3]);
  expect(problems[0].message).toContain("already listed");
  expect(remaining([btop, btop], [btop])).toEqual([btop]);
});

test("a created module is imported by its parent and takes later edits", async () => {
  const config = setup();
  expect(importPath("modules/default.nix", "modules/programs/fzf.nix")).toBe("./programs/fzf.nix");
  expect(importPath("modules/home/default.nix", "modules/fzf.nix")).toBe("../fzf.nix");
  const create: Change = { kind: "create-module", scope: "home", file: "modules/programs/fzf.nix", importer: "modules/apps.nix" };
  const { files, problems } = await renderDraft(config.flake, [
    create,
    { kind: "set-option", scope: "home", file: create.file, option: "programs.fzf.enable", value: true },
    { kind: "add-package", scope: "home", file: create.file, attr: "pkgs.fd" },
    create,
    { ...create, file: "modules/apps.nix" },
  ]);
  expect(problems.map((problem) => [problem.index, problem.message])).toEqual([
    [3, "modules/programs/fzf.nix already exists."],
    [4, "modules/apps.nix already exists."],
  ]);
  expect(files.map((file) => [file.path, file.created ?? false, file.baseHash === ""])).toEqual([
    ["modules/apps.nix", false, false],
    ["modules/programs/fzf.nix", true, true],
  ]);
  expect(files[0].after).toContain("  imports = [ ./programs/fzf.nix ];\n}");
  expect(files[1].after).toBe("{ pkgs, ... }:\n{\n  programs.fzf.enable = true;\n  home.packages = [ pkgs.fd ];\n}\n");
  expect(files[1].hunks[0].lines.some((line) => line.kind === "-")).toBe(false);
});

test("applying writes created files and adds them to Git", async () => {
  const config = setup();
  mkdirSync(join(config.flake, ".git"));
  const source = join(reviewRoot(config, "abababab"), "source");
  mkdirSync(join(source, "modules/programs"), { recursive: true });
  writeFileSync(join(source, "modules/programs/fzf.nix"), "{ }\n");
  const edits: ReviewEdits = {
    changes: [],
    hash: "",
    files: [{ path: "modules/programs/fzf.nix", baseHash: "", created: true, hunks: [] }],
    effects: [],
  };
  const calls: string[][] = [];
  const run = writeSources(config, "abababab", edits).pipe(
    Effect.provideService(Runner, {
      run: (command) => Effect.sync(() => (calls.push([...command.argv]), { code: 0, stdout: "", stderr: "" })),
    }),
  );
  await Effect.runPromise(run);
  expect(readFileSync(join(config.flake, "modules/programs/fzf.nix"), "utf8")).toBe("{ }\n");
  expect(calls).toEqual([["git", "-C", config.flake, "add", "--intent-to-add", "--", "modules/programs/fzf.nix"]]);
  // A file that appeared meanwhile is not overwritten.
  await expect(Effect.runPromise(run)).rejects.toThrow("was created after");
});

test("edited reviews build a staged copy, verify the edits and write them on apply", async () => {
  const config = setup();
  const snapshot = join(config.flake, "snapshot");
  cpSync(join(config.flake, "modules"), join(snapshot, "modules"), { recursive: true });
  writeFileSync(join(snapshot, "flake.nix"), "{}");
  const output = join(config.flake, "home-output");
  mkdirSync(output);
  const package_ = (name: string) => ({ out: `/nix/store/${name}`, name, version: "1", description: null });
  const calls: Command[] = [];
  const manager = new Manager(config, undefined, (command) => {
    calls.push(command);
    const [program, verb, sub] = command.argv;
    if (program === "cp") return runProcess(command);
    return Effect.sync(() => {
      if (verb === "flake" && sub === "metadata") {
        const ref = command.argv[4];
        return { code: 0, stdout: JSON.stringify({ path: ref === config.flake ? snapshot : ref.slice(5) }), stderr: "" };
      }
      if (verb === "eval") {
        const source = command.argv[3].slice(5).split("#")[0];
        const items = source === snapshot ? [package_("git")] : [package_("git"), package_("btop")];
        return {
          code: 0,
          stdout: JSON.stringify({
            packages: [{ file: `${source}/modules/apps.nix`, items }],
            options: [],
            resolved: ["/nix/store/btop"],
            used: [],
          }),
          stderr: "",
        };
      }
      if (command.argv.includes("--out-link"))
        symlinkSync(output, command.argv[command.argv.indexOf("--out-link") + 1]);
      return { code: 0, stdout: "", stderr: "" };
    });
  });
  writeDraft(config, [btop]);
  manager.start(request({ action: "review-build", reviewId: "eeeeeeee", scope: "home", edits: true }));
  const built = await manager.wait();
  expect(built?.error).toBeUndefined();
  const build = calls.find((command) => command.argv[1] === "build")!;
  expect(build.argv).toContain(`path:${reviewRoot(config, "eeeeeeee")}/source#homeConfigurations.user-test.activationPackage`);
  // The checkout is untouched until the review is applied.
  expect(readFileSync(join(config.flake, "modules/apps.nix"), "utf8")).not.toContain("btop");
  const [review] = await listReviews(config);
  expect(review.edits?.effects).toEqual([
    { scope: "home", added: [{ ...package_("btop"), file: "modules/apps.nix" }], removed: [], options: [] },
  ]);
  expect(review.edits?.files.map((file) => file.path)).toEqual(["modules/apps.nix"]);
  expect(manager.plan(request({ action: "review-apply", reviewId: review.id })).steps[0].internal).toBe("write-sources");
  manager.start(request({ action: "review-apply", reviewId: review.id, scope: "home" }));
  expect((await manager.wait())?.status).toBe("succeeded");
  expect(readFileSync(join(config.flake, "modules/apps.nix"), "utf8")).toContain("    btop\n");
  expect(readDraft(config)).toEqual([]);
});

test("a setting that does not take effect is reported before the package it was meant to allow", async () => {
  const config = setup();
  const snapshot = join(config.flake, "snapshot");
  cpSync(join(config.flake, "modules"), join(snapshot, "modules"), { recursive: true });
  writeFileSync(join(snapshot, "flake.nix"), "{}");
  const calls: Command[] = [];
  const manager = new Manager(config, undefined, (command) => {
    calls.push(command);
    const [program, verb, sub] = command.argv;
    if (program === "cp") return runProcess(command);
    return Effect.sync(() => {
      if (verb === "flake" && sub === "metadata") {
        const ref = command.argv[4];
        return { code: 0, stdout: JSON.stringify({ path: ref === config.flake ? snapshot : ref.slice(5) }), stderr: "" };
      }
      if (verb === "eval") {
        const source = command.argv[3].slice(5).split("#")[0];
        const files = [`/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-source/flake.nix`];
        if (source !== snapshot) files.push(`${source}/modules/apps.nix`);
        return {
          code: 0,
          stdout: JSON.stringify({
            packages: [],
            options: [{ exists: true, value: false, files }],
            resolved: [],
            used: [],
          }),
          stderr: "",
        };
      }
      return { code: 0, stdout: "", stderr: "" };
    });
  });
  const steam: Change = { kind: "add-package", scope: "home", file: "modules/apps.nix", attr: "pkgs.steam" };
  writeDraft(config, [
    steam,
    { kind: "set-option", scope: "home", file: "modules/apps.nix", option: "nixpkgs.config.allowUnfree", value: true },
  ]);
  manager.start(request({ action: "review-build", reviewId: "ffffffff", scope: "home", edits: true }));
  const failed = await manager.wait();
  expect(failed?.explanation).toEqual({
    title: "nixpkgs.config.allowUnfree is also set elsewhere",
    body: "A module written directly in flake.nix sets nixpkgs.config.allowUnfree too, and that value wins. Change it there instead of adding a new definition.",
  });
  expect(calls.some((command) => command.argv.at(-1)?.endsWith(".pkgs.steam.outPath"))).toBe(false);
});

test("edits only combine with reviewed builds of the checkout", () => {
  const config = setup();
  expect(() => plan(config, request({ action: "build", edits: true }))).toThrow("reviews");
  expect(() =>
    plan(config, request({ action: "review-build", scope: "home", generation: 1, edits: true })),
  ).toThrow("generation");
  const steps = plan(config, request({ action: "review-build", reviewId: "ffffffff", edits: true })).steps;
  expect(steps.slice(0, 2).map((step) => step.internal)).toEqual(["stage-sources", "verify-edits"]);
});
