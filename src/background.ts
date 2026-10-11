import { readFileSync } from "node:fs";
import { availableParallelism, loadavg } from "node:os";
import { join } from "node:path";
import { request, type Config } from "./domain";
import { atomicJson, type Manager } from "./manager";
import type { Operation } from "./contracts";
import { readPreferences } from "./preferences";
import { onMains } from "./hardware";

/** Not busy: the load of the last minute is below half the processors. */
export const notBusy = () => loadavg()[0] < availableParallelism() / 2;

/** The updates last prepared in the background, so the same ones are not prepared twice. */
interface Prepared {
  signature: string;
  reviewId: string;
}
const preparedPath = (config: Config) => join(config.stateDir, "background.json");

export function lastPrepared(config: Config): Prepared | undefined {
  try {
    return JSON.parse(readFileSync(preparedPath(config), "utf8"));
  } catch {
    return undefined;
  }
}

/**
 * Starts preparing the input updates the last check found, when the
 * preferences allow it, these updates were not prepared before and the
 * computer is plugged in and not busy. Returns the operation, or why not.
 */
export function prepareInBackground(
  manager: Manager,
  conditions: { mains: () => boolean; idle: () => boolean } = { mains: () => onMains(process.env.NIX_MANAGER_SYSFS), idle: notBusy },
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
