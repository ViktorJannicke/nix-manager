/**
 * Records what the Bun version's edit engine (src/packages.ts, src/options.ts, src/draft.ts, src/changes.ts)
 * returns for a corpus of Nix sources and edits, into Fixtures/bun-edits.json. EditDifferentialTests replays every
 * case with the C# port and requires the same result or error message.
 *
 * Run from the repository root in the development shell (NIX_MANAGER_NIX_GRAMMAR set):
 *
 *     bun runic/NixManager.Tests/Fixtures/record-bun-edits.ts
 *
 * With `--files DIR --out FILE`, it records moduleShape, packageLists and a few edits of every .nix file under
 * DIR instead (read-only), for a one-off comparison against a real flake; point NIX_MANAGER_BUN_EDITS at FILE to
 * replay it. Such a recording holds the files' text: keep it out of the repository.
 */
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";
import {
  changesScope,
  describeChange,
  dropChange,
  featureModule,
  freePath,
  mergeChanges,
  newFileProblem,
  oneStepProblem,
  optionTopic,
  origin,
  rankImporters,
  rankModules,
  showValue,
} from "../../../src/changes";
import { changeHash, diffLines, importPath, renderDraft, sourcePath, type Change } from "../../../src/draft";
import { addImport, moduleShape, renderValue, setOption, startList, unsetOption } from "../../../src/options";
import { addPackage, appendItem, packageLists, removePackage } from "../../../src/packages";
import { parseNix } from "../../../src/syntax";

type Case = { op: string; args: unknown[]; result?: unknown; error?: string };

const lists = async (source: string) => {
  const tree = await parseNix(source);
  try {
    return packageLists(tree.rootNode);
  } finally {
    tree.delete();
  }
};

const run = async (op: string, args: any[]): Promise<unknown> => {
  switch (op) {
    case "packageLists": return lists(args[0]);
    case "moduleShape": return moduleShape(args[0]);
    case "addPackage": return addPackage(args[0], args[1], args[2] ?? undefined);
    case "removePackage": return removePackage(args[0], args[1]);
    case "appendItem": return appendItem(args[0], args[1], args[2] ?? undefined, args[3]);
    case "setOption": return setOption(args[0], args[1], args[2]);
    case "startList": return startList(args[0], args[1], args[2]);
    case "addImport": return addImport(args[0], args[1]);
    case "unsetOption": return unsetOption(args[0], args[1]);
    case "renderValue": return renderValue(args[0]);
    case "diffLines": return diffLines(args[0], args[1], args[2] ?? undefined);
    case "importPath": return importPath(args[0], args[1]);
    case "sortLocale": return [...args[0]].sort((a: string, b: string) => a.localeCompare(b));
    case "rankImporters": return rankImporters(args[0], args[1], args[2] ?? undefined);
    case "rankModules": return rankModules(args[0], args[1], args[2], args[3] ?? undefined);
    case "optionTopic": return optionTopic(args[0]);
    case "freePath": return freePath(new Set(args[0]), args[1], args[2] ?? undefined, args[3]);
    case "featureModule": return featureModule(args[0], args[1]) ?? null;
    case "newFileProblem": return newFileProblem(new Set(args[0]), args[1]) ?? null;
    case "origin": return origin(args[0]);
    case "showValue": return showValue(args[0]);
    case "describeChange": return describeChange(args[0]);
    case "mergeChanges": return mergeChanges(args[0], args[1], args[2] ? () => true : undefined);
    case "dropChange": return dropChange(args[0], args[1]);
    case "changesScope": return changesScope(args[0]);
    case "oneStepProblem": return oneStepProblem(args[0]) ?? null;
    case "renderDraft": {
      // args: files to create (path → text, or "->target" for a symbolic link), changes.
      const flake = mkdtempSync(join(tmpdir(), "nix-manager-edits-"));
      try {
        for (const [name, text] of Object.entries(args[0] as Record<string, string>)) {
          const path = join(flake, name);
          mkdirSync(join(path, ".."), { recursive: true });
          if (text.startsWith("->")) symlinkSync(text.slice(2), path);
          else writeFileSync(path, text);
        }
        const { files, problems } = await renderDraft(flake, args[1]);
        return {
          files: files.map((file) => ({
            path: file.path,
            baseHash: file.baseHash,
            created: file.created ?? null,
            before: file.before,
            after: file.after,
            hunks: file.hunks,
          })),
          problems,
          hash: changeHash(args[1]),
        };
      } finally {
        rmSync(flake, { recursive: true, force: true });
      }
    }
    case "sourcePath": {
      const flake = mkdtempSync(join(tmpdir(), "nix-manager-edits-"));
      try {
        return sourcePath(flake, args[0]).replace(flake, "<flake>");
      } finally {
        rmSync(flake, { recursive: true, force: true });
      }
    }
  }
  throw new Error(`Unknown operation ${op}`);
};

