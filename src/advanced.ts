import {
  existsSync,
  lstatSync,
  mkdirSync,
  chmodSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
  realpathSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { basename, dirname, join } from "node:path";
import { createHash } from "node:crypto";
import { ManagerError, type Config, type Request } from "./domain";
import type { AdvancedState, BuildReview, Generation, InputProposal } from "./contracts";
import { Effect } from "effect";
import { runProcess } from "./process";

const safeRealpath = (path: string) => {
  try {
    return realpathSync(path);
  } catch {
    return undefined;
  }
};

function generations(profile: string, activePath: string): Generation[] {
  const directory = dirname(profile);
  const prefix = `${basename(profile)}-`;
  const active = safeRealpath(activePath);
  if (!existsSync(directory)) return [];
  return readdirSync(directory)
    .flatMap((name): Generation[] => {
      const match = name.match(new RegExp(`^${prefix}(\\d+)-link$`));
      if (!match) return [];
      const link = join(directory, name);
      const output = safeRealpath(link);
      if (!output) return [];
      return [{
        number: Number(match[1]),
        createdAt: lstatSync(link).mtime.toISOString(),
        output,
        active: output === active,
      }];
    })
    .sort((a, b) => b.number - a.number);
}

export function repositoryState(flake: string) {
  const git = Bun.spawnSync(
    ["git", "-C", flake, "status", "--porcelain=v1", "--untracked-files=normal"],
    { stdout: "pipe", stderr: "pipe" },
  );
  const status = git.exitCode === 0 ? git.stdout.toString() : "unknown";
  const lock = existsSync(join(flake, "flake.lock"))
    ? readFileSync(join(flake, "flake.lock"))
    : Buffer.from("");
  const fingerprint = createHash("sha256")
    .update(lock)
    .update("\0")
    .update(status)
    .digest("hex");
  const revision = Bun.spawnSync(
    ["git", "-C", flake, "rev-parse", "--verify", "HEAD"],
    { stdout: "pipe", stderr: "pipe" },
  );
  return {
    fingerprint,
    clean: status === "",
    changes: status === "unknown" ? [] : status.trim().split("\n").filter(Boolean),
    revision: revision.exitCode === 0 ? revision.stdout.toString().trim() : undefined,
    observedAt: new Date().toISOString(),
  };
}

export function reviewPath(config: Config, id: string) {
  return join(config.stateDir, "reviews", id);
}

export function finalizeReview(config: Config, request: Request): BuildReview {
  const root = reviewPath(config, request.reviewId!);
  const homeOutput = safeRealpath(join(root, "home"));
  const systemOutput = safeRealpath(join(root, "system"));
  const inspect = (output: string | undefined, current: string | undefined) => {
    if (!output?.startsWith("/nix/store/")) return {};
    const size = Bun.spawnSync(["nix", "path-info", "--json", "-S", output], {
      stdout: "pipe", stderr: "pipe",
    });
    let closureSize: number | undefined;
    try {
      const value = Object.values(JSON.parse(size.stdout.toString()))[0] as { closureSize?: number };
      closureSize = value?.closureSize;
    } catch { /* Observation remains unknown. */ }
    const diff = current
      ? Bun.spawnSync(["nix", "store", "diff-closures", current, output], {
          stdout: "pipe", stderr: "pipe",
        })
      : undefined;
    return {
      closureSize,
      diff: diff?.exitCode === 0 ? diff.stdout.toString().slice(0, 32_000) : undefined,
    };
  };
  const homeInspection = inspect(homeOutput, safeRealpath(config.homeProfile));
  const systemInspection = inspect(systemOutput, safeRealpath("/run/current-system"));
  const review: BuildReview = {
    id: request.reviewId!,
    host: request.host || config.hostKey,
    scope: request.scope,
    fingerprint: request.fingerprint!,
    createdAt: new Date().toISOString(),
    homeOutput,
    systemOutput,
    homeClosureSize: homeInspection.closureSize,
    systemClosureSize: systemInspection.closureSize,
    homeDiff: homeInspection.diff,
    systemDiff: systemInspection.diff,
  };
  mkdirSync(root, { recursive: true, mode: 0o700 });
  writeFileSync(join(root, "review.json"), JSON.stringify(review, null, 2) + "\n", {
    mode: 0o600,
  });
  return review;
}

export function generationReview(
  config: Config,
  kind: "home" | "system",
  number: number,
): BuildReview {
  const state = advancedState(config);
  const generation = state.generations[kind].find((item) => item.number === number);
  if (!generation)
    throw new ManagerError({ message: "The selected generation is no longer available." });
  const id = crypto.randomUUID();
  const root = reviewPath(config, id);
  const review: BuildReview = {
    id,
    host: config.hostKey,
    scope: kind,
    fingerprint: state.repository.fingerprint,
    createdAt: new Date().toISOString(),
    ...(kind === "home"
      ? { homeOutput: generation.output }
      : { systemOutput: generation.output }),
  };
  mkdirSync(root, { recursive: true, mode: 0o700 });
  symlinkSync(generation.output, join(root, kind));
  writeFileSync(join(root, "review.json"), JSON.stringify(review, null, 2) + "\n", {
    mode: 0o600,
  });
  return review;
}

export function reviews(config: Config): BuildReview[] {
  const root = join(config.stateDir, "reviews");
  if (!existsSync(root)) return [];
  return readdirSync(root)
    .flatMap((id): BuildReview[] => {
      try {
        return [JSON.parse(readFileSync(join(root, id, "review.json"), "utf8"))];
      } catch {
        return [];
      }
    })
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

export function getReview(config: Config, id: string): BuildReview {
  if (!/^[a-f0-9-]{8,64}$/.test(id))
    throw new ManagerError({ message: "Invalid build review." });
  try {
    return JSON.parse(
      readFileSync(join(reviewPath(config, id), "review.json"), "utf8"),
    );
  } catch {
    throw new ManagerError({ message: "The selected build review is unavailable." });
  }
}

export function markReviewApplied(config: Config, id: string) {
  const path = join(reviewPath(config, id), "review.json");
  const review: BuildReview = JSON.parse(readFileSync(path, "utf8"));
  review.appliedAt = new Date().toISOString();
  writeFileSync(path, JSON.stringify(review, null, 2) + "\n", { mode: 0o600 });
}

export function removeReview(config: Config, id: string) {
  if (!/^[a-f0-9-]{8,64}$/.test(id))
    throw new ManagerError({ message: "Invalid build review." });
  rmSync(reviewPath(config, id), { recursive: true, force: true });
}

function proposals(config: Config): InputProposal[] {
  const root = join(config.stateDir, "input-proposals");
  if (!existsSync(root)) return [];
  return readdirSync(root).flatMap((id): InputProposal[] => {
    try {
      return [JSON.parse(readFileSync(join(root, id, "proposal.json"), "utf8"))];
    } catch {
      return [];
    }
  }).sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

const lockVersions = (path: string) => {
  const lock = JSON.parse(readFileSync(path, "utf8"));
  return Object.fromEntries(
    Object.entries(lock.nodes || {}).map(([name, raw]) => {
      const node = raw as { locked?: Record<string, unknown> };
      const value = node.locked?.rev || node.locked?.narHash || node.locked?.lastModified || "unlocked";
      return [name, String(value)];
    }),
  ) as Record<string, string>;
};

export async function proposeInputs(config: Config, inputs: string[]): Promise<InputProposal> {
  if (!inputs.length || inputs.some((name) => !/^[A-Za-z0-9_-]+$/.test(name)))
    throw new ManagerError({ message: "Select one or more valid flake inputs." });
  const before = repositoryState(config.flake);
  const id = crypto.randomUUID();
  const root = join(config.stateDir, "input-proposals", id);
  const proposed = join(root, "flake.lock");
  mkdirSync(root, { recursive: true, mode: 0o700 });
  await Effect.runPromise(
    runProcess({
      label: "Resolve selected flake inputs",
      argv: [
        "nix", "flake", "update", ...inputs,
        "--flake", config.flake,
        "--output-lock-file", proposed,
      ],
      cwd: config.flake,
    }),
  );
  const oldNodes = lockVersions(join(config.flake, "flake.lock"));
  const newNodes = lockVersions(proposed);
  const changes = [...new Set([...Object.keys(oldNodes), ...Object.keys(newNodes)])]
    .filter((name) => oldNodes[name] !== newNodes[name])
    .map((name) => ({ name, before: oldNodes[name] || "absent", after: newNodes[name] || "absent" }));
  const proposal: InputProposal = {
    id,
    fingerprint: before.fingerprint,
    createdAt: new Date().toISOString(),
    inputs,
    changes,
  };
  writeFileSync(join(root, "proposal.json"), JSON.stringify(proposal, null, 2) + "\n", { mode: 0o600 });
  return proposal;
}

export function applyInputProposal(config: Config, id: string, fingerprint: string) {
  if (!/^[a-f0-9-]{8,64}$/.test(id))
    throw new ManagerError({ message: "Invalid input proposal." });
  const root = join(config.stateDir, "input-proposals", id);
  const proposal: InputProposal = JSON.parse(readFileSync(join(root, "proposal.json"), "utf8"));
  if (proposal.fingerprint !== fingerprint || repositoryState(config.flake).fingerprint !== fingerprint)
    throw new ManagerError({ message: "The checkout changed after this input proposal was created." });
  const target = join(config.flake, "flake.lock");
  const temporary = `${target}.${id}.tmp`;
  try {
    writeFileSync(temporary, readFileSync(join(root, "flake.lock")), { flag: "wx" });
    chmodSync(temporary, statSync(target).mode & 0o777);
    renameSync(temporary, target);
  } finally {
    rmSync(temporary, { force: true });
  }
  return proposal;
}

export function advancedState(config: Config): AdvancedState {
  const home = generations(config.homeProfile, config.homeProfile);
  const system = generations("/nix/var/nix/profiles/system", "/run/current-system");
  return {
    repository: repositoryState(config.flake),
    generations: { home, system },
    current: {
      home: safeRealpath(config.homeProfile),
      system: safeRealpath("/run/current-system"),
    },
    reviews: reviews(config),
    inputProposals: proposals(config),
  };
}
