import { describe, expect, test } from "bun:test";
import { chooseHomeProfile, pickHome, pickSystem } from "../src/config";

describe("host detection", () => {
  test("picks the NixOS configuration named like the host, as nixos-rebuild does", () => {
    expect(pickSystem("laptop", ["desktop", "laptop"])).toBe("laptop");
    expect(pickSystem("whatever", ["only"])).toBe("only");
    expect(pickSystem("my-laptop", ["desktop", "laptop"])).toBe("evaluate");
    expect(pickSystem("my-laptop", ["desktop", "laptop"], { desktop: "my-desktop", laptop: "my-laptop" })).toBe("laptop");
    expect(() => pickSystem("other", ["desktop", "laptop"], { desktop: "a", laptop: "b" })).toThrow("Cannot tell which NixOS configuration");
    expect(() => pickSystem("other", [])).toThrow("no nixosConfigurations");
  });

  test("picks the Home Manager configuration as home-manager does, then by user and host words", () => {
    expect(pickHome("alice", ["laptop"], ["alice@laptop", "alice"])).toBe("alice@laptop");
    expect(pickHome("alice", ["laptop"], ["alice", "bob"])).toBe("alice");
    expect(pickHome("alice", ["laptop"], ["main"])).toBe("main");
    const names = ["alice-desktop", "alice-laptop", "bob-laptop"];
    const users = { "alice-desktop": "alice", "alice-laptop": "alice", "bob-laptop": "bob" };
    expect(pickHome("alice", ["alices-nixos-laptop"], names)).toBe("evaluate");
    expect(pickHome("alice", ["alices-nixos-laptop"], names, users)).toBe("alice-laptop");
    expect(pickHome("bob", ["anything"], names, users)).toBe("bob-laptop");
    expect(() => pickHome("alice", ["server"], names, users)).toThrow("alice-desktop, alice-laptop");
    expect(() => pickHome("alice", ["laptop"], [])).toThrow("standalone Home Manager");
  });
});

describe("Home Manager profile", () => {
  const modern = "/home/a/.local/state/nix/profiles";
  const legacy = "/nix/var/nix/profiles/per-user/a";
  const choose = (...present: string[]) => chooseHomeProfile(modern, legacy, (path) => present.includes(path));

  test("a new user gets the modern folder, created before the first generation", () => {
    expect(choose()).toBe(`${modern}/home-manager`);
    expect(choose(modern)).toBe(`${modern}/home-manager`);
  });
  test("only a legacy folder keeps the legacy profile, as Home Manager chooses", () => {
    expect(choose(legacy)).toBe(`${legacy}/home-manager`);
  });
  test("an existing legacy profile stays put until Home Manager moves it", () => {
    // Nix creates the modern folder on its own; a new profile there would make
    // Home Manager delete the legacy generations instead of moving them.
    expect(choose(modern, legacy, `${legacy}/home-manager`)).toBe(`${legacy}/home-manager`);
    expect(choose(modern, legacy, `${legacy}/home-manager`, `${modern}/home-manager`)).toBe(`${modern}/home-manager`);
  });
});
