// Interface text through Runic Translations: components call the typed message functions of `m`, which follow the
// ViewModel's language (App and Setup call `useMessageLocale`). The backend stores English, as the Bun version does:
// step labels, errors and failure explanations, often with values filled in. `stored` translates such text through
// the stored catalogue (translations/stored), which the import script made from the Bun version's labels and
// messages with `runic-translations import-text`; its base-text.json maps each English text to its message key.

import { bindMarkup, createInlineRenderer, defineMarkup, getLocale, type Locale } from "virtual:runic-translations/dashboard/runtime";
import { m } from "virtual:runic-translations/dashboard";
import { inlineFactory } from "@runic-artifex/translations-svelte/inline";
import baseText from "../../translations/stored/base-text.json";

export { getLocale, m };
export type { Locale };

type Pattern = { key: string; english: string; pattern: RegExp; names: string[]; literal: number };
let exact: Map<string, string> | undefined;
let patterns: Pattern[] | undefined;
const call = (key: string, values?: Record<string, string>) =>
  (m as unknown as Record<string, (inputs?: Record<string, string>) => string>)[key](values);

function index() {
  exact = new Map();
  patterns = [];
  for (const [english, key] of Object.entries(baseText)) {
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
  if (getLocale() === "en") return text;
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
export function list(items: readonly string[]) {
  const locale = getLocale();
  return new Intl.ListFormat(locale === "en" ? "en-GB" : locale, { type: "conjunction" }).format(items);
}

// Messages with markup, such as `Detected as {#highlight}{$host}{/highlight}.`, render through `inline` and
// `LocalizedInline`. The page's own tags are declared in translations/runic.json; `LocalizedInline` renders them with
// the snippets its `custom` prop gives by tag name, such as "nm:highlight". The generated runtime has no binding for
// a project's own tags yet, so their contracts are restated here as runic.json declares them
// (Runic-Artifex/runic-translations-sdk#96).
const tag = (name: string) =>
  bindMarkup(defineMarkup({ name, kind: "paired", children: "inline", interactive: false, plainText: "children" }), inlineFactory.element);

/** Renders a message with markup for `LocalizedInline`. */
export const inline = createInlineRenderer(inlineFactory, [tag("nm:highlight"), tag("nm:aside")]);

/**
 * Text without its soft hyphens (U+00AD), for headings and names that never wrap: German compounds such as
 * "Wiederherstellungs\u00adpunkte" carry one for the narrow navigation, and a heading would otherwise give a landmark
 * a name with it.
 */
export const plain = (text: string) => text.replaceAll("\u00ad", "");
