import {
  chmodSync,
  existsSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { readFile, readdir, readlink, realpath } from "node:fs/promises";
import { join } from "node:path";
import { Effect } from "effect";
import {
  ManagerError,
  reviewRoot,
  validId,
  type Command,
  type Config,
  type Request,
} from "./domain";
import type { BuildReview, ReviewEdits, ReviewOutput } from "./contracts";
import type { Result } from "./process";
import { kernelVersion, lockChanges, parseClosureDiff } from "./nix";
import { hashText, readLock } from "./observe";

type Run = (command: Command) => Effect.Effect<Result, ManagerError>;

const reviewFile = (config: Config, id: string) =>
  join(reviewRoot(config, id), "review.json");

export function getReview(config: Config, id: string): BuildReview {
  if (!validId(id)) throw new ManagerError({ message: "Invalid review." });
  try {
    return JSON.parse(readFileSync(reviewFile(config, id), "utf8"));
  } catch {
    throw new ManagerError({ message: "The selected review is unavailable." });
  }
}

export async function listReviews(config: Config): Promise<BuildReview[]> {
  const root = join(config.stateDir, "reviews");
  const ids = await readdir(root).catch(() => [] as string[]);
  const found = await Promise.all(
    ids.map(async (id) => {
      try {
        return JSON.parse(await readFile(join(root, id, "review.json"), "utf8")) as BuildReview;
      } catch {
        return undefined;
      }
    }),
  );
  return found
    .filter((review): review is BuildReview => !!review?.id)
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

/** Removes a review and its GC roots; the outputs become collectable. */
export function removeReview(config: Config, id: string) {
  if (!validId(id)) throw new ManagerError({ message: "Invalid review." });
  rmSync(reviewRoot(config, id), { recursive: true, force: true });
}

export const atomicWrite = (path: string, data: string | Buffer, mode = 0o600) => {
  const temporary = `${path}.${crypto.randomUUID()}.tmp`;
  try {
    writeFileSync(temporary, data, { mode, flag: "wx" });
    chmodSync(temporary, mode);
    renameSync(temporary, path);
  } finally {
    rmSync(temporary, { force: true });
  }
};

const safe = async <A>(f: () => Promise<A>) => {
  try {
    return await f();
  } catch {
    return undefined;
  }
};

/** Snapshot of the checkout taken before a review build evaluates it. */
export interface ReviewBase {
  fingerprint: string;
  lock: string;
}

/**
 * Records what a finished review build would change: package versions,
 * closure sizes, NixOS version and kernel transitions, and input changes.
 */
export const compareReview = (
  config: Config,
  r: Request,
  base: ReviewBase,
  operationId: string,
  run: Run,
  edits?: ReviewEdits,
  undo?: BuildReview["undo"],
) =>
  Effect.gen(function* () {
    const id = r.reviewId!;
    const root = reviewRoot(config, id);
    const inspect = (scope: "home" | "system", current: string | undefined) =>
      Effect.gen(function* () {
        const path = yield* Effect.promise(() => safe(() => realpath(join(root, scope))));
        if (!path) return undefined;
        const output: ReviewOutput = { path, changes: [], compared: false };
        const sizes = yield* run({
          label: "Measure closure sizes",
          argv: ["nix", "path-info", "--json", "--closure-size", path, ...(current ? [current] : [])],
          quiet: true,
          allowFailure: true,
        });
        try {
          const parsed = JSON.parse(sizes.stdout) as
            | Record<string, { closureSize?: number }>
            | Array<{ path: string; closureSize?: number }>;
          const byPath = Array.isArray(parsed)
            ? Object.fromEntries(parsed.map((entry) => [entry.path, entry]))
            : parsed;
          output.closureSize = byPath[path]?.closureSize;
          if (current) output.currentClosureSize = byPath[current]?.closureSize;
        } catch {
          // Sizes are informative only.
        }
        if (current && current !== path) {
          const diff = yield* run({
            label: "Compare package versions",
            argv: ["nix", "store", "diff-closures", current, path],
            quiet: true,
            allowFailure: true,
          });
          if (diff.code === 0) {
            output.changes = parseClosureDiff(diff.stdout);
            output.compared = true;
          }
        } else if (current) output.compared = true;
        if (scope === "system") {
          const meta = (link: string | undefined) =>
            Effect.promise(async () => ({
              version: link
                ? (await safe(() => readFile(join(link, "nixos-version"), "utf8")))?.trim()
                : undefined,
              kernel: link ? kernelVersion(await safe(() => readlink(join(link, "kernel")))) : undefined,
            }));
          const [before, after] = [yield* meta(current), yield* meta(path)];
          output.version = { before: before.version, after: after.version };
          output.kernel = { before: before.kernel, after: after.kernel };
        }
        return output;
      });
    const currentHome = yield* Effect.promise(() => safe(() => realpath(config.homeProfile)));
    const currentSystem = yield* Effect.promise(() => safe(() => realpath("/run/current-system")));
    const home = r.scope !== "system" ? yield* inspect("home", currentHome) : undefined;
    const system = r.scope !== "home" ? yield* inspect("system", currentSystem) : undefined;
    if (!home && !system)
      return yield* Effect.fail(
        new ManagerError({ message: "The build did not produce any output to review." }),
      );
    const proposed = join(root, "flake.lock");
    const proposal = existsSync(proposed)
      ? yield* Effect.promise(() => readFile(proposed, "utf8"))
      : undefined;
    const review: BuildReview = {
      id,
      host: r.host || config.hostKey,
      scope: r.scope,
      createdAt: new Date().toISOString(),
      fingerprint: base.fingerprint,
      source: r.generation !== undefined || undo ? "generation" : proposal ? "inputs" : "checkout",
      ...(r.generation !== undefined ? { generation: r.generation } : {}),
      ...(undo ? { undo } : {}),
      ...(edits ? { edits } : {}),
      ...(proposal
        ? {
            inputs: {
              names: [...(r.inputs ?? [])],
              changes: lockChanges(base.lock, proposal),
              baseLockHash: hashText(base.lock),
            },
          }
        : {}),
      ...(home ? { home } : {}),
      ...(system ? { system } : {}),
      operationId,
    };
    yield* Effect.try({
      try: () => atomicWrite(reviewFile(config, id), JSON.stringify(review, null, 2) + "\n"),
      catch: (error) => new ManagerError({ message: `Save review: ${error}` }),
    });
    return review;
  });

/** Copies the reviewed lock into the checkout unless flake.lock changed since. */
export const writeReviewedLock = (config: Config, review: BuildReview) =>
  Effect.tryPromise({
    try: async () => {
      if (!review.inputs) return;
      const target = join(config.flake, "flake.lock");
      if (hashText(await readLock(config.flake)) !== review.inputs.baseLockHash)
        throw new ManagerError({
          message:
            "flake.lock changed after this review was prepared. Prepare the update again.",
        });
      const proposal = readFileSync(join(reviewRoot(config, review.id), "flake.lock"));
      atomicWrite(
        target,
        proposal,
        existsSync(target) ? statSync(target).mode & 0o777 : 0o644,
      );
    },
    catch: (error) =>
      error instanceof ManagerError
        ? error
        : new ManagerError({ message: `Write flake.lock: ${error}` }),
  });
