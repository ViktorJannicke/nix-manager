// Pending edits in words and in combination (`src/changes.ts`). The backend keeps the English text of these
// phrases in its rules (Edits/ChangeRules.cs); the stored catalogue translates them, so `stored` turns each into
// the current language. Problems are returned in English, for `stored` where they are shown.

import type { ModuleFile, RenderedDraft, UnfreePolicy } from "../generated/types.js";
import { stored, storedTemplate } from "./i18n";

export type Change = RenderedDraft["changes"][number];
export type Target = "home" | "system";

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** Whether a policy accepts a non-free package by its name; a predicate counts as accepting. */
export const acceptsUnfree = (policy: UnfreePolicy | null | undefined, name: string | null) =>
  !policy || policy.all || policy.predicate || (name !== null && policy.packages.includes(name));

/**
 * Adds edits to a draft. An edit replaces a pending edit of the same option, and cancels a pending opposite
 * package edit instead of stacking. An option edit that `unchanged` reports as a no-op only drops the pending edit.
 */
export function mergeChanges(current: readonly Change[], next: readonly Change[], unchanged: (change: Change) => boolean = () => false): Change[] {
  let changes = [...current];
  for (const change of next) {
    if (change.kind === "create-module") {
      if (!changes.some((item) => item.kind === "create-module" && item.file === change.file)) changes.push(change);
    } else if ("attr" in change) {
      const opposite = changes.findIndex(
        (item) => "attr" in item && item.attr === change.attr && item.file === change.file && item.kind !== change.kind,
      );
      if (opposite >= 0) {
        changes.splice(opposite, 1);
        continue;
      }
      if (changes.some((item) => same(item, change))) continue;
      changes.push(change);
    } else {
      changes = changes.filter((item) => !("option" in item && item.option === change.option && item.scope === change.scope));
      if (!unchanged(change)) changes.push(change);
    }
  }
  return changes;
}

/** Removes an edit; removing a created file also removes the edits written into it. */
export function dropChange(changes: readonly Change[], index: number): Change[] {
  const removed = changes[index];
  return changes.filter((change, position) => position !== index && !(removed?.kind === "create-module" && change.file === removed.file));
}

const directory = (file: string) => file.split("/").slice(0, -1).join("/");

/**
 * The files a new module is best imported from, best first: the one importing most of its neighbours already
 * (per `imports`), the folder's default.nix, a file in the same folder, then the closest folder above.
 */
export function rankImporters(importers: readonly string[], file: string, imports: Readonly<Record<string, readonly string[]>> = {}) {
  const folder = directory(file);
  const score = (candidate: string) => {
    const neighbours = imports[candidate]?.filter((path) => directory(path) === folder).length ?? 0;
    if (neighbours) return 1000 + neighbours;
    const at = directory(candidate);
    const inside = folder === at || folder.startsWith(`${at}/`) || !at;
    const depth = at ? at.split("/").length : 0;
    return (inside ? 100 + depth * 10 : 0) + (candidate.endsWith("/default.nix") || candidate === "default.nix" ? 5 : 0);
  };
  return [...importers].sort((a, b) => score(b) - score(a) || a.localeCompare(b));
}

/**
 * Module files that could hold an option, best first: files the host imports, the last one used, files of the
 * same kind of configuration, then files that already set options under the same name or live in a folder of
 * that name. Packages, overlays, data, tests and docs are left out.
 */
