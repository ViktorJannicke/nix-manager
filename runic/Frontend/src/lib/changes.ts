// Pending edits in words (`src/changes.ts`). The backend keeps the English text of these phrases in its rules
// (Edits/ChangeRules.cs); the stored catalogue translates them, so `stored` turns each into the current language.

import type { RenderedDraft } from "../generated/types.js";
import { stored } from "./i18n";

export type Change = RenderedDraft["changes"][number];

const fill = (text: string, values: Record<string, string>) => text.replace(/\{(\w+)\}/g, (_, name: string) => values[name] ?? "");
/** English text with values filled in, in the current language. */
const say = (text: string, values: Record<string, string> = {}) => stored(fill(text, values));

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
  return `${label}: ${option} ${showValue(value)}`;
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
