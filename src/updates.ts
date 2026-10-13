import {
  readFile,
  writeFile,
  mkdtemp,
  rm,
  rename,
  chmod,
  stat,
} from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { Context, Effect, Layer, Schedule, Schema } from "effect";
import { ManagerError, resolveHost, type Config, type Request } from "./domain";
import { attempt, command } from "./process";
import { lockRef } from "./nix";

export class Network extends Context.Tag("nix-manager/Network")<
  Network,
  {
    text: (url: string) => Effect.Effect<string, ManagerError>;
  }
>() {}
export const networkLayer = Layer.succeed(Network, {
  text: (url: string) =>
    Effect.tryPromise({
      try: async (signal) => {
        const response = await fetch(url, {
          signal: AbortSignal.any([signal, AbortSignal.timeout(30_000)]),
          headers: { "User-Agent": "nix-manager" },
        });
        if (!response.ok)
          throw new Error(`HTTP ${response.status} from ${url}`);
        return response.text();
      },
      catch: (error) => new ManagerError({ message: `Fetch failed: ${error}` }),
    }).pipe(
      Effect.retry(
        Schedule.exponential("500 millis").pipe(
          Schedule.intersect(Schedule.recurs(2)),
        ),
      ),
    ),
});
const json = (text: string): any => JSON.parse(text);
const stableVersion = /^(\d+)\.(\d+)\.(\d+)$/;
const nightlyVersion = /^(\d+)\.(\d+)\.(\d+)-nightly\.(\d{8})\.(\d+)$/;
const Pin = Schema.Struct({
  version: Schema.String.pipe(
    Schema.filter((v) => stableVersion.test(v) || nightlyVersion.test(v), {
      message: () => "Expected X.Y.Z or X.Y.Z-nightly.YYYYMMDD.N",
    }),
  ),
  /** Nightly follows the newest nightly or stable release. */
  channel: Schema.optional(Schema.Literal("stable", "nightly")),
  nixpkgsRev: Schema.optional(Schema.String),
});
type Channel = "stable" | "nightly";
const Lockfile = Schema.Struct({
  nodes: Schema.Record({
    key: Schema.String,
    value: Schema.Struct({
      locked: Schema.optional(
        Schema.Record({ key: Schema.String, value: Schema.Unknown }),
      ),
    }),
  }),
});
const parsePin = (text: string) =>
  Schema.decodeUnknown(Schema.parseJson(Pin))(text).pipe(
    Effect.mapError(
      (e) => new ManagerError({ message: `Invalid source pin: ${e}` }),
    ),
  );
const readLock = (path: string) =>
  attempt(`Read ${path}`, () => readFile(path, "utf8")).pipe(
    Effect.flatMap(Schema.decodeUnknown(Schema.parseJson(Lockfile))),
    Effect.mapError(
      (e) => new ManagerError({ message: `Invalid lock file ${path}: ${e}` }),
    ),
  );
export const readJson = (path: string) =>
  attempt(`Read ${path}`, async () => json(await readFile(path, "utf8")));
export const temporary = Effect.acquireRelease(
  attempt("Create temporary workspace", () =>
    mkdtemp(join(tmpdir(), "nix-manager-")),
  ),
  (path) => Effect.promise(() => rm(path, { recursive: true, force: true })),
);
const saveJson = (path: string, data: unknown) =>
  attempt(`Write ${path}`, () =>
    writeFile(path, JSON.stringify(data, null, 2) + "\n"),
  );
