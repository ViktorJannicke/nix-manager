import { expect, test } from "bun:test";
import { importedFiles } from "../src/software";

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
