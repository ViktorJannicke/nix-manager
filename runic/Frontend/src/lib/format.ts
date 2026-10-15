import type { Operation } from "../generated/types.js";
import { getLocale, m } from "./i18n";
import { usualSeconds, type Scope } from "./requests";

const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 365 * 24 * 3600],
  ["month", 30 * 24 * 3600],
  ["week", 7 * 24 * 3600],
  ["day", 24 * 3600],
  ["hour", 3600],
  ["minute", 60],
];

/** "3 hours ago", "yesterday", "just now". */
export function relative(value: string | null | undefined, now = Date.now()) {
  if (!value) return m.format_never();
  const seconds = (Date.parse(value) - now) / 1000;
  if (Math.abs(seconds) < 45) return m.format_just_now();
  const relativeFormat = new Intl.RelativeTimeFormat(getLocale(), { numeric: "auto" });
  for (const [unit, size] of units)
    if (Math.abs(seconds) >= size || unit === "minute") return relativeFormat.format(Math.round(seconds / size), unit);
  return m.format_just_now();
}

export const dateTime = (value: string) =>
  new Date(value).toLocaleString(getLocale(), {
    month: "short",
    day: "numeric",
    year: new Date(value).getFullYear() === new Date().getFullYear() ? undefined : "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });

export const day = (value: string | null | undefined) =>
  value ? new Date(value).toLocaleDateString(getLocale(), { month: "short", day: "numeric", year: "numeric" }) : m.format_unknown();

/** Days between two dates, rounded; undefined if either is unknown. */
export function daysBetween(from?: string | null, to?: string | null) {
  if (!from || !to) return undefined;
  return Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000);
}

export function duration(operation: Operation, now = Date.now()) {
  const end = operation.finishedAt ? Date.parse(operation.finishedAt) : now;
  return formatSeconds(Math.max(0, (end - Date.parse(operation.startedAt)) / 1000));
}

const number = (value: number, digits = 0) =>
  value.toLocaleString(getLocale(), { minimumFractionDigits: digits, maximumFractionDigits: digits });

export function formatSeconds(seconds: number) {
  if (seconds < 10) return m.format_seconds({ seconds: number(seconds, 1) });
  if (seconds < 60) return m.format_seconds({ seconds: number(Math.round(seconds)) });
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return m.format_minutes_seconds({ minutes, seconds: Math.floor(seconds % 60) });
  return m.format_hours_minutes({ hours: Math.floor(minutes / 60), minutes: minutes % 60 });
}

/** Sizes in binary units. */
export function bytes(value: number | bigint | undefined) {
  if (value === undefined) return "—";
  let size = Math.abs(Number(value));
  const scale = ["B", "KiB", "MiB", "GiB", "TiB"];
  let index = 0;
  while (size >= 1024 && index < scale.length - 1) {
    size /= 1024;
    index++;
  }
  const digits = size >= 100 || index === 0 ? 0 : 1;
  return `${size.toLocaleString(getLocale(), { minimumFractionDigits: digits, maximumFractionDigits: digits, useGrouping: false })} ${scale[index]}`;
}

export const scopeLabel = (scope: string) =>
  scope === "all" ? m.scope_all() : scope === "home" ? m.scope_home() : m.scope_system();

/** The scope inside a sentence: "your user", "system". */
export const scopeText = (scope: Scope | string) =>
  scope === "all" ? m.scope_all_text() : scope === "home" ? m.scope_home_text() : m.scope_system_text();

export const shortRev = (rev?: string | null) => (rev ? rev.slice(0, 7) : "—");

const roughly = (seconds: number) => {
  const minutes = Math.round(seconds / 60);
  if (minutes < 1) return m.format_less_than_a_minute();
  if (minutes < 60) return m.format_about_minutes({ count: minutes });
  // A decimal input takes exact decimal text.
  return m.format_about_hours({ count: String(Math.round(minutes / 6) / 10) });
};

/** "About 3 minutes left" for a running operation, judged from earlier runs of the same kind. */
export function remaining(operations: readonly Operation[], operation: Operation, now = Date.now()) {
  const usual = usualSeconds(operations, operation.request);
  if (usual === undefined) return undefined;
  const elapsed = (now - Date.parse(operation.startedAt)) / 1000;
  if (elapsed > usual * 1.2 + 30) return m.format_longer_than_usual();
  const text = m.format_time_left({ time: roughly(Math.max(0, usual - elapsed)) });
  return text[0].toUpperCase() + text.slice(1);
}
