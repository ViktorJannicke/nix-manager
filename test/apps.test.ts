import { expect, test } from "bun:test";
import { matchApps, parseAppStream } from "../src/apps";
import { searchCatalog, type CatalogEntry } from "../src/software";

const component = (body: string, type = "desktop-application") => `<component type="${type}">${body}</component>`;
const xml = `<?xml version="1.0"?><components>
${component(`<id>org.gimp.GIMP</id>
  <name>GNU Image Manipulation Program</name><name xml:lang="de">GNU-Bildbearbeitung</name>
  <summary>Create images &amp; edit photographs</summary>
  <developer id="org.gimp"><name>The GIMP team</name></developer>
  <description><p>Not a <name>name</name></p></description>
  <icon type="cached" width="128" height="128">org.gimp.GIMP.png</icon>
  <icon type="remote" width="64" height="64">https://dl.flathub.org/media/gimp-64.png</icon>
  <icon type="remote" width="128" height="128">https://dl.flathub.org/media/gimp-128.png</icon>
  <icon type="remote" width="128" height="128" scale="2">https://dl.flathub.org/media/gimp-256.png</icon>
  <url type="homepage">https://www.gimp.org/</url>
  <categories><category>Graphics</category><category>2DGraphics</category></categories>
  <keywords><keyword>Photo</keyword><keyword xml:lang="de">Foto</keyword></keywords>`)}
${component(`<id>org.example.Elsewhere</id><name>Elsewhere</name><icon type="remote" width="128">https://example.org/icon.png</icon>`, "desktop")}
${component(`<id>org.mozilla.firefox</id><name>Firefox</name><icon type="cached" width="64" height="64">org.mozilla.firefox.png</icon>`)}
${component(`<id>org.example.Font</id><name>A font</name>`, "font")}
</components>`;

test("the AppStream catalogue gives each desktop app's own name, summary, icon and main categories", () => {
  const [gimp, elsewhere, firefox, ...rest] = parseAppStream(xml);
  expect(rest).toEqual([]);
  expect(gimp).toEqual({
    id: "org.gimp.GIMP",
    name: "GNU Image Manipulation Program",
    summary: "Create images & edit photographs",
    categories: ["Graphics"],
    icon: "https://dl.flathub.org/media/gimp-128.png",
    homepage: "https://www.gimp.org/",
    keywords: ["photo"],
  });
  // Icons are only taken from Flathub's media server.
  expect(elsewhere).toMatchObject({ id: "org.example.Elsewhere", icon: null, categories: [] });
  expect(firefox.icon).toBe("https://dl.flathub.org/repo/appstream/x86_64/icons/64x64/org.mozilla.firefox.png");
});

const entry = (a: string, fields: Partial<CatalogEntry> = {}): CatalogEntry => ({
  a,
  n: a,
  v: "1",
  d: null,
  h: null,
  p: null,
  l: [],
  f: true,
  b: false,
  ...fields,
});
const app = (id: string, name: string, homepage: string | null, summary = "") => ({
  id,
  name,
  summary,
  categories: [],
  icon: null,
  homepage,
  keywords: [],
});

test("apps match one package by homepage or by name on the same site", () => {
  const entries = [
    entry("firefox", { h: "https://www.mozilla.com/firefox" }),
    entry("firefox-bin", { h: "https://www.mozilla.org/firefox/" }),
    entry("moonlight", { h: "https://moonlight-mod.github.io", d: "Discord client modification" }),
    entry("moonlight-qt", { h: "https://moonlight-stream.org" }),
    entry("discord", { h: "https://discordapp.com/", d: "Voice and text chat for gamers" }),
    entry("flow", { h: "https://flow.org/", d: "Static type checker for JavaScript" }),
    entry("python3Packages.gimp", { h: "https://www.gimp.org" }),
    entry("darkly", { h: "https://github.com/Bali10050/Darkly", d: "Modern style for Qt applications" }),
  ];
  const matched = matchApps(
    [
      app("org.mozilla.firefox", "Firefox", "https://www.mozilla.org/firefox"),
      app("com.moonlight_stream.Moonlight", "Moonlight", "https://moonlight-stream.org"),
      app("com.discordapp.Discord", "Discord", "https://discord.com", "Talk, play, hang out"),
      app("com.flow_browser.Flow", "Flow", "https://www.flow-browser.com", "A fast browser"),
      app("org.gimp.GIMP", "GIMP", "https://www.gimp.org"),
      app("art.darkly.Darkly", "Darkly", "https://darkly.art", "Forbidden editor for artists"),
    ],
    entries,
  );
  expect(Object.fromEntries([...matched].map(([attr, item]) => [attr, item.id]))).toEqual({
    // The plain package rather than the variant whose homepage matched.
    firefox: "org.mozilla.firefox",
    // A matching homepage wins over a matching name.
    "moonlight-qt": "com.moonlight_stream.Moonlight",
    // The homepage moved, but the package's homepage names the id's developer.
    discord: "com.discordapp.Discord",
  });
});

test("search finds apps by their shown name and summary, and puts them first", () => {
  const entries = [entry("gimp", { d: "GNU Image Manipulation Program" }), entry("imagetool", { d: "Edit a photo from the command line" })];
  const apps = new Map([["gimp", { ...app("org.gimp.GIMP", "GIMP", null, "Edit photographs"), keywords: ["photo"] }]]);
  expect(searchCatalog(entries, "photo", 10, {}, apps).map((item) => item.a)).toEqual(["gimp", "imagetool"]);
  expect(searchCatalog(entries, "photo", 10).map((item) => item.a)).toEqual(["imagetool"]);
});
