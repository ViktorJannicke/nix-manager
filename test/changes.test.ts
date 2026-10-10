import { afterEach, expect, test } from "bun:test";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { changesScope, dropChange, freePath, mergeChanges, newFileProblem, rankImporters } from "../src/changes";
import { readDraft, writeDraft, type Change } from "../src/draft";
import { parseSoftware, runSoftware, software } from "../src/software-cli";
import { fixture } from "./helpers";

const cleanups: (() => void)[] = [];
afterEach(() => cleanups.splice(0).forEach((f) => f()));

const add: Change = { kind: "add-package", scope: "home", file: "a.nix", attr: "pkgs.btop" };
const set = (value: unknown): Change => ({ kind: "set-option", scope: "home", file: "a.nix", option: "programs.fzf.enable", value });
const create: Change = { kind: "create-module", scope: "home", file: "m/fzf.nix", importer: "m/default.nix" };

test("edits replace, cancel and deduplicate pending edits", () => {
  expect(mergeChanges([add], [add])).toEqual([add]);
  expect(mergeChanges([add], [{ ...add, kind: "remove-package" }])).toEqual([]);
  expect(mergeChanges([set(true)], [set(false)])).toEqual([set(false)]);
  expect(mergeChanges([set(true)], [set(false)], () => true)).toEqual([]);
  expect(mergeChanges([create], [create, { ...set(true), file: create.file }])).toEqual([create, { ...set(true), file: create.file }]);
  expect(changesScope([add])).toBe("home");
  expect(changesScope([add, { ...add, scope: "system" }])).toBe("all");
});

test("dropping a created file drops the edits written into it", () => {
  const into = { ...set(true), file: create.file };
  expect(dropChange([add, create, into], 1)).toEqual([add]);
  expect(dropChange([add, create, into], 2)).toEqual([add, create]);
});

test("new module paths are free, valid and imported from the closest folder", () => {
  const known = new Set(["modules/home/programs/fzf.nix"]);
  expect(freePath(known, "fzf", "modules/home/programs/git.nix", "modules/home")).toBe("modules/home/programs/fzf-2.nix");
  expect(freePath(known, "Virtual Machines", undefined, "modules/nixos")).toBe("modules/nixos/virtual-machines.nix");
  expect(newFileProblem(known, "modules/home/programs/fzf.nix")).toContain("exists");
  expect(newFileProblem(known, "../x.nix")).toContain("inside the flake");
  expect(newFileProblem(known, "modules/x.txt")).toContain(".nix");
  expect(newFileProblem(known, "modules/x.nix")).toBeUndefined();
  expect(
    rankImporters(["hosts/desktop/home.nix", "modules/home/default.nix", "modules/home/programs/default.nix", "modules/home/programs/git.nix"], "modules/home/programs/fzf.nix"),
  ).toEqual(["modules/home/programs/default.nix", "modules/home/programs/git.nix", "modules/home/default.nix", "hosts/desktop/home.nix"]);
});

test("software commands accept options anywhere", async () => {
  expect(software(["--flake", "/f", "software", "add", "btop", "--system"])).toEqual(["software", "add", "--flake", "/f", "btop", "--system"]);
  expect(software(["home", "switch"])).toBeUndefined();
  expect(await parseSoftware(["software", "add", "btop", "--new-file", "m/x.nix", "fd"])).toMatchObject({
    edit: { kind: "add", packages: ["btop", "fd"] },
    newFile: "m/x.nix",
    system: false,
  });
  expect(await parseSoftware(["software", "set", "--system", "services.x.port", "8080", "--file", "a.nix"])).toMatchObject({
    edit: { kind: "set", option: "services.x.port", value: "8080" },
    file: "a.nix",
    system: true,
  });
  expect(await parseSoftware(["software", "apply", "--boot"])).toMatchObject({ edit: { kind: "apply", boot: true, dryRun: false } });
});

test("draft commands edit the shared draft; prepare builds it into a review", async () => {
  const config = fixture();
  cleanups.push(() => rmSync(config.flake, { recursive: true, force: true }));
  mkdirSync(join(config.flake, "m"));
  writeFileSync(join(config.flake, "a.nix"), "{ pkgs, ... }: {\n  home.packages = [ pkgs.git ];\n}\n");
  const base = { json: false, plain: true, system: false } as const;
  writeDraft(config, [add, { ...add, attr: "pkgs.fd" }]);
  const log = console.log;
  const lines: string[] = [];
  console.log = (...values) => lines.push(values.join(" "));
  try {
    await runSoftware(config, { ...base, edit: { kind: "pending", diff: true } });
    await runSoftware(config, { ...base, edit: { kind: "drop", index: 1 } });
    expect(readDraft(config)).toEqual([{ ...add, attr: "pkgs.fd" }]);
    expect(await runSoftware(config, { ...base, edit: { kind: "prepare" } })).toMatchObject({
      action: "review-build",
      scope: "home",
      edits: true,
    });
    await expect(runSoftware(config, { ...base, edit: { kind: "apply", boot: false, dryRun: false } })).rejects.toThrow(
      "No prepared review",
    );
    await runSoftware(config, { ...base, edit: { kind: "clear" } });
  } finally {
    console.log = log;
  }
  expect(readDraft(config)).toEqual([]);
  expect(lines.join("\n")).toContain("+  home.packages = [ pkgs.git pkgs.btop pkgs.fd ];");
  expect(lines.join("\n")).toContain("Dropped: Install btop.");
});
