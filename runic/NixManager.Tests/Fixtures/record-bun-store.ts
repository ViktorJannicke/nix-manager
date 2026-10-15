// Records the Bun version's software store helpers into Fixtures/bun-store.json, which StoreTests compares with
// the C# port: parseAppStream on appstream-sample.xml, matchApps against a small catalog, validAppId, detectHardware
// on the device trees of hardware-cases.json, and the options the inventory reads for the curated experiences and the
// Computer page (curatedOptions in src/store.ts). Run from the repository root in the development shell, after
// `bun install`:
//
//     bun runic/NixManager.Tests/Fixtures/record-bun-store.ts
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { matchApps, parseAppStream, validAppId, type AppInfo } from "../../../src/apps";
import { computerOptions } from "../../../src/computer";
import { experiences } from "../../../src/curated";
import { detectHardware } from "../../../src/hardware";
import type { CatalogEntry } from "../../../src/software";

const xml = readFileSync(new URL("./appstream-sample.xml", import.meta.url), "utf8");
const parsed = parseAppStream(xml);

const entry = (a: string, fields: Partial<CatalogEntry> = {}): CatalogEntry => ({
  a, n: a, v: "1.0", d: null, h: null, p: null, l: ["MIT"], f: true, b: false, ...fields,
});
const entries: CatalogEntry[] = [
  entry("firefox", { h: "https://www.mozilla.org/firefox/", d: "Web browser" }),
  entry("firefox-bin", { n: "firefox", h: "https://www.mozilla.org/firefox/" }),
  entry("firefox-esr", { n: "firefox", h: "https://www.mozilla.org/en-US/firefox/enterprise/" }),
  entry("discord", { h: "https://discordapp.com/", d: "All-in-one cross-platform voice and text chat for gamers", f: false }),
  entry("discord-canary", { n: "discord", h: "https://canary.discordapp.com/" }),
  entry("sioyek", { h: "https://github.com/ahrm/sioyek", d: "PDF viewer designed for research papers" }),
  entry("darkly", { h: "https://github.com/someone/darkly", d: "A Qt style" }),
  entry("gimp", { h: "https://www.gimp.org", d: "The GNU Image Manipulation Program" }),
  entry("gimp_3", { n: "gimp", h: "https://www.gimp.org", d: "The GNU Image Manipulation Program" }),
  entry("gimpPlugins.gmic", { n: "gimp-with-gmic", h: "https://www.gimp.org" }),
  entry("keepassxc", { h: "https://keepassxc.org/" }),
  entry("vlc", { h: "https://www.videolan.org/vlc/" }),
  entry("libvlc", { n: "vlc", h: "https://www.videolan.org/vlc/" }),
  entry("ripgrep", { p: "rg", h: "https://github.com/BurntSushi/ripgrep" }),
  entry("broken", { n: "VLC", b: true }),
];
const apps: AppInfo[] = [
  ...parsed,
  { id: "org.example.Broken", name: "Broken", summary: null, categories: [], icon: null, homepage: null, keywords: [] },
];

const curatedOptions = (scope: "home" | "system") => [
  ...experiences
    .filter((experience) => experience.scope === scope)
    .flatMap((experience) => [...Object.keys(experience.enable), ...experience.settings.map((setting) => setting.option)]),
  ...(scope === "system" ? computerOptions : []),
];

const hardware = (JSON.parse(readFileSync(new URL("./hardware-cases.json", import.meta.url), "utf8")) as {
  name: string;
  files: Record<string, string>;
  directories?: string[];
}[]).map(({ name, files, directories = [] }) => {
  const root = mkdtempSync(join(tmpdir(), "nix-manager-sysfs-"));
  try {
    for (const directory of directories) mkdirSync(join(root, directory), { recursive: true });
    for (const [path, text] of Object.entries(files)) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), text);
    }
    return { name, result: detectHardware(root) };
  } finally {
    rmSync(root, { recursive: true });
  }
});

const result = {
  parseAppStream: parsed,
  matchApps: { entries, apps, result: Object.fromEntries([...matchApps(apps, entries)].map(([attr, app]) => [attr, app.id])) },
  validAppId: ["org.gimp.GIMP", "org.keepassxc.KeePassXC.desktop", "", ".hidden", "a/b", "a..b", "x".repeat(201), "x".repeat(202), "a b", "1-a_b.c"].map(
    (id) => ({ id, result: validAppId(id) }),
  ),
  detectHardware: hardware,
  curatedOptions: { home: curatedOptions("home"), system: curatedOptions("system") },
};
await Bun.write(new URL("./bun-store.json", import.meta.url), JSON.stringify(result, null, 2) + "\n");
console.log("Recorded bun-store.json.");
