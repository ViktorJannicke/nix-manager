import { existsSync } from "node:fs";
import { realpath, readFile } from "node:fs/promises";
import { homedir, hostname, userInfo } from "node:os";
import { join } from "node:path";
import { Effect, Schema } from "effect";
import { Hosts, ManagerError, resolveHost, type Config } from "./domain";
import { attempt, command } from "./process";

export const loadConfig = (flake?: string, host?: string) =>
  Effect.gen(function* () {
    let selected = flake || process.env.NIX_MANAGER_FLAKE;
    if (!selected) {
      const repo = yield* command(
        "Find checkout",
        ["git", "rev-parse", "--show-toplevel"],
        { quiet: true, allowFailure: true },
      );
      const path = repo.stdout.trim();
      selected =
        repo.code === 0 &&
        existsSync(join(path, "data/hosts.nix")) &&
        existsSync(join(path, "flake.nix"))
          ? path
          : join(homedir(), "my-flakes");
    }
    const path = yield* attempt("Resolve flake directory", () =>
      realpath(selected),
    );
    if (!existsSync(join(path, "flake.nix")))
      return yield* Effect.fail(
        new ManagerError({
          message: `Required file not found: ${path}/flake.nix`,
        }),
      );
    const registry = process.env.NIX_MANAGER_HOSTS
      ? yield* attempt("Read host registry", () =>
          readFile(process.env.NIX_MANAGER_HOSTS!, "utf8"),
        )
      : (yield* command(
          "Read host registry",
          ["nix", "eval", "--json", "--file", join(path, "data/hosts.nix")],
          { quiet: true },
        )).stdout;
    const hosts = yield* Schema.decodeUnknown(Schema.parseJson(Hosts))(
      registry,
    ).pipe(
      Effect.mapError(
        (e) => new ManagerError({ message: `Invalid host registry: ${e}` }),
      ),
    );
    const [hostKey] = yield* Effect.try({
      try: () =>
        resolveHost(hosts, host || process.env.NIX_MANAGER_HOST || hostname()),
      catch: (e) => e as ManagerError,
    });
    const state = process.env.XDG_STATE_HOME || join(homedir(), ".local/state");
    const modern = join(state, "nix/profiles");
    const homeProfile = existsSync(modern)
      ? join(modern, "home-manager")
      : join(
          process.env.NIX_STATE_DIR || "/nix/var/nix",
          "profiles/per-user",
          userInfo().username,
          "home-manager",
        );
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
