import type { Operation } from "../../src/contracts";
import type { Scope } from "../../src/domain";

export { bytes, editedPackages, plural, removedGenerations, reviewSummary, summarize } from "../../src/summary";

const relativeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 365 * 24 * 3600],
  ["month", 30 * 24 * 3600],
  ["week", 7 * 24 * 3600],
  ["day", 24 * 3600],
  ["hour", 3600],
  ["minute", 60],
];

/** "3 hours ago", "yesterday", "just now". */
export function relative(value: string | undefined, now = Date.now()) {
  if (!value) return "never";
  const seconds = (Date.parse(value) - now) / 1000;
  if (Math.abs(seconds) < 45) return "just now";
  for (const [unit, size] of units)
    if (Math.abs(seconds) >= size || unit === "minute")
      return relativeFormat.format(Math.round(seconds / size), unit);
  return "just now";
}

export const dateTime = (value: string) =>
  new Date(value).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    year: new Date(value).getFullYear() === new Date().getFullYear() ? undefined : "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });

export const day = (value: string | undefined) =>
  value
    ? new Date(value).toLocaleDateString(undefined, {
        month: "short",
        day: "numeric",
        year: "numeric",
      })
    : "unknown";

/** Days between two dates, rounded; undefined if either is unknown. */
export function daysBetween(from?: string, to?: string) {
  if (!from || !to) return undefined;
  return Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000);
}

export function duration(operation: Operation, now = Date.now()) {
  const end = operation.finishedAt ? Date.parse(operation.finishedAt) : now;
  return formatSeconds(Math.max(0, (end - Date.parse(operation.startedAt)) / 1000));
}

export function formatSeconds(seconds: number) {
  if (seconds < 10) return `${seconds.toFixed(1)}s`;
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${Math.floor(seconds % 60)}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}

export const scopeLabel = (scope: Scope) =>
  scope === "all" ? "Home & system" : scope === "home" ? "Home" : "System";

export const shortRev = (rev?: string) => (rev ? rev.slice(0, 7) : "—");

export const versions = (list: string[]) =>
  list.length ? list.map((v) => v || "unversioned").join(", ") : "—";

export const storeBaseName = (path: string) =>
  path.replace(/^\/nix\/store\/[a-z0-9]{32}-/, "");
