// Pending edits in words and in combination, shared by the dashboard and the
// command line. Only types come from the draft module, so the browser bundle
// stays free of Node code.
import type { Change } from "./draft";
import type { ModuleFile, Target } from "./software";

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/**
 * Adds edits to a draft. An edit replaces a pending edit of the same option,
 * and cancels a pending opposite package edit instead of stacking. An option
 * edit that `unchanged` reports as a no-op only drops the pending edit.
 */
export function mergeChanges(
  current: readonly Change[],
  next: readonly Change[],
  unchanged: (change: Change) => boolean = () => false,
): Change[] {
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
      changes = changes.filter(
        (item) => !("option" in item && item.option === change.option && item.scope === change.scope),
      );
      if (!unchanged(change)) changes.push(change);
    }
  }
  return changes;
}

/** Removes an edit; removing a created file also removes the edits written into it. */
export function dropChange(changes: readonly Change[], index: number): Change[] {
  const removed = changes[index];
  return changes.filter(
    (change, position) => position !== index && !(removed?.kind === "create-module" && change.file === removed.file),
  );
}

/** A value as a short phrase: on, off, not set, or its JSON. */
export function showValue(value: unknown) {
  if (value === true) return "on";
  if (value === false) return "off";
  if (value === null || value === undefined) return "not set";
  if (Array.isArray(value)) return value.length ? value.join(", ") : "empty";
  return typeof value === "string" ? `"${value}"` : JSON.stringify(value);
}

/** A pending edit in words: what it does, and where. */
export function describeChange(change: Change) {
  switch (change.kind) {
    case "add-package":
      return { title: `Install ${change.attr.replace(/^pkgs\./, "")}`, detail: change.file };
    case "remove-package":
      return { title: `Remove ${change.attr.replace(/^pkgs\./, "")}`, detail: change.file };
    case "set-option":
      return { title: `${change.label ? `${change.label}: ` : ""}${change.option} ${showValue(change.value)}`, detail: change.file };
    case "unset-option":
      return { title: `Reset ${change.option} to its default`, detail: change.file };
    case "create-module":
      return { title: `Create ${change.file.split("/").at(-1)}`, detail: `${change.file}, imported from ${change.importer}` };
  }
}

/** The scope a review must build to include every edit. */
export function changesScope(changes: readonly Change[]): "all" | "home" | "system" {
  const scopes = new Set(changes.map((change) => change.scope));
  return scopes.size > 1 ? "all" : scopes.has("system") ? "system" : "home";
}

const directory = (file: string) => file.split("/").slice(0, -1).join("/");

/**
 * The files a new module is best imported from, best first: the one importing
 * most of its neighbours already (per `imports`), the folder's default.nix, a file
 * in the same folder, then the closest folder above.
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
 * Module files that could hold an option, best first: files the host imports,
 * the last one used, files of the same kind of configuration, then files that
 * already set options under the same name or live in a folder of that name.
 * Packages, overlays, data, tests and docs are left out.
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

/** The topic of an option, which names a module of its own: `fzf` for `programs.fzf.enable`. */
export const optionTopic = (option: string) => {
  const [head, next] = option.split(".");
  return /^(programs|services|hardware|virtualisation|security)$/.test(head) && next ? next : head;
};

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
 * The module a feature's options belong in, from candidates ranked best
 * first: one named after the feature, or the best candidate unless it sits
 * among several single-topic modules. `undefined` means a new file.
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
