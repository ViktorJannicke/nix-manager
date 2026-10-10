import type { BuildProgress, LockChange, LockRef, PackageChange } from "./contracts";

const ansi = /\x1b\[[0-9;]*[a-zA-Z]/g;
export const stripAnsi = (text: string) => text.replace(ansi, "");

// Activity and result types from Nix's `--log-format internal-json` protocol.
const act = {
  copyPath: 100,
  fileTransfer: 101,
  copyPaths: 103,
  builds: 104,
  build: 105,
  substitute: 108,
} as const;
const res = {
  buildLogLine: 101,
  setPhase: 104,
  progress: 105,
  setExpected: 106,
  postBuildLogLine: 107,
} as const;

interface Activity {
  type: number;
  name: string;
  done: number;
  expected: number;
  running: number;
  failed: number;
  expectedByType: Map<number, number>;
  phase?: string;
}
interface ByType {
  live: Map<number, Activity>;
  done: number;
  expected: number;
  failed: number;
}

/** Name of a store path or derivation without its hash and `.drv` suffix. */
export const storeName = (path: string) =>
  path
    .replace(/^.*\/nix\/store\/[a-z0-9]{32}-/, "")
    .replace(/\.drv$/, "")
    .replace(/['"].*$/, "");

/**
 * Converts Nix's structured log into human-readable log text and aggregated
 * progress, using the same accounting as Nix's own progress bar.
 */
export class NixProgress {
  private activities = new Map<number, Activity>();
  private byType = new Map<number, ByType>();
  private started = false;

  private type(type: number): ByType {
    let entry = this.byType.get(type);
    if (!entry) {
      entry = { live: new Map(), done: 0, expected: 0, failed: 0 };
      this.byType.set(type, entry);
    }
    return entry;
  }

  private stats(type: number) {
    const entry = this.type(type);
    let done = entry.done,
      expected = entry.done,
      running = 0,
      failed = entry.failed;
    for (const activity of entry.live.values()) {
      done += activity.done;
      expected += activity.expected;
      running += activity.running;
      failed += activity.failed;
    }
    return { done, expected: Math.max(expected, entry.expected), running, failed };
  }

  /** Returns log text for one line of output, or undefined for pure progress lines. */
  line(raw: string): string | undefined {
    if (!raw.startsWith("@nix ")) return raw;
    let event: Record<string, unknown>;
    try {
      event = JSON.parse(raw.slice(5));
    } catch {
      return raw;
    }
    const id = Number(event.id);
    const fields = Array.isArray(event.fields) ? event.fields : [];
    switch (event.action) {
      case "msg": {
        const level = Number(event.level ?? 0);
        return level <= 3 ? stripAnsi(String(event.msg ?? "")) : undefined;
      }
      case "start": {
        this.started = true;
        const type = Number(event.type);
        const text = String(event.text ?? "");
        const activity: Activity = {
          type,
          name:
            type === act.build || type === act.substitute
              ? storeName(String(fields[0] ?? text))
              : text,
          done: 0,
          expected: 0,
          running: 0,
          failed: 0,
          expectedByType: new Map(),
        };
        this.activities.set(id, activity);
        this.type(type).live.set(id, activity);
        return Number(event.level ?? 0) <= 3 && text ? stripAnsi(text) : undefined;
      }
      case "stop": {
        const activity = this.activities.get(id);
        if (!activity) return undefined;
        const entry = this.type(activity.type);
        entry.done += activity.done;
        entry.failed += activity.failed;
        for (const [type, expected] of activity.expectedByType)
          this.type(type).expected -= expected;
        entry.live.delete(id);
        this.activities.delete(id);
        return undefined;
      }
      case "result": {
        const activity = this.activities.get(id);
        const type = Number(event.type);
        if (type === res.buildLogLine || type === res.postBuildLogLine)
          return `${activity?.name ?? "build"}> ${stripAnsi(String(fields[0] ?? ""))}`;
        if (!activity) return undefined;
        if (type === res.progress) {
          activity.done = Number(fields[0] ?? 0);
          activity.expected = Number(fields[1] ?? 0);
          activity.running = Number(fields[2] ?? 0);
          activity.failed = Number(fields[3] ?? 0);
        } else if (type === res.setExpected) {
          const target = Number(fields[0]);
          const previous = activity.expectedByType.get(target) ?? 0;
          const next = Number(fields[1] ?? 0);
          activity.expectedByType.set(target, next);
          this.type(target).expected += next - previous;
        } else if (type === res.setPhase) activity.phase = String(fields[0] ?? "");
        return undefined;
      }
      default:
        return undefined;
    }
  }

  snapshot(): BuildProgress {
    const running = [...this.activities.values()];
    const builds = running
      .filter((activity) => activity.type === act.build)
      .map((activity) =>
        activity.phase ? `${activity.name} (${activity.phase})` : activity.name,
      );
    const fetching = running
      .filter((activity) => activity.type === act.substitute)
      .map((activity) => activity.name);
    const build = this.stats(act.builds);
    const paths = this.stats(act.copyPaths);
    const download = this.stats(act.fileTransfer);
    const copied = this.stats(act.copyPath);
    return {
      evaluating: !this.started || (build.expected === 0 && paths.expected === 0 && !builds.length && !fetching.length),
      builds: { done: build.done, expected: build.expected, running: build.running, failed: build.failed },
      downloads: { done: paths.done, expected: paths.expected, running: paths.running },
      bytes: { done: Math.max(download.done, copied.done), expected: Math.max(download.expected, copied.expected) },
      current: [...builds, ...fetching].slice(0, 4),
    };
  }
}

/** Nix renders sizes with a binary unit chosen by magnitude. */
const units: Record<string, number> = { B: 1, KiB: 1024, MiB: 1024 ** 2, GiB: 1024 ** 3, TiB: 1024 ** 4 };
const sizePattern = /^([+-]\d+(?:\.\d+)?) (B|KiB|MiB|GiB|TiB)$/;
const versions = (value: string) =>
  value === "∅" ? [] : value.split(", ").map((v) => (v === "ε" ? "" : v));

/** Parses `nix store diff-closures` output into structured package changes. */
export function parseClosureDiff(text: string): PackageChange[] {
  const changes: PackageChange[] = [];
  for (const raw of stripAnsi(text).split("\n")) {
    const line = raw.trim();
    const separator = line.indexOf(": ");
    if (!line || separator <= 0) continue;
    const name = line.slice(0, separator);
    let rest = line.slice(separator + 2);
    let sizeDelta = 0;
    const sized = rest.match(/(?:^|, )([+-]\d+(?:\.\d+)?) (B|KiB|MiB|GiB|TiB)$/);
    if (sized) {
      sizeDelta = Math.round(Number(sized[1]) * units[sized[2]]);
      rest = rest.slice(0, rest.length - sized[0].length);
    }
    let before: string[] = [],
      after: string[] = [];
    if (rest.includes(" → ")) {
      const [left, right] = rest.split(" → ");
      before = versions(left);
      after = versions(right);
    } else if (rest && !sizePattern.test(rest)) continue;
    changes.push({
      name,
      before,
      after,
      sizeDelta,
      kind: !before.length && after.length
        ? "added"
        : before.length && !after.length
          ? "removed"
          : before.length || after.length
            ? "changed"
            : "rebuilt",
    });
  }
  return changes;
}

interface LockNode {
  locked?: Record<string, unknown>;
  original?: Record<string, unknown>;
}

export function lockRef(node: LockNode | undefined): LockRef | undefined {
  const locked = node?.locked;
  if (!locked) return undefined;
  const rev = typeof locked.rev === "string" ? locked.rev : undefined;
  const narHash = typeof locked.narHash === "string" ? locked.narHash : undefined;
  const lastModified =
    typeof locked.lastModified === "number"
      ? new Date(locked.lastModified * 1000).toISOString()
      : undefined;
  let url: string | undefined;
  let source: string | undefined;
  if ((locked.type === "github" || locked.type === "gitlab") && locked.owner && locked.repo) {
    source = `${locked.owner}/${locked.repo}`;
    const host = locked.type === "github" ? "github.com" : "gitlab.com";
    url = `https://${host}/${source}${rev ? `/commit/${rev}` : ""}`;
  } else if (typeof locked.url === "string") {
    source = locked.url.replace(/^git\+|\?.*$/g, "");
  } else if (typeof locked.path === "string") source = locked.path;
  return { rev, narHash, lastModified, source, url };
}

/** Compares two lock files and returns every changed node, including transitive ones. */
export function lockChanges(before: string, after: string): LockChange[] {
  const parse = (text: string): Record<string, LockNode> => {
    try {
      return JSON.parse(text).nodes ?? {};
    } catch {
      return {};
    }
  };
  const oldNodes = parse(before),
    newNodes = parse(after);
  const key = (node: LockNode | undefined) => JSON.stringify(node?.locked ?? null);
  return [...new Set([...Object.keys(oldNodes), ...Object.keys(newNodes)])]
    .filter((name) => name !== "root" && key(oldNodes[name]) !== key(newNodes[name]))
    .sort()
    .map((name) => ({
      name,
      before: lockRef(oldNodes[name]),
      after: lockRef(newNodes[name]),
    }));
}

/** `homeConfigurations."user@host"` style attribute for a flake fragment. */
export const attr = (name: string) =>
  /^[A-Za-z_][A-Za-z0-9_'-]*$/.test(name) ? name : JSON.stringify(name);

export const kernelVersion = (target: string | undefined) =>
  target?.match(/-linux-([^/]+)\//)?.[1];
