// Pending edits in words and in combination, in the current language. The backend describes them with the same
// messages (Edits/ChangeRules.cs).

import type { Change, ModuleFile, UnfreePolicy } from "../generated/types.js";
import { m, text, type DisplayText } from "./i18n";

export type { Change };
export type Target = "home" | "system";

/** Whether two labels are the same text. */
const sameLabel = (a: DisplayText | null, b: DisplayText | null) => a?.english === b?.english && a?.reference === b?.reference;

/** Whether two package edits are the same: every member matches. */
const same = (a: Change, b: Change) =>
  a.$case === b.$case && a.scope === b.scope && a.file === b.file && sameLabel(a.label, b.label) && "attr" in a && "attr" in b && a.attr === b.attr;

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
    if (change.$case === "create-module") {
      if (!changes.some((item) => item.$case === "create-module" && item.file === change.file)) changes.push(change);
    } else if ("attr" in change) {
      const opposite = changes.findIndex(
        (item) => "attr" in item && item.attr === change.attr && item.file === change.file && item.$case !== change.$case,
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
  return changes.filter((change, position) => position !== index && !(removed?.$case === "create-module" && change.file === removed.file));
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

/** Why a path cannot be a new module file, if it cannot. */
export function newFileProblem(known: ReadonlySet<string>, file: string) {
  if (!/^([A-Za-z0-9_+-][A-Za-z0-9._+-]*\/)*[A-Za-z0-9_+-][A-Za-z0-9._+-]*\.nix$/.test(file))
    return m.stored_use_a_path_inside_the_flake_ending();
  if (known.has(file)) return m.stored_this_file_exists_already();
  return undefined;
}

/** "Home Manager: programs/git.nix" style names for module files outside the flake. */
export function origin(file: string) {
  const inner = file.replace(/^\/nix\/store\/[a-z0-9]{32}-[^/]+\//, "");
  if (inner === file) return file;
  return inner.startsWith("nixos/modules/") ? `NixOS: ${inner.slice(14)}` : `Home Manager: ${inner.replace(/^modules\//, "")}`;
}

/** An option's value in words: on, off, not set, a list or JSON. */
export function showValue(value: unknown) {
  if (value === true) return m.stored_on();
  if (value === false) return m.stored_off();
  if (value === null || value === undefined) return m.stored_not_set();
  if (Array.isArray(value)) return value.length ? value.join(", ") : m.stored_empty();
  return typeof value === "string" ? `"${value}"` : JSON.stringify(value);
}

/** An option edit in words; the value's phrase has its own keys so it can be translated. */
function describeOption(option: string, value: unknown, label: DisplayText | null) {
  const empty = Array.isArray(value) && !value.length;
  if (!label) {
    if (value === true) return m.stored_on_2({ option });
    if (value === false) return m.stored_off_2({ option });
    if (value === null || value === undefined) return m.stored_not_set_2({ option });
    if (empty) return m.stored_empty_2({ option });
    return `${option} ${showValue(value)}`;
  }
  const values = { option, label: text(label) };
  if (value === true) return m.stored_on_3(values);
  if (value === false) return m.stored_off_3(values);
  if (value === null || value === undefined) return m.stored_not_set_3(values);
  if (empty) return m.stored_empty_3(values);
  return `${values.label}: ${option} ${showValue(value)}`;
}

/** A pending edit in words: what it does, and where. */
export function describeChange(change: Change) {
  switch (change.$case) {
    case "add-package":
      return { title: m.stored_install({ name: change.attr.replace(/^pkgs\./, "") }), detail: change.file };
    case "remove-package":
      return { title: m.stored_remove({ name: change.attr.replace(/^pkgs\./, "") }), detail: change.file };
    case "set-option":
      return { title: describeOption(change.option, change.value, change.label), detail: change.file };
    case "unset-option":
      return { title: m.stored_reset_to_its_default({ option: change.option }), detail: change.file };
    case "create-module":
      return {
        title: m.stored_create({ name: change.file.split("/").at(-1)! }),
        detail: m.stored_imported_from({ file: change.file, importer: change.importer }),
      };
  }
}

/** The scope a review must build to include every edit. */
export function changesScope(changes: readonly Change[]): "all" | "home" | "system" {
  const scopes = new Set(changes.map((change) => change.scope));
  return scopes.size > 1 ? "all" : scopes.has("system") ? "system" : "home";
}

/** Why `changes` cannot be installed in one step; undefined when they can (`oneStepProblem`). */
export function oneStepProblem(changes: readonly Change[]): string | undefined {
  if (!changes.length) return m.stored_there_is_nothing_to_install();
  if (changes.some((change) => change.scope !== "home")) return m.stored_it_changes_the_computer_itself_not_just();
  const simple = changes.every(
    (change) =>
      change.$case === "add-package" ||
      change.$case === "remove-package" ||
      change.$case === "create-module" ||
      (change.$case === "set-option" && change.option === "nixpkgs.config.allowUnfree" && change.value === true),
  );
  return simple ? undefined : m.stored_it_changes_settings_which_can_restart_programs();
}
