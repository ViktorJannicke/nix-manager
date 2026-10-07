import { mkdir, readFile, readdir } from "node:fs/promises";
import { extname, join, relative } from "node:path";
import { build as buildVite } from "vite";
await mkdir("dist", { recursive: true });
await buildVite({ configFile: new URL("vite.config.ts", import.meta.url).pathname });
const mime = (path: string) =>
  ({
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".svg": "image/svg+xml",
  })[extname(path)] || "application/octet-stream";
const root = new URL("ui-dist", import.meta.url).pathname;
const walk = async (directory: string): Promise<string[]> => {
  const entries = await readdir(directory, { withFileTypes: true });
  return (
    await Promise.all(
      entries.map((entry) => {
        const path = join(directory, entry.name);
        return entry.isDirectory() ? walk(path) : Promise.resolve([path]);
      }),
    )
  ).flat();
};
const assets = Object.fromEntries(
  await Promise.all(
    (await walk(root)).map(async (path) => {
      const name = relative(root, path);
      return [
        name === "index.html" ? "/" : `/${name}`,
        { body: await readFile(path, "utf8"), type: mime(path) },
      ];
    }),
  ),
);
const result = await Bun.build({
  entrypoints: ["src/main.ts"],
  outdir: "dist",
  target: "bun",
  external: ["dbus-next", "vite"],
  define: {
    NIX_MANAGER_UI_ASSETS: JSON.stringify(JSON.stringify(assets)),
  },
});
if (!result.success) throw new Error(result.logs.join("\n"));