const record = async (op: string, args: unknown[]): Promise<Case> => {
  try {
    // JSON round trip: undefined members drop out, as the C# side writes them.
    return { op, args, result: JSON.parse(JSON.stringify((await run(op, args)) ?? null)) };
  } catch (error) {
    return { op, args, error: error instanceof Error ? error.message : String(error) };
  }
};

// Sources each edit of the matrix runs against.
const sources: string[] = [
  // Category module with a with-scope list and a trailing comment.
  `mkPackageModule {\n  name = "media";\n  default = false;\n  packages = with pkgs; [\n    gimp3-with-plugins\n    easyeffects # audio effects\n    vorta\n  ];\n}\n`,
  // Nested with scopes, ++ and lib.optionals.
  `{\n  packages =\n    (with pkgs.kdePackages; [\n      kate\n      dolphin\n    ])\n    ++ (with pkgs; [ partitionmanager ])\n    ++ lib.optionals config.extra [ zoom ];\n}\n`,
  // Home module with let, nested sets, a comment and a computed value.
  `{ config, pkgs, ... }:\nlet\n  name = "alexis";\nin\n{\n  # Shell\n  programs.git = {\n    enable = true;\n    lfs.enable = false; # large files\n  };\n  programs.direnv.enable = true;\n  home.username = name;\n  home.packages = [\n    pkgs.git\n    pkgs.gh\n  ];\n}\n`,
  // Options beside config.
  `{ lib, ... }: {\n  options.my.flag = lib.mkEnableOption "flag";\n  config = {\n    services.a.enable = true;\n    environment.systemPackages = with pkgs; [ git htop ];\n  };\n}\n`,
  // Conditional config.
  `{ lib, config, ... }: {\n  options.my.flag = lib.mkEnableOption "flag";\n  config = lib.mkIf config.my.flag { services.a.enable = true; };\n}\n`,
  // Empty modules, one-line and multi-line.
  `{ ... }: { }\n`,
  `{ pkgs, ... }:\n{\n}\n`,
  `{ }`,
  // One-line set with inline bindings and items.
  `{ home.packages = with pkgs; [ git fzf ]; programs.git.enable = true; }`,
  // Closing bracket on the last item's line, and an empty multi-line list.
  `{\n  home.packages = with pkgs; [\n    fzf ];\n  packages = with pkgs; [\n  ];\n}\n`,
  // Quoted attributes, rec sets and inherit.
  `{ pkgs, ... }:\nrec {\n  inherit (pkgs) lib;\n  "programs".git.enable = true;\n  programs."my-tool".enable = false;\n  home.packages = [ pkgs."7zz" pkgs.kdePackages.kate ];\n  xdg = { enable = true; configFile."a b".text = "x"; };\n}\n`,
  // Imports: plain, conditional and computed.
  `{ lib, ... }:\n{\n  imports = [\n    ./git.nix # vcs\n    ../shared\n  ];\n  options.x = 1;\n}\n`,
  `{ imports = lib.optionals x [ ./a.nix ]; }`,
  `{ imports = map f xs; services.x.enable = true; }`,
  // A comment before the function, parentheses and a let in the body.
  `# Shared host settings\n\n{ pkgs, ... }:\n(let x = 1; in\n{\n  imports = [ ./a.nix ];\n  services.x.enable = true;\n  services.x.port = 80;\n  programs.git.enable = x;\n})\n`,
  // Values of every literal kind, and a split definition.
  `{\n  a.int = -3;\n  a.float = 1.5;\n  a.path = ./x;\n  a.string = "s\${x}";\n  a.plain = ''\n    text\n  '';\n  a.list = [ 1 "a" pkgs.git ];\n  a.set = { b = true; };\n  a.null = null;\n  programs.git.enable = true;\n  programs.git.lfs.enable = true;\n}\n`,
  // Syntax error.
  `{ home.packages = [ `,
  // Not a module.
  `mkPackageModule { name = "x"; }`,
  // Tabs, CRLF line endings and Unicode text.
  `{\r\n\thome.packages = with pkgs; [\r\n\t\tgit # Grüße\r\n\t];\r\n\tprograms.git.enable = true;\r\n}\r\n`,
  // Settings after the last binding on the closing line.
  `{\n  programs.git.enable = true; }\n`,
  `{\n  # only a comment\n}\n`,
  // Duplicate items.
  `{ packages = with pkgs; [ git ]; home.packages = [ pkgs.git ]; }`,
];

