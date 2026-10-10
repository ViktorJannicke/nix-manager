import { expect, test } from "bun:test";
import { importedFiles, previousVersion, searchCatalog } from "../src/software";

test("files imported by used files are used too, following directories", () => {
  const modules = [
    { file: "hosts/desktop/home.nix", imports: ["../../modules/home"] },
    { file: "hosts/laptop/home.nix", imports: ["../../modules/home", "./battery.nix"] },
    { file: "hosts/laptop/battery.nix", imports: [] },
    { file: "modules/home/default.nix", imports: ["./programs", "./missing.nix"] },
    { file: "modules/home/programs/default.nix", imports: ["./git.nix"] },
    { file: "modules/home/programs/git.nix", imports: [] },
  ];
  expect([...importedFiles(modules, ["hosts/desktop/home.nix"])].sort()).toEqual([
    "hosts/desktop/home.nix",
    "modules/home/default.nix",
    "modules/home/programs/default.nix",
    "modules/home/programs/git.nix",
  ]);
  // Importing shared modules says nothing about the importer.
  expect(importedFiles(modules, ["modules/home/programs/git.nix"]).has("hosts/laptop/home.nix")).toBe(false);
});

test("installed packages compare with the running generation", () => {
  const running = ["/nix/store/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-btop-1.4.6", "/nix/store/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-git-2.50.0"];
  const paths = new Set(running);
  const names = ["btop-1.4.6", "git-2.50.0", "git-2.50.0-doc"];
  const item = (name: string, version: string, out = `/nix/store/cccccccccccccccccccccccccccccccc-${name}-${version}`) => ({ out, name, version });
  expect(previousVersion(paths, names, item("btop", "1.4.7"))).toBe("1.4.6");
  expect(previousVersion(paths, names, item("git", "2.50.0"))).toBeUndefined();
  expect(previousVersion(paths, names, item("fd", "10.0"))).toBeNull();
  expect(previousVersion(paths, names, item("btop", "1.4.6", running[0]))).toBeUndefined();
});

test("search can leave out unfree and broken packages", () => {
  const entry = (a: string, f: boolean, b: boolean) => ({ a, n: a, v: "1", d: null, h: null, p: null, l: [], f, b });
  const entries = [entry("steam", false, false), entry("steam-run", true, false), entry("steamcmd", false, true)];
  expect(searchCatalog(entries, "steam").map((item) => item.a)).toEqual(["steam", "steam-run", "steamcmd"]);
  expect(searchCatalog(entries, "steam", 60, { free: true }).map((item) => item.a)).toEqual(["steam-run"]);
  expect(searchCatalog(entries, "steam", 60, { working: true }).map((item) => item.a)).toEqual(["steam", "steam-run"]);
});