export function rankModules(modules: readonly ModuleFile[], scope: Target, option: string, last?: string) {
  const head = option.split(".")[0];
  const own = scope === "home" ? /home/ : /nixos|system/;
  const other = scope === "home" ? /nixos|system/ : /home/;
  const score = (module: ModuleFile) =>
    (module.active === true ? 200 : module.active === false ? -200 : 0) +
    (module.file === last ? 100 : 0) +
    (own.test(module.file) ? 20 : 0) -
    (other.test(module.file) ? 40 : 0) +
    (module.names.some((name) => name.split(".")[0] === head) ? 6 : 0) +
    (module.file.split("/").includes(head) ? 4 : 0) -
    (/hosts\//.test(module.file) ? 3 : 0);
  return modules
    .filter((module) => !/(^|\/)(pkgs|overlays?|data|tests?|docs)\//.test(module.file))
    .sort((a, b) => score(b) - score(a) || a.file.localeCompare(b.file));
}

/** A path for a new module named `name` in the folder of `near`, not in `known`. */
export function freePath(known: ReadonlySet<string>, name: string, near: string | undefined, fallback: string) {
  const folder = near ? directory(near) : fallback;
  const slug = name.toLowerCase().replace(/[^a-z0-9+-]+/g, "-").replace(/^-|-$/g, "") || "module";
  for (let n = 1; ; n++) {
    const file = `${folder ? `${folder}/` : ""}${slug}${n > 1 ? `-${n}` : ""}.nix`;
    if (!known.has(file)) return file;
  }
}

/**
 * The module a feature's options belong in, from candidates ranked best first: one named after the feature, or
 * the best candidate unless it sits among several single-topic modules. `undefined` means a new file.
 */
export function featureModule(modules: readonly { file: string; active?: boolean | null }[], name: string) {
  const usable = modules.filter((module) => module.active !== false);
  const named = usable.find((module) => module.file.split("/").at(-1) === `${name}.nix`);
  if (named) return named.file;
  const best = usable[0];
  if (!best) return undefined;
  const folder = directory(best.file);
  const siblings = usable.filter((module) => module.active && directory(module.file) === folder).length;
  return siblings >= 3 ? undefined : best.file;
}

/** Why a path cannot be a new module file, in English, if it cannot. */
export function newFileProblem(known: ReadonlySet<string>, file: string) {
  if (!/^([A-Za-z0-9_+-][A-Za-z0-9._+-]*\/)*[A-Za-z0-9_+-][A-Za-z0-9._+-]*\.nix$/.test(file))
    return "Use a path inside the flake ending in .nix, with letters, digits, dots, dashes and slashes.";
  if (known.has(file)) return "This file exists already.";
  return undefined;
}

/** "Home Manager: programs/git.nix" style names for module files outside the flake. */
export function origin(file: string) {
  const inner = file.replace(/^\/nix\/store\/[a-z0-9]{32}-[^/]+\//, "");
  if (inner === file) return file;
  return inner.startsWith("nixos/modules/") ? `NixOS: ${inner.slice(14)}` : `Home Manager: ${inner.replace(/^modules\//, "")}`;
}

/** Stored English text in the current language, with values filled in; a label is stored text itself. */
const say = (text: string, values: Record<string, string> = {}) =>
  storedTemplate(text, "label" in values ? { ...values, label: stored(values.label) } : values);

/** An option's value in words: on, off, not set, a list or JSON. */
export function showValue(value: unknown) {
  if (value === true) return say("on");
  if (value === false) return say("off");
  if (value === null || value === undefined) return say("not set");
  if (Array.isArray(value)) return value.length ? value.join(", ") : say("empty");
  return typeof value === "string" ? `"${value}"` : JSON.stringify(value);
}

/** An option edit in words; the value's phrase has its own keys so it can be translated. */
function describeOption(option: string, value: unknown, label: string | undefined) {
  const values = { option, label: label ?? "" };
  const empty = Array.isArray(value) && !value.length;
  if (!label) {
    if (value === true) return say("{option} on", values);
    if (value === false) return say("{option} off", values);
    if (value === null || value === undefined) return say("{option} not set", values);
    if (empty) return say("{option} empty", values);
    return `${option} ${showValue(value)}`;
  }
  if (value === true) return say("{label}: {option} on", values);
  if (value === false) return say("{label}: {option} off", values);
  if (value === null || value === undefined) return say("{label}: {option} not set", values);
  if (empty) return say("{label}: {option} empty", values);
  return `${stored(label)}: ${option} ${showValue(value)}`;
}

/** A pending edit in words: what it does, and where. */
export function describeChange(change: Change) {
  switch (change.kind) {
    case "add-package":
      return { title: say("Install {name}", { name: change.attr.replace(/^pkgs\./, "") }), detail: change.file };
    case "remove-package":
      return { title: say("Remove {name}", { name: change.attr.replace(/^pkgs\./, "") }), detail: change.file };
    case "set-option":
      return { title: describeOption(change.option, change.value, change.label), detail: change.file };
    case "unset-option":
      return { title: say("Reset {option} to its default", { option: change.option }), detail: change.file };
    case "create-module":
      return {
        title: say("Create {name}", { name: change.file.split("/").at(-1)! }),
        detail: say("{file}, imported from {importer}", { file: change.file, importer: change.importer }),
      };
  }
}

/** The scope a review must build to include every edit. */
export function changesScope(changes: readonly Change[]): "all" | "home" | "system" {
  const scopes = new Set(changes.map((change) => change.scope));
  return scopes.size > 1 ? "all" : scopes.has("system") ? "system" : "home";
}

/** Why `changes` cannot be installed in one step, in English; undefined when they can (`oneStepProblem`). */
export function oneStepProblem(changes: readonly Change[]): string | undefined {
  if (!changes.length) return "There is nothing to install.";
  if (changes.some((change) => change.scope !== "home")) return "It changes the computer itself, not just your apps.";
  const simple = changes.every(
    (change) =>
      change.kind === "add-package" ||
      change.kind === "remove-package" ||
      change.kind === "create-module" ||
      (change.kind === "set-option" && change.option === "nixpkgs.config.allowUnfree" && change.value === true),
  );
  return simple ? undefined : "It changes settings, which can restart programs you are using.";
}
