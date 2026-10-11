import { expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { branches, channelPaths, flakeTemplate, homeTemplate, targetProblem, type SetupInfo } from "../src/setup";
import { pickHome, pickSystem } from "../src/config";

const info: SetupInfo = {
  user: "alice",
  home: "/home/alice",
  hostName: "alice-pc",
  system: "x86_64-linux",
  release: "25.05",
  unstable: false,
  revision: "abc",
  stateVersion: "24.11",
  flakes: false,
  files: ["configuration.nix", "hardware-configuration.nix"],
  unreadable: [],
  channelImports: [],
  suggested: "/home/alice/nixos-config",
};

test("the new flake is found again by host detection", () => {
  const flake = flakeTemplate(info, branches(info.release, info.unstable)[0]);
  expect(flake).toContain('nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.05"');
  expect(flake).toContain('url = "github:nix-community/home-manager/release-25.05"');
  expect(flake).toContain("nixosConfigurations.alice-pc = ");
  expect(flake).toContain('homeConfigurations."alice@alice-pc" = ');
  expect(flake).toContain('"flakes"');
  expect(pickSystem(info.hostName, ["alice-pc"])).toBe("alice-pc");
  expect(pickHome(info.user, [info.hostName], ["alice@alice-pc"])).toBe("alice@alice-pc");
  expect(homeTemplate(info)).toContain('home.stateVersion = "24.11";');
  expect(homeTemplate(info)).toContain('home.homeDirectory = "/home/alice";');
});

test("unstable systems follow the unstable branches, releases fall back to them", () => {
  expect(branches("25.11", true)).toEqual([{ nixpkgs: "nixos-unstable", homeManager: "master" }]);
  expect(branches("25.05", false).map((branch) => branch.nixpkgs)).toEqual(["nixos-25.05", "nixos-unstable"]);
});

test("the checkout goes into a new or empty folder in the home folder", () => {
  const home = mkdtempSync(join(tmpdir(), "nm-setup-test-"));
  try {
    expect(targetProblem(join(home, "config"), home)).toBeUndefined();
    mkdirSync(join(home, "empty"));
    expect(targetProblem(join(home, "empty"), home)).toBeUndefined();
    mkdirSync(join(home, "full"));
    writeFileSync(join(home, "full", "x"), "");
    expect(targetProblem(join(home, "full"), home)).toContain("not empty");
    expect(targetProblem("/etc/nixos", home)).toContain("inside your home folder");
    expect(targetProblem(home, home)).toContain("new folder");
    expect(targetProblem("config", home)).toContain("full path");
    expect(targetProblem(`${home}/../elsewhere`, home)).toContain("inside your home folder");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("channel imports are found in code, not in comments", () => {
  const text = `{ imports = [ <home-manager/nixos> ./hardware.nix ];
  # networking.interfaces.<interface>.useDHCP
  /* <nixos-hardware> */ x = import <nixpkgs> { }; }`;
  expect(channelPaths(text)).toEqual(["<home-manager/nixos>", "<nixpkgs>"]);
});
