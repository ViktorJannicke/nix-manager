import { expect, test } from "bun:test";
import { FORMAT_VARIABLES, formatLocale, formatSettings, inFlakeFile, userNameProblem } from "../src/computer";
import { field, parseDefault, parsePorts } from "../ui/lib/fields";

test("user names follow useradd's rules and must be new", () => {
  expect(userNameProblem("alex", ["viktor"])).toBeUndefined();
  expect(userNameProblem("", [])).toBe("Enter a user name.");
  expect(userNameProblem("Alex", [])).toContain("lowercase");
  expect(userNameProblem("1alex", [])).toContain("starting with a letter");
  expect(userNameProblem("viktor", ["viktor"])).toBe("viktor already exists.");
  expect(userNameProblem("root", [])).toBe("root is reserved.");
  expect(userNameProblem("a".repeat(33), [])).toContain("32");
});

test("formats are one locale for every format variable", () => {
  const settings = formatSettings("de_DE.UTF-8");
  expect(Object.keys(settings)).toEqual(FORMAT_VARIABLES);
  expect(formatLocale(settings)).toBe("de_DE.UTF-8");
  // Nothing set: formats follow the language.
  expect(formatLocale({})).toBeNull();
  expect(formatLocale(undefined)).toBeNull();
  // Different locales per variable cannot be shown as one choice.
  expect(formatLocale({ ...settings, LC_TIME: "en_GB.UTF-8" })).toBeUndefined();
});

test("only a module written in a flake.nix counts as one", () => {
  expect(inFlakeFile("/nix/store/0123456789abcdfghijklmnpqrsvwxyz-source/flake.nix")).toBe(true);
  expect(inFlakeFile("/nix/store/0123456789abcdfghijklmnpqrsvwxyz-source/nixos/modules/x.nix")).toBe(false);
  expect(inFlakeFile("flake.nix")).toBe(false);
});

test("ports are read from a comma or space separated list", () => {
  expect(parsePorts("22, 8080 8080 443")).toEqual([22, 8080, 443]);
  expect(parsePorts("")).toEqual([]);
  expect(parsePorts("22, http")).toBeUndefined();
  expect(parsePorts("70000")).toBeUndefined();
});

test("option types become form controls", () => {
  expect(field("boolean").kind).toBe("boolean");
  expect(field('one of "a", "b"')).toEqual({ kind: "choice", choices: ["a", "b"] });
  expect(field("null or string")).toEqual({ kind: "text", nullable: true });
  expect(field("16 bit unsigned integer; between 0 and 65535 (both inclusive)")).toEqual({ kind: "number", min: 0, max: 65535 });
  // A list of ports is not one number.
  expect(field("list of 16 bit unsigned integer; between 0 and 65535 (both inclusive)").kind).toBe("other");
  expect(parseDefault("{ }")).toEqual({});
  expect(parseDefault('"en_US.UTF-8"')).toBe("en_US.UTF-8");
});
