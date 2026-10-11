import type { Operation } from "../../src/contracts";
import type { Scope } from "../../src/domain";
import { getLocale, t } from "../../src/i18n";

export { bytes, editedPackages, removedGenerations, reviewSummary, summarize, timeLeft, usualSeconds } from "../../src/summary";
import { timeLeft, usualSeconds } from "../../src/summary";

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
  if (!value) return t("never");
  const seconds = (Date.parse(value) - now) / 1000;
  if (Math.abs(seconds) < 45) return t("just now");
  const relativeFormat = new Intl.RelativeTimeFormat(getLocale(), { numeric: "auto" });
  for (const [unit, size] of units)
    if (Math.abs(seconds) >= size || unit === "minute")
      return relativeFormat.format(Math.round(seconds / size), unit);
  return t("just now");
}

export const dateTime = (value: string) =>
  new Date(value).toLocaleString(getLocale(), {
    month: "short",
    day: "numeric",
    year: new Date(value).getFullYear() === new Date().getFullYear() ? undefined : "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });

export const day = (value: string | undefined) =>
  value
    ? new Date(value).toLocaleDateString(getLocale(), {
        month: "short",
        day: "numeric",
        year: "numeric",
      })
    : t("unknown");

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
  if (seconds < 10) return t("{seconds}s", { seconds: Number(seconds.toFixed(1)) });
  if (seconds < 60) return t("{seconds}s", { seconds: Math.round(seconds) });
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return t("{minutes}m {seconds}s", { minutes, seconds: Math.floor(seconds % 60) });
  return t("{hours}h {minutes}m", { hours: Math.floor(minutes / 60), minutes: minutes % 60 });
}

export const scopeLabel = (scope: Scope) =>
  scope === "all" ? t("Your user and system") : scope === "home" ? t("Your user") : t("System");

/** The scope inside a sentence: "your user", "system". */
export const scopeText = (scope: Scope) =>
  scope === "all" ? t("your user and system") : scope === "home" ? t("your user") : t("system");

export const shortRev = (rev?: string) => (rev ? rev.slice(0, 7) : "—");

export const versions = (list: string[]) =>
  list.length ? list.map((v) => v || t("unversioned")).join(", ") : "—";

export const storeBaseName = (path: string) =>
  path.replace(/^\/nix\/store\/[a-z0-9]{32}-/, "");

/** "About 3 minutes left" for a running operation, judged from earlier runs of the same kind. */
export const remaining = (operations: readonly Operation[], operation: Operation, now = Date.now()) =>
  timeLeft(usualSeconds(operations, operation.request), (now - Date.parse(operation.startedAt)) / 1000);