/** Orders X.Y.Z and X.Y.Z-nightly.YYYYMMDD.N; nightlies precede their X.Y.Z. */
export function compareVersions(a: string, b: string): number {
  const parse = (v: string) => {
    const stable = v.match(stableVersion),
      parts = (stable || v.match(nightlyVersion))?.slice(1).map(BigInt);
    if (!parts)
      throw new ManagerError({
        message: `Expected X.Y.Z or X.Y.Z-nightly.YYYYMMDD.N versions: ${a}, ${b}`,
      });
    return stable ? [...parts, BigInt(Number.MAX_SAFE_INTEGER), 0n] : parts;
  };
  const aa = parse(a),
    bb = parse(b);
  for (let i = 0; i < 5; i++)
    if (aa[i] !== bb[i]) return aa[i] > bb[i] ? 1 : -1;
  return 0;
}
export const packages = {
  t3code: {
    owner: "pingdotgg",
    repo: "t3code",
    prefix: "t3",
    recipes: ["package.nix", "unwrapped.nix", "resource-monitor.nix"],
    blockedVersions: [] as string[],
  },
  opencode: {
    owner: "anomalyco",
    repo: "opencode",
    prefix: "op",
    recipes: ["package.nix"],
    blockedVersions: [] as string[],
  },
  pince: {
    owner: "korcankaraokcu",
    repo: "PINCE",
    prefix: "",
    recipes: [],
    blockedVersions: [] as string[],
  },
};
export type Package = keyof typeof packages;
const latest = (name: Package, channel: Channel = "stable") =>
  Effect.gen(function* () {
    const network = yield* Network;
    const p = packages[name];
    const raw = yield* network.text(
      channel === "nightly"
        ? `https://api.github.com/repos/${p.owner}/${p.repo}/releases?per_page=100`
        : `https://api.github.com/repos/${p.owner}/${p.repo}/releases/latest`,
    );
    return yield* Effect.try({
      try: () => {
        if (channel === "nightly") {
          const releases: { tag_name?: string; draft?: boolean }[] = json(raw);
          const versions = releases
            .filter((release) => !release.draft)
            .map((release) => release.tag_name?.replace(/^v/, "") || "")
            .filter((v) => stableVersion.test(v) || nightlyVersion.test(v))
            .sort(compareVersions);
          if (!versions.length)
            throw new Error("No stable or nightly release tags found");
          return versions.at(-1)!;
        }
        const version = json(raw).tag_name?.match(/^v(\d+\.\d+\.\d+)$/)?.[1];
        if (!version)
          throw new Error("Release does not have a stable vX.Y.Z tag");
        return version as string;
      },
      catch: (error) => new ManagerError({ message: `${name}: ${error}` }),
    });
  });
const fakeHash = "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
export function extractHash(
  log: string,
  expected: string,
  code: number,
): string {
  let matching = false;
  if (code !== 0)
    for (const line of log.replace(/\x1b\[[0-9;]*m/g, "").split("\n")) {
      const mismatch = line.match(
        /hash mismatch in fixed-output derivation '([^']+)'/,
      );
      if (mismatch) matching = mismatch[1] === expected;
      else if (matching) {
        const hash = line.match(
          /^\s*got:\s*(sha256-[A-Za-z0-9+/]{43}=)\s*$/,
        )?.[1];
        if (hash) return hash;
      }
    }
  throw new ManagerError({
    message:
      code === 0
        ? "Expected a fake-hash build failure, but the build succeeded."
        : `Could not extract the hash for ${expected}. See the operation log.`,
  });
}
export const discoverHash = (
  label: string,
  expr: string,
  env: Record<string, string>,
) =>
  Effect.gen(function* () {
    const expected = (yield* command(
      `Evaluate ${label}`,
      ["nix", "eval", "--impure", "--raw", "--expr", `(${expr}).drvPath`],
      { env, quiet: true },
    )).stdout.trim();
    const result = yield* command(
      `Compute ${label} hash (may take several minutes)`,
      [
        "nix",
        "build",
        "--no-link",
        "--impure",
        "--print-build-logs",
        "--expr",
        expr,
      ],
      { env, allowFailure: true },
    );
    return yield* Effect.try({
      try: () =>
        extractHash(
          result.stderr + "\n" + result.stdout,
          expected,
          result.code,
        ),
      catch: (e) => e as ManagerError,
    });
  });
export const atomicPin = (path: string, before: string, value: unknown) =>
  attempt("Save validated pin", async () => {
    const candidate = `${path}.${crypto.randomUUID()}.tmp`;
    try {
      await writeFile(candidate, JSON.stringify(value, null, 2) + "\n", {
        flag: "wx",
      });
      await chmod(candidate, (await stat(path)).mode & 0o777);
      if ((await readFile(path, "utf8")) !== before)
        throw new Error(
          "Pin changed during the build; refusing to overwrite it. Retry with the new pin.",
        );
      await rename(candidate, path);
    } finally {
      await rm(candidate, { force: true });
    }
  });

