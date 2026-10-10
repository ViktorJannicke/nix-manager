import { expect, test } from "bun:test";
import { addPackage, packageLists, removePackage } from "../src/packages";
import { parseNix } from "../src/syntax";

// Shaped like the category modules built with `mkPackageModule`.
const category = `mkPackageModule {
  name = "media";
  default = false;
  packages = with pkgs; [
    gimp3-with-plugins
    easyeffects # audio effects
    vorta
  ];
}
`;

const kde = `{
  packages =
    (with pkgs.kdePackages; [
      kate
      dolphin
    ])
    ++ (with pkgs; [ partitionmanager ])
    ++ lib.optionals config.extra [ zoom ];
}
`;

test("package lists resolve names through their with scope", async () => {
  const tree = await parseNix(kde);
  const lists = packageLists(tree.rootNode);
  expect(
    lists.map((list) => ({
      scope: list.scope,
      conditional: list.conditional,
      attrs: list.items.map((item) => item.attr),
    })),
  ).toEqual([
    { scope: "pkgs.kdePackages", conditional: false, attrs: ["pkgs.kdePackages.kate", "pkgs.kdePackages.dolphin"] },
    { scope: "pkgs", conditional: false, attrs: ["pkgs.partitionmanager"] },
    // No `with` scope: a bare name could be a let binding, so it stays unresolved.
    { scope: undefined, conditional: true, attrs: [undefined] },
  ]);
  tree.delete();
});

test("adding keeps every other byte and matches the list's style", async () => {
  const added = await addPackage(category, "pkgs.krita");
  expect(added.source).toBe(category.replace("    vorta\n", "    vorta\n    krita\n"));
  // Only an insertion: removing the inserted text restores the original exactly.
  expect(added.source.slice(0, added.start) + added.source.slice(added.start + added.text.length)).toBe(category);
  expect((await parseNix(added.source)).rootNode.hasError).toBe(false);

  // A trailing comment stays attached to its item.
  const commented = category.replace("    vorta\n", "");
  expect((await addPackage(commented, "pkgs.krita")).source).toBe(
    commented.replace("easyeffects # audio effects\n", "easyeffects # audio effects\n    krita\n"),
  );
});

test("the longest matching with scope receives the package", async () => {
  expect((await addPackage(kde, "pkgs.kdePackages.kcalc")).source).toBe(
    kde.replace("      dolphin\n", "      dolphin\n      kcalc\n"),
  );
  expect((await addPackage(kde, "pkgs.krita")).source).toBe(
    kde.replace("[ partitionmanager ]", "[ partitionmanager krita ]"),
  );
});

test("qualified lists and names that are not identifiers are written in full", async () => {
  const qualified = `{ pkgs, ... }: {
  home.packages = [
    pkgs.gh
  ];
}
`;
  expect((await addPackage(qualified, "pkgs.nixd")).source).toBe(
    qualified.replace("    pkgs.gh\n", "    pkgs.gh\n    pkgs.nixd\n"),
  );
  // Inside `with pkgs;`, a quoted first name would be a string.
  expect((await addPackage(category, 'pkgs."7zz"')).text).toBe('\n    pkgs."7zz"');
});

test("empty lists and a closing bracket on the item line stay well formed", async () => {
  expect((await addPackage("{ packages = with pkgs; [ ]; }", "pkgs.bat")).source).toBe(
    "{ packages = with pkgs; [ bat ]; }",
  );
  const empty = "{\n  packages = with pkgs; [\n  ];\n}\n";
  expect((await addPackage(empty, "pkgs.bat")).source).toBe(
    "{\n  packages = with pkgs; [\n    bat\n  ];\n}\n",
  );
  const tight = "{\n  packages = with pkgs; [\n    fzf ];\n}\n";
  expect((await addPackage(tight, "pkgs.bat")).source).toBe(
    "{\n  packages = with pkgs; [\n    fzf bat ];\n}\n",
  );
});

test("adding refuses duplicates and files without an editable list", async () => {
  await expect(addPackage(category, "pkgs.vorta")).rejects.toThrow("already listed");
  await expect(addPackage("{ packages = lib.optionals x [ a ]; }", "pkgs.bat")).rejects.toThrow(
    "No editable package list",
  );
  await expect(addPackage("{ packages = [ ", "pkgs.bat")).rejects.toThrow("syntax errors");
});

test("removing drops the item's line, or just the item when it shares a line", async () => {
  expect((await removePackage(category, "pkgs.easyeffects")).source).toBe(
    category.replace("    easyeffects # audio effects\n", ""),
  );
  expect((await removePackage(kde, "pkgs.partitionmanager")).source).toBe(
    kde.replace("[ partitionmanager ]", "[ ]"),
  );
  await expect(removePackage(category, "pkgs.krita")).rejects.toThrow("not listed");
});
