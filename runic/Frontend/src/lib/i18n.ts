// Interface text through Runic Translations: components call the typed message functions of `m`. The backend
// stores English, as the Bun version does: step labels, errors and failure explanations, often with values filled
// in. `stored` translates such text through the stored catalogue (translations/stored), which the import script
// made from the Bun version's labels and messages.

import { m } from "virtual:runic-translations/dashboard";
import { configureLocaleResolver, resolveLocale, type Locale } from "virtual:runic-translations/dashboard/runtime";
import { storedMessages } from "./stored-catalog";

export { m };
export type { Locale };

let current: Locale = "en";
configureLocaleResolver(() => current);

/** The language every message without an explicit locale uses. */
export const getLocale = () => current;

/** Switches the language; the caller re-renders (App keys the page on it). */
export function setLocale(locale: string) {
  current = resolveLocale(locale);
  document.documentElement.lang = current;
}

type Pattern = { key: string; english: string; pattern: RegExp; names: string[]; literal: number };
let exact: Map<string, string> | undefined;
let patterns: Pattern[] | undefined;
const call = (key: string, values?: Record<string, string>) =>
  (m as unknown as Record<string, (inputs?: Record<string, string>) => string>)[key](values);

function index() {
  exact = new Map();
  patterns = [];
  for (const [key, english] of storedMessages) {
    const names = [...english.matchAll(/\{(\w+)\}/g)].map((match) => match[1]);
    if (!names.length) {
      exact.set(english, key);
      continue;
    }
    const source = english
      .split(/\{\w+\}/)
      .map((piece) => piece.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))
      .join("([\\s\\S]+?)");
    patterns.push({ key, english, pattern: new RegExp(`^${source}$`), names, literal: english.replace(/\{\w+\}/g, "").length });
  }
  // The most specific key wins, so "{label} failed" does not take "Building {name} failed".
  patterns.sort((a, b) => b.literal - a.literal);
}

/**
 * Text that the backend stored in English, in the current language. A value filled into it can be stored text
 * itself, like the step label in "{label} failed". Text without a translation stays English.
 */
export function stored(text: string | null | undefined): string {
  if (!text) return "";
  if (!exact) index();
  const key = exact!.get(text);
  if (key) return call(key);
  if (current === "en") return text;
  for (const { key, pattern, names } of patterns!) {
    const match = pattern.exec(text);
    if (match) return call(key, Object.fromEntries(names.map((name, i) => [name, stored(match[i + 1])])));
  }
  return text;
}

/**
 * Stored text with placeholders, such as "{label}: {option} on", filled with `values` in the current language.
 * Unlike matching the filled English with `stored`, the template is known, so a value cannot be mistaken for part
 * of another message. Values are used as given; translate those that are stored text first.
 */
export function storedTemplate(english: string, values: Record<string, string>): string {
  if (!exact) index();
  const key = exact!.get(english) ?? patterns!.find((pattern) => pattern.english === english)?.key;
  return key ? call(key, values) : english.replace(/\{(\w+)\}/g, (_, name: string) => values[name] ?? "");
}

/** English action labels as the backend stores them (`actionLabels`). */
const actionLabels: Record<string, string> = {
  build: "Build configuration",
  switch: "Build and switch",
  boot: "Apply on next boot",
  rollback: "Go back to the previous version",
  gc: "Remove old restore points",
  check: "Check for updates",
  "t3code-update": "Update T3 Code",
  "opencode-update": "Update OpenCode",
  "pince-update": "Update PINCE",
  "review-build": "Prepare changes",
  "review-apply": "Apply changes",
};

/** The name of an action, such as "Check for updates". */
export const actionLabel = (action: string) => stored(actionLabels[action] ?? action);

/** "a, b and c" in the current language; English without the serial comma. */
export const list = (items: readonly string[]) =>
  new Intl.ListFormat(current === "en" ? "en-GB" : current, { type: "conjunction" }).format(items);

// Markup in translated text, as the Bun version's T component did: a message is called with `slot(name)` for a
// value that is markup, such as emphasis, and `Markup` renders its pieces with a snippet for each slot. The words
// around a slot stay in the translation's order.
const open = "\u{F0000}";
const close = "\u{F0001}";

/** A placeholder value that `Markup` replaces with markup. */
export const slot = (name: string) => `${open}${name}${close}`;

/** The pieces of a message called with `slot` values: text, and the names of slots between it. */
export function pieces(text: string): ({ text: string } | { slot: string })[] {
  return text
    .split(new RegExp(`${open}(\\w+)${close}`, "u"))
    .map((piece, index) => (index % 2 ? { slot: piece } : { text: piece }))
    .filter((piece) => !("text" in piece) || piece.text);
}
