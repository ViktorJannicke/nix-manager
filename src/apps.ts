// Apps in the package catalog. nixpkgs knows which packages exist but not
// which of them are apps people open from a menu, what they are called there
// or what they look like. Flathub's public AppStream catalogue does; its
// entries are matched to nixpkgs packages by homepage and name. Packages still
// come from the locked nixpkgs: Flathub only names, sorts and pictures them.

import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import type { Config } from "./domain";
import { atomicJson } from "./manager";
import type { CatalogEntry } from "./software";

/** Freedesktop main categories, as the app list groups them. */
export const appCategories = {
  AudioVideo: "Audio and video",
  Development: "Development",
  Education: "Education",
  Game: "Games",
  Graphics: "Graphics",
  Network: "Internet",
  Office: "Office",
  Science: "Science",
  System: "System",
  Utility: "Utilities",
} as const;
export type AppCategory = keyof typeof appCategories;

export interface AppInfo {
  /** AppStream id, such as org.gimp.GIMP. */
  id: string;
  name: string;
  summary: string | null;
  categories: AppCategory[];
  /** A 128 pixel icon on Flathub's media server. */
  icon: string | null;
  homepage: string | null;
  keywords: string[];
}

/** A catalog entry that is an app, with what Flathub says about it. */
export type AppEntry = CatalogEntry & { app?: AppInfo };

/** Well-known apps shown before anyone picks a category. */
export const featuredApps = [
  "org.mozilla.firefox",
  "org.libreoffice.LibreOffice",
  "org.mozilla.thunderbird",
  "org.gimp.GIMP",
  "org.inkscape.Inkscape",
  "org.kde.krita",
  "org.videolan.VLC",
  "com.obsproject.Studio",
  "org.audacityteam.Audacity",
  "org.blender.Blender",
  "com.valvesoftware.Steam",
  "org.signal.Signal",
  "com.discordapp.Discord",
  "com.spotify.Client",
  "com.visualstudio.code",
  "org.keepassxc.KeePassXC.desktop",
];

export const appStreamUrl = "https://dl.flathub.org/repo/appstream/x86_64/appstream.xml.gz";
const iconHost = "https://dl.flathub.org/";

