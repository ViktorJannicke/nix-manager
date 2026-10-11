import { Effect } from "effect";
import type { Command, ManagerError } from "./domain";
import type { SystemState } from "./contracts";
import { runProcess, type Result } from "./process";
import { removedGenerations } from "./summary";

export interface CleanupEstimate {
  /** Restore points the cleanup removes. */
  generations: number;
  /** Bytes in store paths only those restore points use. */
  bytes: number;
}

/**
 * Measures what a cleanup keeping the newest `retention` restore points frees,
 * without removing anything. Other roots, such as build results, can still keep
 * some of it; paths nothing uses any more are collected on top.
 */
export const estimateCleanup = (
  system: SystemState,
  scope: "home" | "all",
  retention: number,
  run: (command: Command) => Effect.Effect<Result, ManagerError> = (command) => runProcess(command),
) =>
  Effect.gen(function* () {
    const removed = (scope === "all" ? (["home", "system"] as const) : (["home"] as const)).flatMap((profile) =>
      removedGenerations(system.generations[profile], retention),
    );
    // The booted system stays a root until the next restart, so it frees nothing yet.
    const kept = new Set(
      [...system.generations.home, ...system.generations.system]
        .filter((generation) => !removed.includes(generation) || generation.booted)
        .map((generation) => generation.output),
    );
    for (const path of Object.values(system.current)) if (path) kept.add(path);
    const freed = [...new Set(removed.map((generation) => generation.output))].filter((path) => !kept.has(path));
    if (!freed.length) return { generations: removed.length, bytes: 0 } satisfies CleanupEstimate;
    const closure = (paths: Iterable<string>, sizes: boolean) =>
      run({
        label: "Measure what cleaning up frees",
        argv: ["nix", "path-info", "--recursive", ...(sizes ? ["--size"] : []), ...paths],
        quiet: true,
        fullOutput: true,
      });
    // Without paths, `nix path-info` would read the flake in the working directory.
    const [leaving, staying] = yield* Effect.all(
      [closure(freed, true), kept.size ? closure(kept, false) : Effect.succeed({ code: 0, stdout: "", stderr: "" })],
      { concurrency: 2 },
    );
    const stays = new Set(staying.stdout.split("\n").map((line) => line.trim()));
    let bytes = 0;
    for (const line of leaving.stdout.split("\n")) {
      const [path, size] = line.trim().split(/\s+/);
      if (path && !stays.has(path)) bytes += Number(size) || 0;
    }
    return { generations: removed.length, bytes } satisfies CleanupEstimate;
  });