const edits: [string, unknown[]][] = [
  ["addPackage", ["pkgs.bat"]],
  ["addPackage", ["pkgs.git"]],
  ["addPackage", ["pkgs.kdePackages.kcalc"]],
  ["addPackage", ['pkgs."7zz"']],
  ["addPackage", ["pkgs.bat", ["packages", "environment.systemPackages"]]],
  ["removePackage", ["pkgs.git"]],
  ["removePackage", ["pkgs.gh"]],
  ["removePackage", ["pkgs.fzf"]],
  ["removePackage", ["pkgs.kdePackages.kate"]],
  ["setOption", ["programs.git.enable", false]],
  ["setOption", ["programs.git.lfs.enable", true]],
  ["setOption", ["programs.git.signing.key", "ABC"]],
  ["setOption", ["services.x.port", 8080]],
  ["setOption", ["services.x.enable", true]],
  ["setOption", ["home.username", "x"]],
  ["setOption", ["programs", { git: { enable: true } }]],
  ["setOption", ["a.int", -0.000000125]],
  ["setOption", ["a.list", ["x", 2, null, { "1": true, b: [] }]]],
  ["setOption", ["xdg.configFile", {}]],
  ["setOption", ["programs.${x}.enable", true]],
  ["unsetOption", ["programs.git.enable"]],
  ["unsetOption", ["programs.git.lfs.enable"]],
  ["unsetOption", ["services.x.port"]],
  ["unsetOption", ["programs.fzf.enable"]],
  ["startList", ["home.packages", "pkgs.fzf"]],
  ["startList", ["environment.systemPackages", "pkgs.fzf"]],
  ["addImport", ["./x.nix"]],
  ["addImport", ["./git.nix"]],
  ["addImport", ["../a b.nix"]],
];

