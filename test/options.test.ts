import { expect, test } from "bun:test";
import { moduleShape, renderValue, setOption, unsetOption } from "../src/options";
import { parseNix } from "../src/syntax";

const module = `{ config, pkgs, ... }:
let
  name = "viktor";
in
{
  # Shell
  programs.git = {
    enable = true;
    lfs.enable = false; # large files
  };
  programs.direnv.enable = true;
  home.username = name;
}
`;

const valid = async (source: string) => {
  const tree = await parseNix(source);
  const ok = !tree.rootNode.hasError;
  tree.delete();
  return ok;
};

test("values render as Nix with strings escaped", () => {
  expect(renderValue(true)).toBe("true");
  expect(renderValue(-3)).toBe("(-3)");
  expect(renderValue(1.5)).toBe("1.5");
  expect(renderValue('a "b" ${c}\n')).toBe('"a \\"b\\" \\${c}\\n"');
  expect(renderValue(["a", "b"])).toBe('[ "a" "b" ]');
  expect(renderValue({ enable: true, "a b": 4 })).toBe('{ enable = true; "a b" = 4; }');
  expect(renderValue(null)).toBe("null");
});

test("existing plain values are replaced in place", async () => {
  const edit = await setOption(module, "programs.git.lfs.enable", true);
  expect(edit.source).toBe(module.replace("lfs.enable = false;", "lfs.enable = true;"));
  expect((await setOption(module, "programs.direnv.enable", false)).source).toBe(
    module.replace("programs.direnv.enable = true;", "programs.direnv.enable = false;"),
  );
});

test("new options join the deepest set that holds part of their path", async () => {
  expect((await setOption(module, "programs.git.signing.signByDefault", true)).source).toBe(
    module.replace(
      "    lfs.enable = false; # large files\n",
      "    lfs.enable = false; # large files\n    signing.signByDefault = true;\n",
    ),
  );
  const added = await setOption(module, "programs.zoxide.enable", true);
  expect(added.source).toBe(
    module.replace("  home.username = name;\n", "  home.username = name;\n  programs.zoxide.enable = true;\n"),
  );
  expect(await valid(added.source)).toBe(true);
});

test("modules with options and config, and empty modules, are editable", async () => {
  const declared = `{ lib, ... }: {
  options.my.flag = lib.mkEnableOption "flag";
  config = {
    services.a.enable = true;
  };
}
`;
  expect((await setOption(declared, "services.b.enable", true)).source).toBe(
    declared.replace("    services.a.enable = true;\n", "    services.a.enable = true;\n    services.b.enable = true;\n"),
  );
  expect((await setOption("{ ... }: { }\n", "programs.fzf.enable", true)).source).toBe(
    "{ ... }: { programs.fzf.enable = true; }\n",
  );
  expect((await setOption("{ ... }:\n{\n}\n", "programs.fzf.enable", true)).source).toBe(
    "{ ... }:\n{\n  programs.fzf.enable = true;\n}\n",
  );
});

test("computed values, split definitions and non-modules are refused", async () => {
  await expect(setOption(module, "home.username", "x")).rejects.toThrow("computed here");
  await expect(setOption(module, "programs", { git: { enable: true } })).rejects.toThrow("separate parts");
  await expect(setOption("mkPackageModule { name = \"x\"; }", "a.b", true)).rejects.toThrow("not a module");
  await expect(setOption(module, "programs.${x}.enable", true)).rejects.toThrow("Unsupported option path");
});

test("unsetting removes the binding's line or just the binding", async () => {
  expect((await unsetOption(module, "programs.git.lfs.enable")).source).toBe(
    module.replace("    lfs.enable = false; # large files\n", ""),
  );
  expect((await unsetOption("{ a.b = 1; c = 2; }", "a.b")).source).toBe("{ c = 2; }");
  await expect(unsetOption(module, "programs.fzf.enable")).rejects.toThrow("not set");
});

test("module shape lists top-level names", async () => {
  expect(await moduleShape(module)).toEqual({
    editable: true,
    names: ["programs.git", "programs.direnv.enable", "home.username"],
  });
  expect((await moduleShape("mkPackageModule { }")).editable).toBe(false);
});
