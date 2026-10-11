import { readFileSync, statfsSync } from "node:fs";
import { availableParallelism, loadavg } from "node:os";
import { join } from "node:path";
import { request, type Config } from "./domain";
import { atomicJson, type Manager } from "./manager";
import type { Operation } from "./contracts";
import { readPreferences } from "./preferences";
import { onMains } from "./hardware";

/** Not busy: the load of the last minute is below half the processors. */
export const notBusy = () => loadavg()[0] < availableParallelism() / 2;

type Conditions = { mains: () => boolean; idle: () => boolean };
const computer: Conditions = { mains: () => onMains(process.env.NIX_MANAGER_SYSFS), idle: notBusy };

const read = <A>(path: string): A | undefined => {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch {
    return undefined;
  }
};

/** The updates last prepared in the background, so the same ones are not prepared twice. */
interface Prepared {
  signature: string;
  reviewId: string;
}
const preparedPath = (config: Config) => join(config.stateDir, "background.json");

export const lastPrepared = (config: Config) => read<Prepared>(preparedPath(config));

/**
 * Starts preparing the input updates the last check found, when the
 * preferences allow it, these updates were not prepared before and the
 * computer is plugged in and not busy. Returns the operation, or why not.
 */
export function prepareInBackground(
  manager: Manager,
  conditions = computer,
): Operation | string {
  const config = manager.config;
  if (!readPreferences(config).prepareUpdates) return "Preparing updates in the background is off.";
  // Package pins change files in the checkout, so only source updates are prepared unasked.
  const inputs = manager.updates()?.items.filter((item) => item.kind === "input") ?? [];
  if (!inputs.length) return "There are no updates to prepare.";
  const signature = JSON.stringify(inputs.map((item) => [item.name, item.latest]));
  if (lastPrepared(config)?.signature === signature) return "These updates were prepared already.";
  if (manager.busy) return "Something else is running.";
  if (!conditions.mains()) return "The computer runs on battery.";
  if (!conditions.idle()) return "The computer is busy.";
  const operation = manager.start(request({ action: "review-build", host: config.hostKey, update: true }));
  atomicJson(preparedPath(config), { signature, reviewId: operation.request.reviewId! } satisfies Prepared);
  return operation;
}

/** Automatic cleanup keeps this many restore points of the apps. */
export const keptAutomatically = 5;
const week = 7 * 24 * 60 * 60_000;

/** The last automatic cleanup, with the free space before it to say what it freed. */
interface Cleaned {
  at: string;
  operationId: string;
  free?: number;
}
const cleanedPath = (config: Config) => join(config.stateDir, "cleanup.json");
export const lastCleanup = (config: Config) => read<Cleaned>(cleanedPath(config));

export function freeSpace() {
  try {
    const stats = statfsSync("/nix/store");
    return stats.bavail * stats.bsize;
  } catch {
    return undefined;
  }
}

/**
 * Removes older restore points of the apps and unused store paths, at most once
 * a week, when the preferences allow it and the computer is plugged in and not
 * busy. System restore points need a password, so they are never removed unasked.
 */
export function cleanUpInBackground(manager: Manager, conditions = computer, now = Date.now()): Operation | string {
  const config = manager.config;
  if (!readPreferences(config).cleanUp) return "Cleaning up automatically is off.";
  const last = lastCleanup(config);
  if (last && now - Date.parse(last.at) < week) return "The last cleanup was less than a week ago.";
  if (manager.busy) return "Something else is running.";
  if (!conditions.mains()) return "The computer runs on battery.";
  if (!conditions.idle()) return "The computer is busy.";
  const free = freeSpace();
  const operation = manager.start(
    request({ action: "gc", scope: "home", host: config.hostKey, retention: keptAutomatically }),
  );
  atomicJson(cleanedPath(config), { at: new Date(now).toISOString(), operationId: operation.id, free } satisfies Cleaned);
  return operation;
}