const candidateExpression = `let
  flake = builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE");
  system = builtins.getEnv "NIX_MANAGER_SYSTEM";
  pkgs = import flake.inputs.nixpkgs { inherit system; config.allowUnfree = true; };
  candidate = import (builtins.toPath (builtins.getEnv "NIX_MANAGER_RECIPE")) {
    prev = pkgs;
    pin = builtins.fromJSON (builtins.readFile (builtins.getEnv "NIX_MANAGER_PACKAGE_PIN"));
    forceOverride = true;
  };
  opencodeDesktop = pkgs.opencode-desktop.overrideAttrs (_: {
    inherit (candidate) version src node_modules patches;
  });
  t3code = flake.packages.\${system}.t3code.override {
    t3code-unwrapped = candidate.unwrapped;
    t3code-resource-monitor = candidate.resourceMonitor;
  };
in if builtins.getEnv "NIX_MANAGER_PACKAGE_NAME" == "t3code" then t3code
else if builtins.getEnv "NIX_MANAGER_PACKAGE_NAME" == "opencode" then opencodeDesktop
else candidate`;
const pinceExpression = `let
  flake = builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE");
  pkgs = import flake.inputs.nixpkgs { system = builtins.getEnv "NIX_MANAGER_SYSTEM"; config.allowUnfree = true; };
in pkgs.callPackage (flake.outPath + "/pkgs/pince/package.nix") {
  pin = builtins.fromJSON (builtins.readFile (builtins.getEnv "NIX_MANAGER_PACKAGE_PIN"));
}`;
export const updatePackage = (
  config: Config,
  r: Request,
  log: (message: string) => void,
) =>
  Effect.scoped(
    Effect.gen(function* () {
      const name = r.action.replace("-update", "") as Package;
      const p = packages[name];
      const pinFile = join(config.flake, "pkgs", name, "source.json");
      const before = yield* attempt("Read current pin", () =>
        readFile(pinFile, "utf8"),
      );
      const pin = yield* parsePin(before);
      const channel = pin.channel || "stable";
      const version = yield* latest(name, channel);
      if (p.blockedVersions.includes(version))
        return yield* Effect.fail(
          new ManagerError({
            message: `${name} ${version} is blocked because of a known upstream regression.`,
          }),
        );
      const order = yield* Effect.try({
        try: () => compareVersions(pin.version, version),
        catch: (e) => e as ManagerError,
      });
      log(`${name}: local pin ${pin.version} | ${channel} release ${version}`);
      if (order > 0)
        return yield* Effect.fail(
          new ManagerError({ message: `Refusing a ${name} downgrade.` }),
        );
      const work = yield* temporary;
      const env = {
        NIX_MANAGER_SYSTEM: resolveHost(
          config.hosts,
          r.host || config.hostKey,
        )[1].system,
        NIX_MANAGER_PACKAGE_NAME: name,
        NIX_MANAGER_HOME_FLAKE: `path:${config.flake}`,
        NIX_MANAGER_RECIPE: join(config.flake, "pkgs", name, "package.nix"),
        NIX_MANAGER_PACKAGE_PIN: join(work, "pin.json"),
      };
      const evaluate = (expr: string) =>
        command(
          "Inspect locked nixpkgs",
          ["nix", "eval", "--impure", "--raw", "--expr", expr],
          { env, quiet: true },
        ).pipe(Effect.map((r) => r.stdout.trim()));
      let rev = "";
      if (name !== "pince") {
        const path = yield* evaluate(
          '(builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE")).inputs.nixpkgs.outPath',
        );
        const locked = yield* evaluate(
          'let flake = builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE"); pkgs = import flake.inputs.nixpkgs { system = builtins.getEnv "NIX_MANAGER_SYSTEM"; }; in pkgs.${builtins.getEnv "NIX_MANAGER_PACKAGE_NAME"}.version',
        );
        rev = yield* evaluate(
          '(builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE")).inputs.nixpkgs.rev',
        );
        const network = yield* Network;
        const upstream = yield* network.text(
          "https://api.github.com/repos/NixOS/nixpkgs/commits/nixos-unstable",
        );
        const upstreamRev = yield* attempt(
          "Parse upstream revision",
          async () => {
            const sha = json(upstream).sha;
            if (!/^[a-f0-9]{40}$/.test(sha))
              throw new Error("Invalid nixpkgs revision");
            return sha as string;
          },
        );
        for (const recipe of p.recipes) {
          const text = yield* network.text(
            `https://raw.githubusercontent.com/NixOS/nixpkgs/${upstreamRev}/pkgs/by-name/${p.prefix}/${name}/${recipe}`,
          );
          const inherited = join(path, "pkgs/by-name", p.prefix, name, recipe);
          yield* attempt("Save upstream recipe", () =>
            writeFile(join(work, recipe), text),
          );
          yield* command(
            `Compare ${recipe} against nixos-unstable ${upstreamRev}`,
            ["diff", "-u", inherited, join(work, recipe)],
            { allowFailure: true },
          ).pipe(
            Effect.flatMap((result) =>
              result.code <= 1
                ? Effect.void
                : Effect.fail(
                    new ManagerError({
                      message: "Could not compare inherited recipe.",
                    }),
                  ),
            ),
          );
          if (
            recipe === (name === "t3code" ? "unwrapped.nix" : "package.nix")
          ) {
            const upstreamVersion = text.match(
              /^\s*version = "(\d+\.\d+\.\d+)";/m,
            )?.[1];
            if (!upstreamVersion)
              return yield* Effect.fail(
                new ManagerError({
                  message:
                    "Upstream recipe layout changed; inspect it before updating.",
                }),
              );
            log(
              `Locked nixpkgs ${locked} | nixos-unstable ${upstreamVersion}. Candidate uses the locked recipe.`,
            );
          }
        }
        const lockedOrder = yield* Effect.try({
          try: () => compareVersions(locked, version),
          catch: (e) => e as ManagerError,
        });
        if (
          lockedOrder > 0 ||
          (!r.force &&
            (locked === version || (order === 0 && pin.nixpkgsRev === rev)))
        ) {
          log(`${name} is current. Use --force to recalculate hashes.`);
          return;
        }
      } else if (order === 0 && !r.force) {
        log("PINCE is current.");
        return;
      }
      if (r.dryRun) {
        log(
          `Would fetch, hash and build ${name} ${version}, then atomically save ${pinFile}.`,
        );
        return;
      }
      const url =
        name === "pince"
          ? `https://github.com/${p.owner}/${p.repo}/releases/download/v${version}/PINCE-x86_64.AppImage`
          : `https://github.com/${p.owner}/${p.repo}/archive/refs/tags/v${version}.tar.gz`;
      const rawHash = (yield* command("Fetch release", [
        "nix-prefetch-url",
        ...(name === "pince" ? [] : ["--unpack"]),
        "--type",
        "sha256",
        url,
      ])).stdout.trim();
      const hash = (yield* command(
        "Convert source hash",
        [
          "nix",
          "hash",
          "convert",
          "--hash-algo",
          "sha256",
          "--to",
          "sri",
          rawHash,
        ],
        { quiet: true },
      )).stdout.trim();
      const candidate: Record<string, string> =
        name === "pince"
          ? { version, hash }
          : {
              version,
              ...(pin.channel ? { channel: pin.channel } : {}),
              srcHash: hash,
              nixpkgsRev: rev,
              ...(name === "t3code"
                ? { pnpmHash: fakeHash, cargoHash: fakeHash }
                : { nodeHash: fakeHash }),
            };
      yield* saveJson(env.NIX_MANAGER_PACKAGE_PIN, candidate);
      const expr = name === "pince" ? pinceExpression : candidateExpression;
      if (name === "t3code") {
        candidate.pnpmHash = yield* discoverHash(
          "T3 Code pnpm dependencies",
          `(${expr}).unwrapped.pnpmDeps`,
          env,
        );
        yield* saveJson(env.NIX_MANAGER_PACKAGE_PIN, candidate);
        candidate.cargoHash = yield* discoverHash(
          "T3 Code Cargo dependencies",
          `(${expr}).resourceMonitor.cargoDeps.vendorStaging`,
          env,
        );
      } else if (name === "opencode")
        candidate.nodeHash = yield* discoverHash(
          "OpenCode node_modules",
          `(${expr}).node_modules`,
          env,
        );
      yield* saveJson(env.NIX_MANAGER_PACKAGE_PIN, candidate);
      yield* command(
        `Build ${name} ${version} before saving pin`,
        [
          "nix",
          "build",
          "--no-link",
          "--impure",
          "--print-build-logs",
          "--expr",
          expr,
        ],
        { env },
      );
      yield* atomicPin(pinFile, before, candidate);
      log(`Updated ${pinFile}. Apply with nix-manager home switch.`);
    }),
  );

