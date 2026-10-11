import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { Glob } from "bun";
import { catalog, list, localeFrom, parts as split, setLocale, t, tn } from "../src/i18n";
import { parts } from "../src/locales/de";

const root = `${import.meta.dir}/..`;
const sources = ["ui/**/*.svelte", "ui/lib/**/*.ts", "ui/*.ts"].flatMap((pattern) => [...new Glob(pattern).scanSync(root)]);
const unquote = (raw: string) => JSON.parse(`"${raw}"`) as string;
const string = String.raw`"((?:[^"\\]|\\.)*)"`;

/** Every literal key the interface looks up, by the file it is in. */
function keys() {
  const found = new Map<string, string>();
  for (const file of sources) {
    const text = readFileSync(`${root}/${file}`, "utf8").replace(/<!--[\s\S]*?-->/g, "");
    for (const match of text.matchAll(new RegExp(String.raw`\btn\(\s*${string}\s*,\s*${string}`, "g"))) {
      found.set(unquote(match[1]), file);
      found.set(unquote(match[2]), file);
    }
    for (const match of text.matchAll(new RegExp(String.raw`\b(?:t|msg)\(\s*${string}`, "g"))) found.set(unquote(match[1]), file);
    for (const match of text.matchAll(new RegExp(String.raw`<T\s+text=\{${string}\}`, "g"))) found.set(unquote(match[1]), file);
  }
  return found;
}
const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();

describe("German catalogue", () => {
  const de = catalog("de");
  const used = keys();

  test("translates every text the interface shows", () => {
    const missing = [...used].filter(([key]) => !(key in de)).map(([key, file]) => `${file}: ${key}`);
    expect(missing).toEqual([]);
  });

  // A count may go unsaid, as in "den neuesten Wiederherstellungspunkt".
  test("keeps every placeholder", () => {
    const unsaid = (names: string[]) => names.filter((name) => name !== "count").join();
    const wrong = Object.entries(de).filter(
      ([key, value]) =>
        unsaid(placeholders(key)) !== unsaid(placeholders(value)) || placeholders(value).some((name) => !placeholders(key).includes(name)),
    );
    expect(wrong).toEqual([]);
  });

  test("translates a text the same way in every part", () => {
    const seen = new Map<string, string>();
    const conflicts: string[] = [];
    for (const [name, entries] of Object.entries(parts))
      for (const [key, value] of Object.entries(entries)) {
        const earlier = seen.get(key);
        if (earlier !== undefined && earlier !== value) conflicts.push(`${name}: ${key}`);
        seen.set(key, value);
      }
    expect(conflicts).toEqual([]);
  });

  test("has no translations nothing uses", () => {
    expect(Object.keys(de).filter((key) => !used.has(key))).toEqual([]);
  });
});

describe("translation", () => {
  test("reads the language from browser and locale tags", () => {
    expect(localeFrom(["de-DE"])).toBe("de");
    expect(localeFrom([undefined, "", "de_AT.UTF-8"])).toBe("de");
    expect(localeFrom(["C.UTF-8", "de"])).toBe("de");
    expect(localeFrom(["fr-FR", "de"])).toBe("en");
    expect(localeFrom([])).toBe("en");
  });

  test("fills placeholders, counts and lists in the current language", () => {
    setLocale("de");
    try {
      expect(t("Settings")).toBe("Einstellungen");
      expect(t("Targeting {host}", { host: "laptop" })).toBe(`${catalog("de")["Targeting {host}"].replace("{host}", "laptop")}`);
      expect(tn("one {count}", "{count} more", 1200)).toBe("1.200 more");
      expect(t("not translated, {name}", { name: "kept" })).toBe("not translated, kept");
      expect(list(["a", "b", "c"])).toBe("a, b und c");
      expect(split("Detected as {host}.")).toEqual([{ text: "Erkannt als " }, { name: "host" }, { text: "." }]);
    } finally {
      setLocale("en");
    }
    expect(t("Settings")).toBe("Settings");
  });
});
