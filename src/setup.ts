// The setup assistant: turns a classic /etc/nixos configuration into a flake
// checkout the user owns, with a standalone Home Manager configuration.

import { existsSync, readdirSync } from "node:fs";
import { cp, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { homedir, hostname, userInfo } from "node:os";
import { join, resolve } from "node:path";
import { Effect } from "effect";
import { currentPlatform, detectHosts, rememberedCheckout } from "./config";
import { ManagerError } from "./domain";
import { attr } from "./nix";
import { attempt, command } from "./process";

/** The classic configuration; tests point elsewhere. */
export const SOURCE = process.env.NIX_MANAGER_SETUP_SOURCE || "/etc/nixos";

export interface SetupInfo {
  user: string;
  home: string;
  hostName: string;
  system: string;
  /** NixOS release such as "25.05", without the date suffix. */
  release: string;
  /** Whether the release is a development version (unstable). */
  unstable: boolean;
  /** nixpkgs revision of the running system, used to lock the new flake. */
  revision?: string;
  stateVersion: string;
  flakes: boolean;
  /** Files copied from /etc/nixos, relative to it. */
  files: string[];
  /** Files in /etc/nixos the user cannot read; they stay behind. */
  unreadable: string[];
  /** Imports that only work with channels, such as <home-manager/nixos>. */
  channelImports: string[];
  /** Why setup cannot continue, if it cannot. */
  blocked?: string;
  suggested: string;
}

/** NIX_CONFIG as the user set it, before allowFlakes added to it. */
const userNixConfig = process.env.NIX_CONFIG;

/** Whether the user's own Nix configuration turns flakes on. */
export const flakesEnabled = command("Check flakes", ["nix", "config", "show", "experimental-features"], {
  quiet: true,
  allowFailure: true,
  env: { NIX_CONFIG: userNixConfig ?? "" },
}).pipe(Effect.map((result) => result.code === 0 && /\bflakes\b/.test(result.stdout) && /\bnix-command\b/.test(result.stdout)));

/** Lets this process's `nix` commands use flakes, also before the system enables them. */
export const allowFlakes = () => {
  if (process.env.NIX_CONFIG?.includes("extra-experimental-features = nix-command flakes")) return;
  process.env.NIX_CONFIG = [process.env.NIX_CONFIG, "extra-experimental-features = nix-command flakes"].filter(Boolean).join("\n");
};

/** Release branches to try for nixpkgs and Home Manager, most specific first. */
export const branches = (release: string, unstable: boolean) => [
  ...(unstable ? [] : [{ nixpkgs: `nixos-${release}`, homeManager: `release-${release}` }]),
  { nixpkgs: "nixos-unstable", homeManager: "master" },
];

const walk = async (root: string, directory = ""): Promise<{ files: string[]; unreadable: string[] }> => {
  const files: string[] = [];
  const unreadable: string[] = [];
  for (const entry of await readdir(join(root, directory), { withFileTypes: true })) {
    const path = directory ? `${directory}/${entry.name}` : entry.name;
    // Build results and other symlinks point into the store or elsewhere.
    if (entry.name === ".git" || entry.isSymbolicLink()) continue;
    if (entry.isDirectory()) {
      try {
        const inner = await walk(root, path);
        files.push(...inner.files);
        unreadable.push(...inner.unreadable);
      } catch {
        unreadable.push(`${path}/`);
      }
    } else if (entry.isFile()) {
      try {
        await readFile(join(root, path));
        files.push(path);
      } catch {
        unreadable.push(path);
      }
    }
  }
  return { files: files.sort(), unreadable: unreadable.sort() };
};

/** Search paths such as <nixpkgs> or <home-manager/nixos>, outside comments. */
export const channelPaths = (text: string) =>
  [...text.replace(/\/\*[\s\S]*?\*\//g, "").replace(/#.*$/gm, "").matchAll(/(?<=[\s(\[=:])<[A-Za-z][\w.+-]*(?:\/[\w.+/-]*)?>/g)].map(
    (match) => match[0],
  );

/** What the assistant found on this computer. */
export const readSetup = (source = SOURCE) =>
  Effect.gen(function* () {
    const user = userInfo().username;
    const home = homedir();
    const version = yield* command("Read NixOS version", ["nixos-version", "--json"], { quiet: true, allowFailure: true });
    const parsed = version.code === 0 ? (JSON.parse(version.stdout) as { nixosVersion?: string; nixpkgsRevision?: string }) : {};
    const full = parsed.nixosVersion ?? "";
    const release = full.match(/^\d+\.\d+/)?.[0] ?? "";
    const { files, unreadable } = existsSync(source) ? yield* attempt("Read /etc/nixos", () => walk(source)) : { files: [], unreadable: [] };
    const nix = yield* attempt("Read the configuration", async () =>
      Object.fromEntries(await Promise.all(files.filter((file) => file.endsWith(".nix")).map(async (file) => [file, await readFile(join(source, file), "utf8")] as const))),
    );
    const stateVersion = nix["configuration.nix"]?.match(/system\.stateVersion\s*=\s*"([^"]+)"/)?.[1] ?? release;
    const channelImports = [...new Set(Object.values(nix).flatMap(channelPaths))].sort();
    const suggested = join(home, "nixos-config");
    const blocked = !release
      ? "This does not look like NixOS: nixos-version is missing."
      : !files.includes("configuration.nix")
        ? `There is no ${source}/configuration.nix to start from.`
        : files.includes("flake.nix")
          ? `${source} is a flake already. Copy it to a folder you own and add a homeConfigurations entry for your user, then choose that folder with --flake.`
          : undefined;
    return {
      user,
      home,
      hostName: hostname(),
      system: currentPlatform(),
      release,
      unstable: /pre/.test(full),
      revision: parsed.nixpkgsRevision,
      stateVersion,
      flakes: yield* flakesEnabled,
      files,
      unreadable,
      channelImports,
      blocked,
      suggested,
    } satisfies SetupInfo;
  });

const nixString = (text: string) => JSON.stringify(text);

/** The new flake: this computer's NixOS configuration and the user's Home Manager configuration. */
export const flakeTemplate = (info: SetupInfo, branch: { nixpkgs: string; homeManager: string }) => `{
  description = "${info.hostName}: system and home configuration";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/${branch.nixpkgs}";
    home-manager = {
      url = "github:nix-community/home-manager/${branch.homeManager}";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    { nixpkgs, home-manager, ... }:
    {
      nixosConfigurations.${attr(info.hostName)} = nixpkgs.lib.nixosSystem {
        modules = [
          ./configuration.nix
          {
            nixpkgs.hostPlatform = nixpkgs.lib.mkDefault ${nixString(info.system)};
            # Nix Manager builds with flakes.
            nix.settings.experimental-features = [
              "nix-command"
              "flakes"
            ];
          }
        ];
      };

      homeConfigurations.${attr(`${info.user}@${info.hostName}`)} = home-manager.lib.homeManagerConfiguration {
        pkgs = nixpkgs.legacyPackages.${attr(info.system)};
        modules = [ ./home.nix ];
      };
    };
}
`;

export const homeTemplate = (info: SetupInfo) => `{
  home.username = ${nixString(info.user)};
  home.homeDirectory = ${nixString(info.home)};
  # The release this home configuration started on; leave it as it is.
  home.stateVersion = ${nixString(info.stateVersion)};

  programs.home-manager.enable = true;
}
`;

/** Why a folder cannot hold the new checkout, if it cannot. */
export const targetProblem = (target: string, home = homedir()) => {
  if (!target.startsWith("/")) return "Use a full path, such as ~/nixos-config.";
  const path = resolve(target);
  if (path !== home && !path.startsWith(`${home}/`)) return "Choose a folder inside your home folder, so you own the files.";
  if (path === home) return "Choose a new folder inside your home folder.";
  if (existsSync(path) && readdirSync(path).length) return "This folder is not empty.";
  return undefined;
};

export type SetupStep = "copy" | "write" | "git" | "lock" | "check" | "remember";
export const setupSteps: { id: SetupStep; label: string }[] = [
  { id: "copy", label: "Copy the configuration from /etc/nixos" },
  { id: "write", label: "Add flake.nix and home.nix" },
  { id: "git", label: "Start a Git history" },
  { id: "lock", label: "Pin nixpkgs to the version this computer runs" },
  { id: "check", label: "Check that the configuration still describes this computer" },
  { id: "remember", label: "Remember the folder" },
];

const lastLine = (text: string) => text.trim().split("\n").filter(Boolean).at(-1) ?? "";

/** Creates the checkout; `progress` hears each step as it starts. */
export const createCheckout = (info: SetupInfo, target: string, progress: (step: SetupStep) => void, source = SOURCE) => {
  const path = resolve(target);
  const created = Effect.gen(function* () {
    const run = (label: string, argv: string[]) =>
      command(label, argv, { cwd: path, quiet: true, allowFailure: true }).pipe(
        Effect.flatMap((result) =>
          result.code === 0 ? Effect.succeed(result) : Effect.fail(new ManagerError({ message: `${label}: ${lastLine(result.stderr) || "failed"}` })),
        ),
      );

    progress("copy");
    yield* attempt("Copy the configuration", async () => {
      await mkdir(path, { recursive: true });
      for (const file of info.files) await cp(join(source, file), join(path, file), { force: false, errorOnExist: true });
    });

    progress("write");
    const tried = branches(info.release, info.unstable);
    yield* attempt("Write the new files", async () => {
      await writeFile(join(path, "flake.nix"), flakeTemplate(info, tried[0]), { flag: "wx" });
      await writeFile(join(path, "home.nix"), homeTemplate(info), { flag: "wx" });
    });

    progress("git");
    yield* run("Start Git", ["git", "init", "--quiet"]);
    yield* run("Add the files to Git", ["git", "add", "--", ...info.files, "flake.nix", "home.nix"]);

    progress("lock");
    // The running revision keeps the first build close to the running system;
    // updates later follow the branch in flake.nix.
    const pin = info.revision ? ["--override-input", "nixpkgs", `github:NixOS/nixpkgs/${info.revision}`] : [];
    let locked = false;
    let failure = "";
    for (const [index, branch] of tried.entries()) {
      if (index > 0) yield* attempt("Write flake.nix", () => writeFile(join(path, "flake.nix"), flakeTemplate(info, branch)));
      const result = yield* command("Pin the inputs", ["nix", "flake", "lock", ...pin], { cwd: path, quiet: true, allowFailure: true });
      if (result.code === 0) {
        locked = true;
        break;
      }
      failure = lastLine(result.stderr);
    }
    if (!locked) return yield* Effect.fail(new ManagerError({ message: `Could not pin the inputs: ${failure}` }));
    yield* run("Add the lock file to Git", ["git", "add", "--", "flake.nix", "flake.lock"]);

    progress("check");
    const hosts = yield* detectHosts(path, info.hostName, info.user);
    const [host] = Object.values(hosts);
    yield* run("Evaluate the system", ["nix", "eval", "--raw", `${path}#nixosConfigurations.${attr(host.nixosConfiguration)}.config.system.build.toplevel.drvPath`]);
    yield* run("Evaluate the home configuration", ["nix", "eval", "--raw", `${path}#homeConfigurations.${attr(host.homeConfiguration)}.activationPackage.drvPath`]);
    // A first commit when Git knows who the user is; staged files are enough for flakes.
    yield* command("Commit", ["git", "commit", "--quiet", "-m", `Start from ${source}`], { cwd: path, quiet: true, allowFailure: true });

    progress("remember");
    yield* attempt("Remember the folder", async () => {
      const file = rememberedCheckout();
      await mkdir(join(file, ".."), { recursive: true });
      await writeFile(file, `${path}\n`);
    });
    return path;
  });
  return Effect.gen(function* () {
    if (info.blocked) return yield* Effect.fail(new ManagerError({ message: info.blocked }));
    const problem = targetProblem(target, info.home);
    if (problem) return yield* Effect.fail(new ManagerError({ message: problem }));
    // The folder was missing or empty, so a failed attempt leaves nothing behind.
    return yield* created.pipe(Effect.tapError(() => Effect.promise(() => rm(path, { recursive: true, force: true }))));
  });
};