export interface UpdateItem {
  name: string;
  current: string;
  latest: string;
  kind: "input" | "package";
  currentDate?: string;
  latestDate?: string;
  /** owner/repo or URL of the input source. */
  source?: string;
  url?: string;
}
export interface UpdateReport {
  checkedAt: string;
  items: UpdateItem[];
  errors: string[];
}
export const checkUpdates = (config: Config) =>
  Effect.scoped(
    Effect.gen(function* () {
      const work = yield* temporary;
      const report: UpdateReport = {
        checkedAt: new Date().toISOString(),
        items: [],
        errors: [],
      };
      const flakeCheck = Effect.gen(function* () {
        const before = yield* readLock(join(config.flake, "flake.lock"));
        yield* command("Check flake inputs without changing the checkout", [
          "nix",
          "flake",
          "update",
          "--flake",
          config.flake,
          "--output-lock-file",
          join(work, "flake.lock"),
        ]);
        const after = yield* readLock(join(work, "flake.lock"));
        const fingerprint = (
          locked: Readonly<Record<string, unknown>> | undefined,
        ) =>
          JSON.stringify(
            Object.entries(locked || {}).sort(([a], [b]) => a.localeCompare(b)),
          );
        const revision = (
          locked: Readonly<Record<string, unknown>> | undefined,
          fallback: string,
        ) =>
          typeof locked?.rev === "string"
            ? locked.rev.slice(0, 12)
            : typeof locked?.narHash === "string"
              ? locked.narHash.slice(0, 19)
              : fallback;
        for (const [name, node] of Object.entries(after.nodes)) {
          const old = before.nodes[name]?.locked,
            next = node.locked;
          if (next && fingerprint(old) !== fingerprint(next)) {
            const from = lockRef({ locked: old }),
              to = lockRef({ locked: next });
            report.items.push({
              name,
              current: revision(old, "new"),
              latest: revision(next, "changed"),
              kind: "input",
              currentDate: from?.lastModified,
              latestDate: to?.lastModified,
              source: to?.source,
              url: to?.url,
            });
          }
        }
      });
      yield* flakeCheck.pipe(
        Effect.catchAll((e) =>
          Effect.sync(() => {
            report.errors.push(e.message);
          }),
        ),
      );
      for (const name of Object.keys(packages) as Package[]) {
        yield* Effect.gen(function* () {
          const pin = yield* attempt(`Read ${name} pin`, () =>
            readFile(join(config.flake, "pkgs", name, "source.json"), "utf8"),
          ).pipe(Effect.flatMap(parsePin));
          const version = yield* latest(name, pin.channel);
          if (
            compareVersions(version, pin.version) > 0 &&
            !packages[name].blockedVersions.includes(version)
          )
            report.items.push({
              name,
              current: pin.version,
              latest: version,
              kind: "package",
              source: `${packages[name].owner}/${packages[name].repo}`,
              url: `https://github.com/${packages[name].owner}/${packages[name].repo}/releases/tag/v${version}`,
            });
        }).pipe(
          Effect.catchAll((e) =>
            Effect.sync(() => {
              report.errors.push(`${name}: ${e.message}`);
            }),
          ),
        );
      }
      return report;
    }),
  );
