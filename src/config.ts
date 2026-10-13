import { existsSync } from "node:fs";
import { realpath, readFile } from "node:fs/promises";
import { homedir, hostname, userInfo } from "node:os";
import { join } from "node:path";
import { Effect, Schema } from "effect";
import { Hosts, ManagerError, resolveHost, type Config, type Host } from "./domain";
import { attempt, command } from "./process";

/** Words of a configuration or host name, for telling `alice-laptop` from `alice-desktop`. */
const words = (name: string) => new Set(name.toLowerCase().split(/[^a-z0-9]+/).filter(Boolean));
const shared = (a: string, b: string) => {
  const other = words(b);
  return [...words(a)].filter((word) => other.has(word)).length;
};
const ambiguous = (what: string, names: readonly string[]) =>
  new ManagerError({
    message: `Cannot tell which ${what} belongs to this computer: ${names.join(", ")}. Choose one with --host, or list the hosts in data/hosts.nix.`,
  });

/**
 * The NixOS configuration for a host name: the one with that name, as
 * nixos-rebuild picks it, the one setting it as `networking.hostName`, or the
 * only one. `hostNames` is read only when the names do not decide.
 */
export const pickSystem = (
  target: string,
  names: readonly string[],
  hostNames?: Record<string, string>,
): string | "evaluate" => {
  if (names.includes(target)) return target;
  if (names.length === 1) return names[0];
  if (!names.length) throw new ManagerError({ message: "The flake has no nixosConfigurations." });
  if (!hostNames) return "evaluate";
  const matches = names.filter((name) => hostNames[name] === target);
  if (matches.length === 1) return matches[0];
  throw ambiguous("NixOS configuration", names);
};

/**
 * The Home Manager configuration for a user on a host: `user@host` or `user`,
 * as home-manager picks it, the only one, or the user's configuration whose
 * name shares the most words with the host. `userNames` is read only when the
 * names do not decide.
 */
export const pickHome = (
  user: string,
  hosts: readonly string[],
  names: readonly string[],
  userNames?: Record<string, string>,
): string | "evaluate" => {
  for (const name of [...hosts.map((host) => `${user}@${host}`), user]) if (names.includes(name)) return name;
  if (names.length === 1) return names[0];
  if (!names.length)
    throw new ManagerError({
      message: "The flake has no homeConfigurations. Nix Manager needs a standalone Home Manager configuration for your user.",
    });
  if (!userNames) return "evaluate";
  const own = names.filter((name) => userNames[name] === user);
  if (own.length === 1) return own[0];
  const score = (name: string) => Math.max(...hosts.map((host) => shared(name, host)));
  const best = Math.max(0, ...own.map(score));
  const closest = own.filter((name) => score(name) === best);
  if (best > 0 && closest.length === 1) return closest[0];
  throw ambiguous("Home Manager configuration", own.length ? own : names);
};

/** The running machine's platform, without evaluating a configuration. */
export const currentPlatform = () => `${{ x64: "x86_64", arm64: "aarch64" }[process.arch as string] ?? process.arch}-linux`;

/** Where the setup assistant remembers the checkout it created. */
export const rememberedCheckout = () =>
  join(
    process.env.NIX_MANAGER_STATE_DIR || join(process.env.XDG_STATE_HOME || join(homedir(), ".local/state"), "nix-manager"),
    "checkout",
  );

/** A JSON value from the flake; with allowFailure, undefined when it is missing. */
const evalJson = <A>(label: string, installable: string, apply: string, allowFailure = false) =>
  command(label, ["nix", "eval", "--json", installable, "--apply", apply], { quiet: true, allowFailure }).pipe(
    Effect.flatMap((result) =>
      result.code === 0
        ? Effect.succeed(JSON.parse(result.stdout) as A)
        : allowFailure
          ? Effect.succeed(undefined as A)
          : Effect.fail(new ManagerError({ message: `${label}: ${result.stderr.trim().split("\n").at(-1)}` })),
    ),
  );

