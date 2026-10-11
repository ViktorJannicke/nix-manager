import { afterEach, expect, test } from "bun:test";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { acceptsUnfree, changesScope, dropChange, featureModule, freePath, mergeChanges, newFileProblem, oneStepProblem, oneStepReviewProblem, rankImporters } from "../src/changes";
import type { BuildReview } from "../src/contracts";
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

test("features go into their own module or a new one beside single-topic modules", () => {
  const services = ["bluetooth", "podman", "ssh"].map((name) => ({ file: `modules/nixos/services/${name}.nix`, active: true }));
  expect(featureModule(services, "podman")).toBe("modules/nixos/services/podman.nix");
  expect(featureModule(services, "flatpak")).toBeUndefined();
  expect(featureModule([{ file: "configuration.nix", active: true }, { file: "hardware.nix", active: true }], "flatpak")).toBe("configuration.nix");
  expect(featureModule([{ file: "old/flatpak.nix", active: false }, { file: "configuration.nix", active: true }], "flatpak")).toBe("configuration.nix");
  expect(featureModule([], "flatpak")).toBeUndefined();
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
  const services = ["ssh", "podman"].map((name) => `modules/nixos/services/${name}.nix`);
  const imports = { "profiles/workstation.nix": services, "hosts/desktop/system.nix": ["profiles/workstation.nix", "modules/nixos/services/libvirt.nix"] };
  expect(rankImporters(["hosts/desktop/system.nix", "profiles/workstation.nix"], "modules/nixos/services/flatpak.nix", imports)[0]).toBe("profiles/workstation.nix");
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

test("non-free packages are accepted by allowUnfree, by name, or by a predicate", () => {
  const policy = { all: false, packages: ["steam"], predicate: false };
  expect(acceptsUnfree(policy, "steam")).toBe(true);
  expect(acceptsUnfree(policy, "discord")).toBe(false);
  expect(acceptsUnfree(policy, null)).toBe(false);
  expect(acceptsUnfree({ ...policy, all: true }, "discord")).toBe(true);
  // A predicate cannot be asked without the package, so it is trusted.
  expect(acceptsUnfree({ ...policy, predicate: true }, "discord")).toBe(true);
  // Without a known policy nothing is claimed.
  expect(acceptsUnfree(undefined, "discord")).toBe(true);
});

test("only installing and removing your own apps is applied in one step", () => {
  const remove: Change = { ...add, kind: "remove-package" };
  const unfree: Change = { kind: "set-option", scope: "home", file: "a.nix", option: "nixpkgs.config.allowUnfree", value: true };
  const module: Change = { kind: "create-module", scope: "home", file: "b.nix", importer: "a.nix" };
  expect(oneStepProblem([unfree, module, add, remove])).toBeUndefined();
  expect(oneStepProblem([])).toBeDefined();
  expect(oneStepProblem([{ ...add, scope: "system" }])).toContain("computer itself");
  expect(oneStepProblem([add, { ...unfree, value: false }])).toContain("settings");
  expect(oneStepProblem([{ kind: "set-option", scope: "home", file: "a.nix", option: "services.syncthing.enable", value: true }])).toContain("settings");

  // What the review found counts too: an edit may turn out to change an option by evaluation.
  const review: BuildReview = {
    id: "abababab",
    host: "test",
    scope: "home",
    createdAt: "",
    fingerprint: "",
    source: "checkout",
    edits: { changes: [add], hash: "", files: [], effects: [{ scope: "home", added: [], removed: [], options: [] }] },
    home: { path: "/nix/store/home", changes: [], compared: true },
  };
  expect(oneStepReviewProblem(review)).toBeUndefined();
  expect(oneStepReviewProblem({ ...review, system: review.home })).toContain("computer itself");
  expect(oneStepReviewProblem({ ...review, edits: undefined })).toBeDefined();
  expect(
    oneStepReviewProblem({ ...review, edits: { ...review.edits!, effects: [{ scope: "home", added: [], removed: [], options: [{ option: "xdg.enable" }] }] } }),
  ).toContain("settings");
});
