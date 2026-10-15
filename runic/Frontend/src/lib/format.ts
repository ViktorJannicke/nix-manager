import type { BuildReview, EditEffect, Generation, Operation, PackageChange } from "../generated/types.js";
import { getLocale, list, m } from "./i18n";
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

/** Sizes in binary units; `signed` shows the direction of a change, such as "+1.2 MiB". */
export function signedBytes(value: number | bigint | undefined) {
  if (value === undefined) return "—";
  const number = Number(value);
  return `${number > 0 ? "+" : number < 0 ? "−" : "±"}${bytes(Math.abs(number))}`;
}

/** Versions of a package as a list; an empty version is shown as such. */
export const versions = (list: readonly string[]) =>
  list.length ? list.map((version) => version || m.format_unversioned()).join(", ") : "—";

export function summarize(changes: readonly PackageChange[]) {
  return {
    changed: changes.filter((change) => change.kind === "changed").length,
    added: changes.filter((change) => change.kind === "added").length,
    removed: changes.filter((change) => change.kind === "removed").length,
    rebuilt: changes.filter((change) => change.kind === "rebuilt").length,
  };
}

/** Packages only rebuilt by an edit (same name on both sides) are listed once, as updated. */
export function editedPackages(effect: EditEffect) {
  const removed = new Set(effect.removed.map((item) => item.name));
  const added = new Set(effect.added.map((item) => item.name));
  return {
    added: effect.added.filter((item) => !removed.has(item.name)),
    removed: effect.removed.filter((item) => !added.has(item.name)),
    updated: effect.added.filter((item) => removed.has(item.name)),
  };
}

/** Up to three names, then how many more. */
const names = (items: readonly string[]) => {
  const unique = [...new Set(items)];
  return unique.length <= 3 ? list(unique) : list([...unique.slice(0, 2), m.summary_more({ count: unique.length - 2 })]);
};
const capitalize = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);
const MiB = 1024 * 1024;

/** What applying a review does, in a few plain sentences (`reviewSummary` in `src/summary.ts`). */
export function reviewSummary(review: BuildReview) {
  const outputs = [review.home, review.system].filter((output) => output !== null);
  const parts: string[] = [];
  if (review.edits) {
    const packages = review.edits.effects.map(editedPackages);
    const added = packages.flatMap((effect) => effect.added.map((item) => item.name));
    const removed = packages.flatMap((effect) => effect.removed.map((item) => item.name));
    const settings = review.edits.effects.reduce((count, effect) => count + effect.options.length, 0);
    if (added.length) parts.push(m.summary_installs({ names: names(added) }));
    if (removed.length) parts.push(m.summary_removes({ names: names(removed) }));
    if (settings) parts.push(m.summary_changes_settings({ count: settings }));
  } else if (outputs.some((output) => output.compared)) {
    const counts = summarize(outputs.filter((output) => output.compared).flatMap((output) => output.changes));
    // Only the first count names what is counted: "updates 12 packages, adds 2 and removes 1".
    if (counts.changed) parts.push(m.summary_updates_packages({ count: counts.changed }));
    if (counts.added) parts.push(parts.length ? m.summary_adds({ count: counts.added }) : m.summary_adds_packages({ count: counts.added }));
    if (counts.removed)
      parts.push(parts.length ? m.summary_removes_count({ count: counts.removed }) : m.summary_removes_packages({ count: counts.removed }));
    // A part with nothing active yet, such as a user's first home generation.
    if (review.home && !review.home.compared) parts.push(m.summary_first_home());
    if (review.system && !review.system.compared) parts.push(m.summary_first_system());
  } else parts.push(m.summary_everything_new());
  const sentences = [parts.length ? `${capitalize(list(parts))}.` : m.summary_nothing_changes()];
  if (review.inputs?.changes.length) sentences.unshift(m.summary_updates_sources({ count: review.inputs.changes.length }));
  const sizes = outputs.filter((output) => output.closureSize !== null && output.currentClosureSize !== null);
  const delta = sizes.reduce((total, output) => total + Number(output.closureSize!) - Number(output.currentClosureSize!), 0);
  if (sizes.length && Math.abs(delta) >= MiB)
    sentences.push(delta > 0 ? m.summary_needs_space({ size: bytes(delta) }) : m.summary_frees_space({ size: bytes(-delta) }));
  if (review.downloaded !== null && Number(review.downloaded) >= MiB)
    sentences.push(m.summary_downloaded({ size: bytes(review.downloaded) }));
  const kernel = review.system?.kernel;
  if (kernel?.after && kernel.before !== kernel.after) sentences.push(m.summary_restart_for_kernel());
  return sentences.join(" ");
}

/** Generations `nix-env --delete-generations +N` removes: all but the newest `retention` and the active one. */
export function removedGenerations(generations: readonly Generation[], retention: number) {
  const kept = new Set(
    [...generations]
      .sort((a, b) => b.number - a.number)
      .slice(0, retention)
      .map((generation) => generation.number),
  );
  return generations.filter((generation) => !generation.current && !kept.has(generation.number));
}