/** A registry with one host, read from the flake's own configurations. */
export const detectHosts = (flake: string, target: string, user: string) =>
  Effect.gen(function* () {
    const [systems, homes] = yield* Effect.all(
      [
        evalJson<string[]>("List NixOS configurations", `${flake}#nixosConfigurations`, "builtins.attrNames"),
        evalJson<string[] | undefined>("List Home Manager configurations", `${flake}#homeConfigurations`, "builtins.attrNames", true),
      ],
      { concurrency: 2 },
    );
    const pick = <A>(f: () => A) => Effect.try({ try: f, catch: (e) => e as ManagerError });
    let system = yield* pick(() => pickSystem(target, systems));
    let hostName = system;
    if (system === "evaluate") {
      const hostNames = yield* evalJson<Record<string, string>>(
        "Read host names",
        `${flake}#nixosConfigurations`,
        "builtins.mapAttrs (_: c: c.config.networking.hostName)",
      );
      system = yield* pick(() => pickSystem(target, systems, hostNames));
      hostName = hostNames[system];
    }
    const hosts = [...new Set([target, hostName, system])];
    let home = yield* pick(() => pickHome(user, hosts, homes ?? []));
    if (home === "evaluate") {
      const userNames = yield* evalJson<Record<string, string>>(
        "Read Home Manager users",
        `${flake}#homeConfigurations`,
        "builtins.mapAttrs (_: c: c.config.home.username)",
      );
      home = yield* pick(() => pickHome(user, hosts, homes ?? [], userNames));
    }
    return {
      [system]: { system: currentPlatform(), userName: user, nixosConfiguration: system, nixosHostName: hostName, homeConfiguration: home },
    } satisfies Record<string, Host>;
  });

/** Whether a checkout looks like a system configuration rather than any flake. */
const isConfiguration = (path: string) =>
  attempt("Read flake.nix", async () =>
    existsSync(join(path, "data/hosts.nix")) ||
    (existsSync(join(path, "flake.nix")) && (await readFile(join(path, "flake.nix"), "utf8")).includes("nixosConfigurations")),
  );

/**
 * The checkout to manage: the one asked for, the Git checkout this runs in
 * when it is a system configuration, the one the setup assistant created, or
 * ~/my-flakes. `chosen` says whether it was asked for.
 */
export const selectCheckout = (flake?: string) =>
  Effect.gen(function* () {
    const asked = flake || process.env.NIX_MANAGER_FLAKE;
    if (asked) return { path: asked, chosen: true };
    const repo = yield* command(
      "Find checkout",
      ["git", "rev-parse", "--show-toplevel"],
      { quiet: true, allowFailure: true },
    );
    const path = repo.stdout.trim();
    if (repo.code === 0 && (yield* isConfiguration(path))) return { path, chosen: false };
    const remembered = existsSync(rememberedCheckout())
      ? (yield* attempt("Read the remembered checkout", () => readFile(rememberedCheckout(), "utf8"))).trim()
      : "";
    return { path: remembered || join(homedir(), "my-flakes"), chosen: false };
  });

export const loadConfig = (flake?: string, host?: string) =>
  Effect.gen(function* () {
    const selected = (yield* selectCheckout(flake)).path;
    const path = yield* attempt("Resolve flake directory", () =>
      realpath(selected),
    );
    if (!existsSync(join(path, "flake.nix")))
      return yield* Effect.fail(
        new ManagerError({
          message: `Required file not found: ${path}/flake.nix`,
        }),
      );
    const target = host || process.env.NIX_MANAGER_HOST || hostname();
    // Without a registry the host is read from the flake's configurations.
    const registry = process.env.NIX_MANAGER_HOSTS
      ? yield* attempt("Read host registry", () =>
          readFile(process.env.NIX_MANAGER_HOSTS!, "utf8"),
        )
      : existsSync(join(path, "data/hosts.nix"))
        ? (yield* command(
            "Read host registry",
            ["nix", "eval", "--json", "--file", join(path, "data/hosts.nix")],
            { quiet: true },
          )).stdout
        : undefined;
    const hosts =
      registry === undefined
        ? yield* detectHosts(path, target, userInfo().username)
        : yield* Schema.decodeUnknown(Schema.parseJson(Hosts))(registry).pipe(
            Effect.mapError(
              (e) => new ManagerError({ message: `Invalid host registry: ${e}` }),
            ),
          );
    const [hostKey] =
      registry === undefined
        ? Object.keys(hosts)
        : yield* Effect.try({
            try: () => resolveHost(hosts, target),
            catch: (e) => e as ManagerError,
          });
    const state = process.env.XDG_STATE_HOME || join(homedir(), ".local/state");
    const modern = join(state, "nix/profiles");
    const legacy = join(process.env.NIX_STATE_DIR || "/nix/var/nix", "profiles/per-user", userInfo().username);
    // As Home Manager chooses: the legacy folder only when it already exists,
    // since current NixOS no longer creates it for new users.
    const homeProfile = join(existsSync(modern) || !existsSync(legacy) ? modern : legacy, "home-manager");
    // The Nix wrapper sets this to its immutable, Polkit-registered executable.
    const self = process.env.NIX_MANAGER_EXECUTABLE;
    const privileged = [
      "/run/wrappers/bin/pkexec",
      ...(self ? [self] : [process.execPath, process.argv[1]]),
      "--privileged",
    ];
    return {
      flake: path,
      hosts,
      hostKey,
      homeProfile,
      homeProfileExists: existsSync(homeProfile),
      privileged,
      stateDir: process.env.NIX_MANAGER_STATE_DIR || join(state, "nix-manager"),
    } satisfies Config;
  });
