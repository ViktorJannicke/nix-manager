import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { Glob } from "bun";
import { bundles } from "../src/bundles";
import { computerSections, hardwareSuggestions, keyboardLayouts } from "../src/computer";
import { experiences } from "../src/curated";
import { actionLabels } from "../src/domain";
import { catalog, list, localeFrom, msg, parts as split, setLocale, t, tn } from "../src/i18n";
import { parts } from "../src/locales/de";
import { setupSteps } from "../src/setup";

const root = `${import.meta.dir}/..`;
const sources = ["ui/**/*.svelte", "ui/lib/**/*.ts", "ui/*.ts", "src/**/*.ts"]
  .flatMap((pattern) => [...new Glob(pattern).scanSync(root)])
  .filter((file) => !file.startsWith("src/locales/") && file !== "src/i18n.ts");
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
  for (const [file, texts] of data) for (const text of texts) found.set(text, file);
  return found;
}

/** Text the interface shows from data, through `t(item.title)` and the like. */
const data: [string, string[]][] = [
  ["src/domain.ts", Object.values(actionLabels)],
  ["src/setup.ts", setupSteps.map((step) => step.label)],
  [
    "src/curated.ts",
    experiences.flatMap((item) => [item.title, item.summary, item.category, ...item.settings.flatMap((setting) => [setting.label, setting.help ?? []].flat())]),
  ],
  ["src/bundles.ts", bundles.flatMap((bundle) => [bundle.title, bundle.summary])],
  [
    "src/computer.ts",
    [
      ...computerSections.flatMap((section) => [
        section.title,
        section.summary,
        ...section.settings.flatMap((setting) => [setting.label, setting.help ?? []].flat()),
      ]),
      ...hardwareSuggestions.map((suggestion) => suggestion.reason),
      ...keyboardLayouts.map((layout) => layout.label),
    ],
  ],
];
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
      expect(tn("one {count}", "{count} things", 1200)).toBe("1.200 things");
      expect(t("not translated, {name}", { name: "kept" })).toBe("not translated, kept");
      expect(list(["a", "b", "c"])).toBe("a, b und c");
      expect(split("Detected as {host}.")).toEqual([{ text: "Erkannt als " }, { name: "host" }, { text: "." }]);
    } finally {
      setLocale("en");
    }
    expect(t("Settings")).toBe("Settings");
  });

  test("translates stored text through the key it was filled from", () => {
    const stored = [
      msg("Go back to system generation {generation}", { generation: 12 }),
      msg("Write {count} edited files", { count: 3 }),
      msg("Write {file}", { file: "home.nix" }),
      msg("{label} failed", { label: msg("Activate Home Manager") }),
    ];
    expect(stored).toEqual(["Go back to system generation 12", "Write 3 edited files", "Write home.nix", "Activate Home Manager failed"]);
    setLocale("de");
    try {
      // "Write 3 edited files" also fits "Write {file}"; the more specific key wins.
      expect(stored.map((text) => t(text))).toEqual([
        "Zum Wiederherstellungspunkt 12 des Systems zurückkehren",
        "3 geänderte Dateien schreiben",
        "home.nix schreiben",
        "Fehlgeschlagen: Benutzerkonfiguration aktivieren",
      ]);
    } finally {
      setLocale("en");
    }
    expect(t(stored[0])).toBe(stored[0]);
  });
});