const corpus = async (): Promise<Case[]> => {
  const cases: Case[] = [];
  for (const source of sources) {
    cases.push(await record("packageLists", [source]));
    cases.push(await record("moduleShape", [source]));
    for (const [op, args] of edits) cases.push(await record(op, [source, ...args]));
  }
  for (const [source, list, last, text] of [
    ["x = [ a ];", { start: 4, end: 9 }, { start: 6, end: 7 }, "b"],
    ["x = [];", { start: 4, end: 6 }, null, "b"],
    ["x = [\n  a # c\n];", { start: 4, end: 15 }, { start: 8, end: 9 }, "b"],
    ["x = [\n];", { start: 4, end: 7 }, null, "b"],
    ["\nx = [\n    ];", { start: 5, end: 12 }, null, "b"],
  ] as const)
    cases.push(await record("appendItem", [source, list, last, text]));
  for (const value of [
    true, false, null, 0, -0, 3, -3, 1.5, -1.5, 0.1, 1e21, 1.5e-7, -2.5e-7, 1e-7, 123456789012345680000, 5e-324,
    0.0000012345678901234567, -987654321.123456789e5, 1e-6, 999999999999999900000,
    "", 'a "b" ${c}\n\t\r\\', "Grüße", [], ["a", ["b"], {}], {}, { enable: true, "a b": 4, "2": 1, "10": 2, "01": 3, "-1": 4 },
  ])
    cases.push(await record("renderValue", [value]));
  for (const [before, after, context] of [
    ["a\nb\nc\nd\ne\nf\ng\nh", "a\nB\nc\nd\ne\nf\ng\nh\ni", 1],
    ["a\nb\nc\nd\ne\nf\ng\nh", "a\nB\nc\nd\ne\nf\ng\nh\ni", null],
    ["", "{ pkgs, ... }:\n{\n}\n", null],
    ["x\ny\n", "", null],
    ["same\n", "same\n", null],
    ["a\nb\na\nb\na\n", "b\na\nb\na\nc\n", 0],
    ["1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12", "1\n2\nX\n4\n5\n6\n7\n8\n9\nY\n11\n12", 3],
  ] as const)
    cases.push(await record("diffLines", [before, after, context]));
  for (const [importer, file] of [
    ["modules/default.nix", "modules/programs/fzf.nix"],
    ["modules/home/default.nix", "modules/fzf.nix"],
    ["default.nix", "a/b.nix"],
    ["a/b/c.nix", "d.nix"],
    ["a.nix", "a.nix"],
  ])
    cases.push(await record("importPath", [importer, file]));
  for (const file of ["a.nix", "m/a.nix", "/etc/a.nix", "../a.nix", "..a.nix", "a.txt", "./a.nix", "a//b.nix", "m/../a.nix", "m/./a.nix", "m/", ""])
    cases.push(await record("sourcePath", [file]));

  const paths = [
    "modules/home/programs/git.nix", "modules/home/programs/Git.nix", "modules/home/programs/git-lfs.nix",
    "modules/home/programs/git_lfs.nix", "modules/home/programs/git.lfs.nix", "modules/home/programs/gitk.nix",
    "modules/home/default.nix", "modules/home-manager/a.nix", "modules/home/2fa.nix", "modules/home/10.nix",
    "modules/home/+x.nix", "modules/home/a b.nix", "Modules/x.nix", "a.nix", "B.nix", "_a.nix", "-a.nix",
    "a/b.nix", "a-b.nix", "a.b.nix", "ab.nix", "a~.nix", "a$.nix", "a@.nix", "a(1).nix", "a,b.nix", "a;b.nix",
  ];
  cases.push(await record("sortLocale", [paths]));
  cases.push(await record("sortLocale", [["b", "B", "a", "A", "ä", "Ä", "é", "e", "f", "Ö", "o", "z", "Z", "10", "9", "a1", "a-1", "a_1", "a.1", "a 1", "aa", "a"]]));
  cases.push(await record("rankImporters", [
    ["hosts/desktop/home.nix", "modules/home/default.nix", "modules/home/programs/default.nix", "modules/home/programs/git.nix"],
    "modules/home/programs/fzf.nix", null]));
  cases.push(await record("rankImporters", [paths, "modules/home/programs/fzf.nix", null]));
  cases.push(await record("rankImporters", [
    ["hosts/desktop/system.nix", "profiles/workstation.nix", "default.nix"], "modules/nixos/services/flatpak.nix",
    { "profiles/workstation.nix": ["modules/nixos/services/ssh.nix", "modules/nixos/services/podman.nix"],
      "hosts/desktop/system.nix": ["profiles/workstation.nix", "modules/nixos/services/libvirt.nix"] }]));
  const modules = [
    { file: "hosts/desktop/home.nix", names: ["home.username", "programs.git"], active: true },
    { file: "modules/home/programs/git.nix", names: ["programs.git"], active: true },
    { file: "modules/nixos/services/ssh.nix", names: ["services.openssh"], active: null },
    { file: "modules/home/programs/fzf.nix", names: [], active: false },
    { file: "pkgs/thing/default.nix", names: [], active: null },
    { file: "programs/default.nix", names: ["programs.fzf"], active: null },
    { file: "system/a.nix", names: [], active: null },
    { file: "tests/x.nix", names: [], active: true },
    { file: "Programs/b.nix", names: [], active: null },
  ];
  for (const [scope, option, last] of [
    ["home", "programs.git.enable", null], ["system", "services.openssh.enable", null],
    ["home", "programs.fzf.enable", "programs/default.nix"], ["system", "programs.x", "Programs/b.nix"],
  ])
    cases.push(await record("rankModules", [modules, scope, option, last]));
  for (const option of ["programs.fzf.enable", "programs", "services.x", "home.packages", "security.sudo.enable", "programs."])
    cases.push(await record("optionTopic", [option]));
  const known = ["modules/home/programs/fzf.nix", "fzf.nix", "x/module.nix"];
  for (const [name, near, fallback] of [
    ["fzf", "modules/home/programs/git.nix", "modules/home"], ["Virtual Machines", null, "modules/nixos"],
    ["fzf", null, ""], ["fzf", "", "x"], ["***", null, "x"], ["--a--", "a.nix", "x"], ["Grüße C++", null, "x"],
  ])
    cases.push(await record("freePath", [known, name, near, fallback]));
  for (const [list, name] of [
    [["bluetooth", "podman", "ssh"].map((n) => ({ file: `modules/nixos/services/${n}.nix`, active: true })), "podman"],
    [["bluetooth", "podman", "ssh"].map((n) => ({ file: `modules/nixos/services/${n}.nix`, active: true })), "flatpak"],
    [[{ file: "configuration.nix", active: true }, { file: "hardware.nix", active: true }], "flatpak"],
    [[{ file: "old/flatpak.nix", active: false }, { file: "configuration.nix", active: true }], "flatpak"],
    [[{ file: "a/x.nix", active: null }, { file: "a/y.nix", active: true }, { file: "a/z.nix", active: true }, { file: "a/w.nix", active: true }], "q"],
    [[], "flatpak"],
  ] as const)
    cases.push(await record("featureModule", [list, name]));
  for (const file of ["modules/home/programs/fzf.nix", "../x.nix", "modules/x.txt", "modules/x.nix", ".x.nix", "a/.b.nix", "a//b.nix", "a b.nix", "x.nix\n"])
    cases.push(await record("newFileProblem", [known, file]));
  for (const file of [
    "/nix/store/0123456789abcdefghijklmnopqrstuv-source/nixos/modules/services/x.nix",
    "/nix/store/0123456789abcdefghijklmnopqrstuv-source/modules/programs/git.nix",
    "/nix/store/0123456789abcdefghijklmnopqrstuv-home-manager/programs/git.nix",
    "/nix/store/short-source/modules/a.nix", "modules/a.nix",
  ])
    cases.push(await record("origin", [file]));
  for (const value of [true, false, null, [], ["a", 1, null, true, ["b", "c"], { a: 1 }], "text", 3.5, -1, { b: 1, "1": 2 }])
    cases.push(await record("showValue", [value]));

  const add: Change = { kind: "add-package", scope: "home", file: "a.nix", attr: "pkgs.btop" };
  const remove: Change = { ...add, kind: "remove-package" };
  const set = (value: unknown, label?: string): Change =>
    ({ kind: "set-option", scope: "home", file: "a.nix", option: "programs.fzf.enable", value, ...(label === undefined ? {} : { label }) }) as Change;
  const unset: Change = { kind: "unset-option", scope: "system", file: "b.nix", option: "programs.fzf.enable" };
  const create: Change = { kind: "create-module", scope: "home", file: "m/fzf.nix", importer: "m/default.nix" };
  for (const change of [add, remove, { ...add, attr: "pkgs.kdePackages.kate" }, { ...add, attr: "xpkgs.a" }, unset, create,
    set(true), set(false), set(null), set([]), set(["a", 2]), set("x"), set(4), set({ a: 1 }),
    set(true, "Shell"), set(false, "Shell"), set(null, "Shell"), set([], "Shell"), set(4, "Shell"), set(true, "")])
    cases.push(await record("describeChange", [change]));
  for (const [current, next, unchanged] of [
    [[add], [add], false], [[add], [remove], false], [[set(true)], [set(false)], false], [[set(true)], [set(false)], true],
    [[create], [create, { ...set(true), file: create.file }], false], [[set(true), add], [unset, { ...unset, scope: "home" }], false],
    [[add, { ...add, file: "b.nix" }], [{ ...remove, file: "b.nix" }], false],
  ] as const)
    cases.push(await record("mergeChanges", [current, next, unchanged]));
  const into = { ...set(true), file: create.file };
  for (const index of [0, 1, 2, 5]) cases.push(await record("dropChange", [[add, create, into], index]));
  for (const changes of [[], [add], [add, { ...add, scope: "system" }], [unset], [unset, unset]])
    cases.push(await record("changesScope", [changes]));
  for (const changes of [
    [], [add, remove, create], [{ ...add, scope: "system" }],
    [{ kind: "set-option", scope: "home", file: "a.nix", option: "nixpkgs.config.allowUnfree", value: true }],
    [{ kind: "set-option", scope: "home", file: "a.nix", option: "nixpkgs.config.allowUnfree", value: false }], [set(true)], [unset],
  ])
    cases.push(await record("oneStepProblem", [changes]));

  // Drafts against files on disk.
  const apps = "{ pkgs, ... }: {\n  home.packages = with pkgs; [\n    git\n  ];\n}\n";
  const btop: Change = { kind: "add-package", scope: "home", file: "modules/apps.nix", attr: "pkgs.btop" };
  const fzf: Change = { kind: "create-module", scope: "home", file: "modules/programs/fzf.nix", importer: "modules/apps.nix" };
  const drafts: [Record<string, string>, Change[]][] = [
    [{ "modules/apps.nix": apps }, [
      btop, { ...btop, attr: "pkgs.git" },
      { kind: "set-option", scope: "home", file: "modules/apps.nix", option: "programs.bat.enable", value: true },
      { ...btop, file: "../outside.nix" }, { ...btop, file: "missing.nix" }, { ...btop, file: "modules" },
    ]],
    [{ "modules/apps.nix": apps }, [
      fzf, { kind: "set-option", scope: "home", file: fzf.file, option: "programs.fzf.enable", value: true },
      { kind: "add-package", scope: "home", file: fzf.file, attr: "pkgs.fd" }, fzf, { ...fzf, file: "modules/apps.nix" },
      { ...fzf, file: "x y.nix" }, { ...fzf, file: "self.nix", importer: "self.nix" }, { ...fzf, file: "other.nix", importer: "nope.nix" },
    ]],
    [{ "a.nix": "{ pkgs, ... }:\n{\n}\n", "b.nix": "mkPackageModule { name = \"b\"; }\n" }, [
      { kind: "add-package", scope: "system", file: "a.nix", attr: "pkgs.htop" },
      { kind: "add-package", scope: "home", file: "a.nix", attr: "pkgs.fd" },
      { kind: "add-package", scope: "home", file: "b.nix", attr: "pkgs.fd" },
      { kind: "remove-package", scope: "home", file: "a.nix", attr: "pkgs.fd" },
      { kind: "unset-option", scope: "home", file: "a.nix", option: "x.y" },
    ]],
    [{ "a.nix": "{ x = 1; }\n", "link.nix": "->..", "inner.nix": "->a.nix" }, [
      { kind: "set-option", scope: "home", file: "link.nix", option: "x", value: 2 },
      { kind: "set-option", scope: "home", file: "inner.nix", option: "x", value: 3, label: "Inner" },
      { kind: "set-option", scope: "home", file: "a.nix", option: "x", value: 1 },
    ]],
  ];
  for (const [files, changes] of drafts) cases.push(await record("renderDraft", [files, changes]));
  return cases;
};