const entities: Record<string, string> = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" };
const text = (value: string) =>
  value
    .replace(/&(#x[0-9a-f]+|#[0-9]+|[a-z]+);/gi, (match, name: string) =>
      name[0] === "#"
        ? String.fromCodePoint(Number.parseInt(name.slice(name[1] === "x" ? 2 : 1), name[1] === "x" ? 16 : 10))
        : (entities[name] ?? match),
    )
    .trim();

/** Untranslated values of a simple element. */
const values = (block: string, tag: string) =>
  [...block.matchAll(new RegExp(`<${tag}(\\s[^>]*)?>([^<]*)</${tag}>`, "g"))]
    .filter(([, attributes = ""]) => !attributes.includes("xml:lang"))
    .map(([, , value]) => text(value));

/** The desktop apps in an AppStream catalogue. */
export function parseAppStream(xml: string): AppInfo[] {
  const apps: AppInfo[] = [];
  for (const [, body] of xml.matchAll(/<component type="desktop(?:-application)?">([\s\S]*?)<\/component>/g)) {
    // Nested elements also hold names; only the app's own are wanted.
    const own = body.replace(/<(developer|description|releases|screenshots|content_rating|branding)\b[\s\S]*?<\/\1>/g, "");
    const id = values(own, "id")[0];
    const name = values(own, "name")[0];
    if (!id || !name) continue;
    // Remote icons have full addresses; cached ones sit in the repository's icon folders.
    const icons = [...own.matchAll(/<icon ([^>]*type="(remote|cached)"[^>]*)>([^<]+)<\/icon>/g)]
      .filter(([, attributes]) => !attributes.includes("scale="))
      .map(([, attributes, type, value]) => {
        const size = Number(attributes.match(/width="(\d+)"/)?.[1] ?? 0);
        const url = type === "remote" ? text(value) : `${iconHost}repo/appstream/x86_64/icons/${size}x${size}/${text(value)}`;
        return { size, url, remote: type === "remote" };
      })
      .filter((icon) => icon.size && icon.url.startsWith(iconHost) && !/\/\.\.|[?#]/.test(icon.url))
      .sort((a, b) => Math.abs(a.size - 128) - Math.abs(b.size - 128) || Number(b.remote) - Number(a.remote));
    apps.push({
      id,
      name,
      summary: values(own, "summary")[0] ?? null,
      categories: [...new Set(values(own, "category").filter((category): category is AppCategory => category in appCategories))],
      icon: icons[0]?.url ?? null,
      homepage: own.match(/<url type="homepage">([^<]+)<\/url>/)?.[1].trim() ?? null,
      keywords: [...new Set(values(own, "keyword").map((keyword) => keyword.toLowerCase()))].slice(0, 12),
    });
  }
  return apps;
}

const site = (url: string | null | undefined) =>
  url
    ?.toLowerCase()
    .replace(/^https?:\/\/(www\.)?/, "")
    .replace(/[?#].*$/, "")
    .replace(/(\/|\.git|\/index\.html?)$/, "") || undefined;
const host = (url: string | null | undefined) => site(url)?.split("/")[0];
const key = (value: string | null | undefined) => value?.toLowerCase().replace(/[^a-z0-9]/g, "") || undefined;
const common = new Set(["with", "your", "from", "that", "this", "the", "and", "for", "app", "application", "tool", "simple", "free", "open", "source", "linux", "based", "client", "program", "software", "desktop", "easy", "fast", "modern"]);
const words = (value: string) => value.toLowerCase().split(/[^a-z0-9]+/).filter((word) => word.length > 3 && !common.has(word));
/** The developer in a reverse domain id: discordapp in com.discordapp.Discord, ahrm in io.github.ahrm.sioyek. */
const developerLabel = (id: string) => {
  const parts = id.toLowerCase().split(".");
  const label = ["github", "gitlab", "codeberg", "sourceforge", "sr"].includes(parts[1]) ? parts[2] : parts[1];
  return label && label.length > 3 && parts.length > 2 ? label : undefined;
};
const variant = /-(bin|unwrapped|wrapped|fhs|beta|nightly|git|full|minimal)$|_\d+$/;

/**
 * Pairs apps with top-level packages: the same homepage, or the same name on
 * the same site. Each app names one package, preferring the plain attribute
 * over binary, unwrapped and versioned variants.
 */
export function matchApps(apps: readonly AppInfo[], entries: readonly CatalogEntry[]): Map<string, AppInfo> {
  const bySite = new Map<string, CatalogEntry[]>();
  const byName = new Map<string, CatalogEntry[]>();
  const add = (map: Map<string, CatalogEntry[]>, name: string | undefined, entry: CatalogEntry) => {
    if (!name) return;
    const list = map.get(name);
    if (!list) map.set(name, [entry]);
    else if (!list.includes(entry)) list.push(entry);
  };
  for (const entry of entries) {
    if (entry.a.includes(".")) continue;
    add(bySite, site(entry.h), entry);
    for (const name of [entry.n, entry.a, entry.p]) add(byName, key(name), entry);
  }
  const matched = new Map<string, AppInfo>();
  for (const app of apps) {
    const names = [key(app.name), key(app.id.replace(/\.desktop$/, "").split(".").at(-1))];
    const named = names.flatMap((name) => byName.get(name ?? "") ?? []);
    const sameHost = (entry: CatalogEntry) => !entry.h || !app.homepage || host(entry.h) === host(app.homepage);
    // The plain package next to a matching variant, such as firefox for firefox-bin.
    const plain = (entry: CatalogEntry) => named.find((other) => other.a === entry.a.replace(variant, ""));
    const bySiteOrHost = [...(bySite.get(site(app.homepage) ?? "") ?? []), ...named.filter(sameHost)];
    let candidates = [...new Set([...bySiteOrHost, ...bySiteOrHost.flatMap((entry) => plain(entry) ?? [])])];
    // When the homepages moved apart, such as discord.com and discordapp.com,
    // the shown name must also end the id, and the package's homepage name
    // the developer or the descriptions share a word.
    if (!candidates.length && names[0] === names[1]) {
      const about = words(`${app.summary ?? ""} ${app.keywords.join(" ")}`);
      // A developer called like the app, as in art.darkly.Darkly, says nothing.
      const developer = developerLabel(app.id);
      const owner = developer !== names[0] ? developer : undefined;
      candidates = named.filter(
        (entry) =>
          key(entry.a) === names[0] &&
          ((owner && site(entry.h)?.includes(owner)) || words(entry.d ?? "").some((word) => about.includes(word))),
      );
    }
    const best = candidates
      .filter((entry) => !matched.has(entry.a))
      .sort(
        (a, b) =>
          Number(variant.test(a.a)) - Number(variant.test(b.a)) ||
          Number(!names.includes(key(a.a))) - Number(!names.includes(key(b.a))) ||
          Number(a.b) - Number(b.b) ||
          a.a.length - b.a.length ||
          a.a.localeCompare(b.a),
      )[0];
    if (best) matched.set(best.a, app);
  }
  return matched;
}

export interface AppStream {
  fetchedAt: string;
  apps: AppInfo[];
}

export const appStreamPath = (config: Config) => join(config.stateDir, "apps", "appstream.json");
export const iconPath = (config: Config, id: string) => join(config.stateDir, "apps", "icons", `${id}.png`);
export const validAppId = (id: string) => /^[A-Za-z0-9][A-Za-z0-9._-]{0,200}$/.test(id);

/** The saved catalogue, if it was downloaded before. */
export function readAppStream(config: Config): AppStream | undefined {
  try {
    return JSON.parse(readFileSync(appStreamPath(config), "utf8")) as AppStream;
  } catch {
    return undefined;
  }
}

/** Downloads and saves Flathub's catalogue; it is about ten megabytes. */
export async function fetchAppStream(config: Config, signal?: AbortSignal): Promise<AppStream> {
  const response = await fetch(appStreamUrl, { signal });
  if (!response.ok) throw new Error(`Flathub answered ${response.status} ${response.statusText}.`);
  const xml = new TextDecoder().decode(Bun.gunzipSync(new Uint8Array(await response.arrayBuffer())));
  const result = { fetchedAt: new Date().toISOString(), apps: parseAppStream(xml) };
  if (!result.apps.length) throw new Error("Flathub's catalogue lists no apps.");
  mkdirSync(join(config.stateDir, "apps"), { recursive: true, mode: 0o700 });
  atomicJson(appStreamPath(config), result);
  return result;
}

/** An app's icon, downloaded once from Flathub's media server and kept. */
export async function appIcon(config: Config, app: AppInfo, signal?: AbortSignal) {
  const path = iconPath(config, app.id);
  if (existsSync(path)) return Bun.file(path);
  if (!app.icon?.startsWith(iconHost) || !validAppId(app.id)) return undefined;
  const response = await fetch(app.icon, { signal, redirect: "error" });
  if (!response.ok || !response.headers.get("content-type")?.startsWith("image/png")) return undefined;
  mkdirSync(join(config.stateDir, "apps", "icons"), { recursive: true, mode: 0o700 });
  await Bun.write(path, await response.arrayBuffer());
  return Bun.file(path);
}
