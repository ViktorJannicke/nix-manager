// Records the Bun version's pure software functions into Fixtures/bun-software-functions.json, which SoftwareTests
// compares with the C# port. Run from the repository root in the development shell:
//
//     bun runic/NixManager.Tests/Fixtures/record-bun-software.ts
import { posix } from "node:path";
import { splitAttr } from "../../../src/edits";
import { software } from "../../../src/software-cli";
import { importedFiles, previousVersion } from "../../../src/software";

const names = [
  "git-2.50.0", "git-2.50.0-doc", "ripgrep-14.1.1", "fzf-0.65.2", "fzf-man", "hello-2.12.2", "hello-2.12.1",
  "python3-3.13.5", "python3-3.13.5-env", "krita-5.2.11", "nodejs-22.0.0", "nodejs-20.0.0",
];
const paths = new Set(["/nix/store/00000000000000000000000000000000-ripgrep-14.1.1"]);
const items = [
  { out: "/nix/store/00000000000000000000000000000000-ripgrep-14.1.1", name: "ripgrep", version: "14.1.1" },
  { out: "/nix/store/11111111111111111111111111111111-git-2.51.0", name: "git", version: "2.51.0" },
  { out: "/nix/store/22222222222222222222222222222222-git-2.50.0", name: "git", version: "2.50.0" },
  { out: "/nix/store/33333333333333333333333333333333-python3-3.13.5", name: "python3", version: "3.13.5" },
  { out: "/nix/store/44444444444444444444444444444444-hello-2.12.3", name: "hello", version: "2.12.3" },
  { out: "/nix/store/55555555555555555555555555555555-new-1.0", name: "new", version: "1.0" },
  { out: "/nix/store/66666666666666666666666666666666-fzf", name: "fzf", version: null },
  { out: "/nix/store/77777777777777777777777777777777-nodejs-24.0.0", name: "nodejs", version: "24.0.0" },
];
const modules = [
  { file: "flake.nix", imports: [] },
  { file: "hosts/test/default.nix", imports: ["../../modules/nixos", "./hardware.nix"] },
  { file: "hosts/test/hardware.nix", imports: [] },
  { file: "modules/nixos/default.nix", imports: ["./desktop.nix", "../shared/fonts.nix", "missing.nix"] },
  { file: "modules/nixos/desktop.nix", imports: [] },
  { file: "modules/shared/fonts.nix", imports: ["../nixos/desktop.nix"] },
  { file: "home.nix", imports: ["./modules/home"] },
  { file: "modules/home/default.nix", imports: [] },
  { file: "unused.nix", imports: ["./modules/home/default.nix"] },
];
const result = {
  previousVersion: items.map((item) => {
    const value = previousVersion(paths, names, item);
    return { item, previous: value === undefined ? "undefined" : value };
  }),
  importedFiles: [["hosts/test/default.nix"], ["home.nix"], ["modules/shared/fonts.nix"], []].map((used) => ({
    used,
    result: [...importedFiles(modules, used)].sort(),
  })),
  splitAttr: ["pkgs.fzf", "pkgs.", "pkgs", "pkgs.kdePackages.kate", 'pkgs."my-pkg".x', 'pkgs."a\\"b"', "pkgs..x", "1pkgs.x", "pkgs.x-y'z", "pkgs.x.", ""].map(
    (text) => ({ text, result: splitAttr(text) ?? null }),
  ),
  software: [
    ["software", "pending"],
    ["--json", "software", "pending", "--diff"],
    ["--flake", "/x", "--host", "h", "software", "add", "fzf", "--system"],
    ["--json", "switch"],
    ["software"],
    ["--limit", "3", "software", "search", "git"],
    ["--flake"],
    ["home", "software"],
  ].map((args) => ({ args, result: software(args) ?? null })),
  normalize: ["a/b/../c", "./a//b/", "../../x", "a/..", "/a/../../b", "", ".", "a/./b/.", "x/../../y/"].map((path) => ({
    path,
    result: posix.normalize(path),
  })),
  join: [["hosts/test", "../../modules/nixos"], [".", "./home.nix"], ["", ""], ["a", "/b"], ["modules", "x/"]].map((parts) => ({
    parts,
    result: posix.join(...parts),
  })),
  dirname: ["a/b.nix", "b.nix", "/x", "/", "a/b/", "a//b", ""].map((path) => ({ path, result: posix.dirname(path) })),
};
await Bun.write(new URL("./bun-software-functions.json", import.meta.url), JSON.stringify(result, null, 2) + "\n");
console.log("Recorded bun-software-functions.json.");