const walk = (directory: string): string[] =>
  readdirSync(directory).flatMap((name) => {
    if (name.startsWith(".")) return [];
    const path = join(directory, name);
    const stat = statSync(path, { throwIfNoEntry: false });
    if (!stat) return [];
    if (stat.isDirectory()) return walk(path);
    return name.endsWith(".nix") ? [path] : [];
  });

const filesCorpus = async (root: string): Promise<Case[]> => {
  const cases: Case[] = [];
  for (const path of walk(root).sort()) {
    const source = readFileSync(path, "utf8");
    cases.push({ ...(await record("packageLists", [source])), file: relative(root, path) } as Case);
    cases.push({ ...(await record("moduleShape", [source])), file: relative(root, path) } as Case);
    for (const [op, args] of [
      ["addPackage", ["pkgs.bat"]], ["removePackage", ["pkgs.git"]], ["setOption", ["programs.git.enable", true]],
      ["unsetOption", ["programs.git.enable"]], ["addImport", ["./x.nix"]],
    ] as const)
      cases.push({ ...(await record(op, [source, ...args])), file: relative(root, path) } as Case);
  }
  return cases;
};

const argv = process.argv.slice(2);
const filesAt = argv.indexOf("--files");
if (filesAt >= 0) {
  const out = argv[argv.indexOf("--out") + 1];
  if (argv.indexOf("--out") < 0 || !out) throw new Error("--files needs --out FILE.");
  writeFileSync(out, JSON.stringify(await filesCorpus(argv[filesAt + 1])) + "\n");
} else {
  const out = join(import.meta.dir, "bun-edits.json");
  writeFileSync(out, JSON.stringify(await corpus(), null, 1) + "\n");
  console.log(`Recorded ${out}`);
}
