import { lstat, readFile, readdir, readlink, realpath } from "node:fs/promises";
import { statfsSync } from "node:fs";
import { basename, dirname, join } from "node:path";
import { createHash } from "node:crypto";
import { systemProfile, type Config } from "./domain";
import type { Generation, Repository, SystemState } from "./contracts";
import { kernelVersion } from "./nix";

const safe = async <A>(f: () => Promise<A>): Promise<A | undefined> => {
  try {
    return await f();
  } catch {
    return undefined;
  }
};
const resolved = (path: string) => safe(() => realpath(path));

export async function git(flake: string, ...args: string[]) {
  const child = Bun.spawn(["git", "-C", flake, ...args], {
    stdin: "ignore",
    stdout: "pipe",
    stderr: "ignore",
  });
  const [code, text] = await Promise.all([
    child.exited,
    new Response(child.stdout).text(),
  ]);
  return code === 0 ? text : undefined;
}

export const hashText = (text: string | Buffer) =>
  createHash("sha256").update(text).digest("hex");

export async function readLock(flake: string) {
  return (await safe(() => readFile(join(flake, "flake.lock"), "utf8"))) ?? "";
}

export async function repositoryState(flake: string): Promise<Repository> {
  const [status, branch, head, lockDiff, lock, diff, upstream] = await Promise.all([
    git(flake, "status", "--porcelain=v1", "--untracked-files=normal"),
    git(flake, "symbolic-ref", "--quiet", "--short", "HEAD"),
    git(flake, "log", "-1", "--format=%H%x00%s%x00%cI"),
    git(flake, "status", "--porcelain=v1", "--", "flake.lock"),
    readLock(flake),
    // Status alone misses further edits to an already modified file.
    git(flake, "diff", "HEAD", "--binary"),
    git(flake, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"),
  ]);
  const [revision, subject, committedAt] = head?.trim().split("\0") ?? [];
  const known = status ?? "unknown";
  return {
    fingerprint: createHash("sha256")
      .update(lock)
      .update("\0")
      .update(known)
      .update("\0")
      .update(diff ?? "")
      .digest("hex"),
    clean: status === "",
    changes: status ? status.split("\n").filter(Boolean) : [],
    branch: branch?.trim() || undefined,
    revision: revision || undefined,
    subject: subject || undefined,
    committedAt: committedAt || undefined,
    upstream: upstream?.trim() || undefined,
    lockModified: Boolean(lockDiff?.trim()),
    lockHash: hashText(lock),
  };
}

async function systemMeta(link: string) {
  const [version, kernel] = await Promise.all([
    safe(() => readFile(join(link, "nixos-version"), "utf8")),
    safe(() => readlink(join(link, "kernel"))),
  ]);
  return { version: version?.trim() || undefined, kernel: kernelVersion(kernel) };
}

/** The generation number a profile link points to, such as 79 for system-79-link. */
export async function profileGeneration(profile: string): Promise<number | undefined> {
  const target = await safe(() => readlink(profile));
  const number = Number(target?.match(/-(\d+)-link$/)?.[1]);
  return Number.isInteger(number) && number > 0 ? number : undefined;
}

export async function generations(
  profile: string,
  current: string | undefined,
  booted?: string,
  system = false,
): Promise<Generation[]> {
  const prefix = `${basename(profile)}-`;
  const names = (await safe(() => readdir(dirname(profile)))) ?? [];
  const found = await Promise.all(
    names.map(async (name): Promise<Generation | undefined> => {
      if (!name.startsWith(prefix) || !name.endsWith("-link")) return undefined;
      const number = Number(name.slice(prefix.length, -5));
      if (!Number.isInteger(number) || number < 1) return undefined;
      const link = join(dirname(profile), name);
      const [output, stat] = await Promise.all([resolved(link), safe(() => lstat(link))]);
      if (!output || !stat) return undefined;
      return {
        number,
        createdAt: stat.mtime.toISOString(),
        output,
        current: output === current,
        ...(system ? { booted: output === booted, ...(await systemMeta(link)) } : {}),
      };
    }),
  );
  return found
    .filter((generation): generation is Generation => !!generation)
    .sort((a, b) => b.number - a.number);
}

export async function observeSystem(config: Config): Promise<SystemState> {
  const [repository, home, current, booted] = await Promise.all([
    repositoryState(config.flake),
    resolved(config.homeProfile),
    resolved("/run/current-system"),
    resolved("/run/booted-system"),
  ]);
  const [homeGenerations, systemGenerations, currentMeta, bootedMeta, parts] =
    await Promise.all([
      generations(config.homeProfile, home),
      generations(systemProfile, current, booted, true),
      current ? systemMeta(current) : Promise.resolve(undefined),
      booted ? systemMeta(booted) : Promise.resolve(undefined),
      // Mirrors nixos-rebuild: a reboot is needed when boot-critical parts differ.
      Promise.all(
        ["kernel", "initrd", "kernel-modules"].map(async (part) => [
          await resolved(`/run/booted-system/${part}`),
          await resolved(`/run/current-system/${part}`),
        ]),
      ),
    ]);
  let disk: SystemState["disk"];
  try {
    const stats = statfsSync("/nix/store");
    disk = {
      path: "/nix/store",
      free: stats.bavail * stats.bsize,
      total: stats.blocks * stats.bsize,
    };
  } catch {
    disk = undefined;
  }
  return {
    observedAt: new Date().toISOString(),
    repository,
    generations: { home: homeGenerations, system: systemGenerations },
    current: { home, system: current, booted },
    nixosVersion: currentMeta?.version,
    kernel: { current: currentMeta?.kernel, booted: bootedMeta?.kernel },
    rebootRequired:
      !!current && !!booted && parts.some(([a, b]) => a !== undefined && a !== b),
    disk,
  };
}

/**
 * Caches the last observation. Concurrent refreshes share one pass so
 * clients and operation events never queue redundant git or filesystem scans.
 */
export class Observer {
  current?: SystemState;
  private pending?: Promise<SystemState>;
  private again = false;
  private listeners = new Set<() => void>();
  constructor(private readonly config: Config) {}
  onChange(listener: () => void) {
    this.listeners.add(listener);
    return () => void this.listeners.delete(listener);
  }
  refresh(): Promise<SystemState> {
    if (this.pending) {
      this.again = true;
      return this.pending;
    }
    this.pending = (async () => {
      try {
        do {
          this.again = false;
          this.current = await observeSystem(this.config);
        } while (this.again);
        for (const listener of this.listeners) listener();
        return this.current;
      } finally {
        this.pending = undefined;
      }
    })();
    return this.pending;
  }
}
