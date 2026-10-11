// Interface text in the reader's language. English sentences are the keys, so
// code reads as it is shown and anything not translated yet stays readable.
// The dashboard and the tray share one catalogue; the command line stays in
// English, so its output can be searched for and pasted into reports.

import { de, filled } from "./locales/de";

export const locales = ["en", "de"] as const;
export type Locale = (typeof locales)[number];
export type Values = Record<string, string | number>;

const catalogs: Record<Locale, Readonly<Record<string, string>>> = { en: {}, de };
/** Keys that stored, already filled text is matched against. */
const stored: Record<Locale, Readonly<Record<string, string>>> = { en: {}, de: filled };
let current: Locale = "en";

export const getLocale = () => current;
export function setLocale(locale: Locale) {
  current = locale;
}

/** The first supported language among tags like "de-DE", "de_DE.UTF-8" or "C"; English otherwise. */
export function localeFrom(tags: readonly (string | undefined)[]): Locale {
  for (const tag of tags) {
    const language = tag?.split(/[-_.@:]/)[0]?.toLowerCase();
    if (!language || language === "c" || language === "posix") continue;
    return (locales as readonly string[]).includes(language) ? (language as Locale) : "en";
  }
  return "en";
}

const number = (value: string | number) => (typeof value === "number" ? value.toLocaleString(current) : value);

const fill = (text: string, values: Values) =>
  text.replace(/\{(\w+)\}/g, (match, name: string) => (name in values ? `${number(values[name])}` : match));

/** Stored keys as patterns, so text filled in English elsewhere can still be translated. */
const patterns = new Map<Locale, { pattern: RegExp; names: string[]; translation: string; literal: number }[]>();
function filledIn(text: string) {
  if (!patterns.has(current))
    patterns.set(
      current,
      Object.entries(stored[current])
        .flatMap(([key, translation]) => {
          const names = [...key.matchAll(/\{(\w+)\}/g)].map((match) => match[1]);
          if (!names.length) return [];
          const source = key.split(/\{\w+\}/).map((piece) => piece.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("([\\s\\S]+?)");
          return [{ pattern: new RegExp(`^${source}$`), names, translation, literal: key.replace(/\{\w+\}/g, "").length }];
        })
        // The most specific key wins, so "{label} failed" does not take "Building {name} failed".
        .sort((a, b) => b.literal - a.literal),
    );
  for (const { pattern, names, translation } of patterns.get(current)!) {
    const match = pattern.exec(text);
    // A value can be stored text itself, like the step label in "{label} failed".
    if (match) return fill(translation, Object.fromEntries(names.map((name, index) => [name, t(match[index + 1])])));
  }
  return text;
}

/**
 * `text` in the current language, with `{name}` filled from `values`. Text
 * that was filled in English before it got here, like a stored step label,
 * is translated through the key it was filled from.
 */
export function t(text: string, values?: Values) {
  const translated: string = catalogs[current][text] ?? (current === "en" || values ? text : filledIn(text));
  return values ? fill(translated, values) : translated;
}

/**
 * Marks English text that is stored or sent as it is and translated where it
 * is shown, like step labels; filled the same way as `t`.
 */
export const msg = (text: string, values?: Values) =>
  values ? text.replace(/\{(\w+)\}/g, (match, name: string) => (name in values ? `${values[name]}` : match)) : text;

/** `one` or `other`, by `count`, which is also available as `{count}`. */
export const tn = (one: string, other: string, count: number, values?: Values) =>
  t(count === 1 ? one : other, { ...values, count });

/** The translated text split around its `{name}` placeholders, for text with markup in it. */
export function parts(text: string): ({ text: string } | { name: string })[] {
  return (catalogs[current][text] ?? text)
    .split(/(\{\w+\})/)
    .filter(Boolean)
    .map((part) => (/^\{\w+\}$/.test(part) ? { name: part.slice(1, -1) } : { text: part }));
}

/** "a, b and c" in the current language; English without the serial comma. */
export const list = (items: readonly string[]) =>
  new Intl.ListFormat(current === "en" ? "en-GB" : current, { type: "conjunction" }).format(items);

/** Every translation of a language, for the catalogue tests. */
export const catalog = (locale: Locale) => catalogs[locale];
