import type { BuildReview, EditEffect, Generation, Operation, PackageChange } from "./contracts";
import type { Request } from "./domain";
import { getLocale, list, t, tn } from "./i18n";

/** Sizes in binary units, optionally with a sign. */
export function bytes(value: number | undefined, signed = false) {
  if (value === undefined || Number.isNaN(value)) return "—";
  const sign = signed ? (value > 0 ? "+" : value < 0 ? "−" : "±") : value < 0 ? "−" : "";
  let size = Math.abs(value);
  const scale = ["B", "KiB", "MiB", "GiB", "TiB"];
  let index = 0;
  while (size >= 1024 && index < scale.length - 1) {
    size /= 1024;
    index++;
  }
  const digits = size >= 100 || index === 0 ? 0 : 1;
  return `${sign}${size.toLocaleString(getLocale(), { minimumFractionDigits: digits, maximumFractionDigits: digits, useGrouping: false })} ${scale[index]}`;
}

export function summarize(changes: PackageChange[]) {
  return {
    changed: changes.filter((c) => c.kind === "changed").length,
    added: changes.filter((c) => c.kind === "added").length,
    removed: changes.filter((c) => c.kind === "removed").length,
    rebuilt: changes.filter((c) => c.kind === "rebuilt").length,
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
const names = (items: string[]) => {
  const unique = [...new Set(items)];
  return unique.length <= 3 ? list(unique) : list([...unique.slice(0, 2), t("{count} more", { count: unique.length - 2 })]);
};
const capitalize = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);

/** What applying a review does, in a few plain sentences. */
export function reviewSummary(review: BuildReview) {
  const outputs = [review.home, review.system].filter((output) => output !== undefined);
  const parts: string[] = [];
  if (review.edits) {
    const packages = review.edits.effects.map(editedPackages);
    const added = packages.flatMap((effect) => effect.added.map((item) => item.name));
    const removed = packages.flatMap((effect) => effect.removed.map((item) => item.name));
    const settings = review.edits.effects.reduce((count, effect) => count + effect.options.length, 0);
    if (added.length) parts.push(t("installs {names}", { names: names(added) }));
    if (removed.length) parts.push(t("removes {names}", { names: names(removed) }));
    if (settings) parts.push(tn("changes {count} setting", "changes {count} settings", settings));
  } else if (outputs.some((output) => output.compared)) {
    const counts = summarize(outputs.filter((output) => output.compared).flatMap((output) => output.changes));
    // Only the first count names what is counted: "updates 12 packages, adds 2 and removes 1".
    if (counts.changed) parts.push(tn("updates {count} package", "updates {count} packages", counts.changed));
    if (counts.added)
      parts.push(parts.length ? t("adds {count}", { count: counts.added }) : tn("adds {count} package", "adds {count} packages", counts.added));
    if (counts.removed)
      parts.push(parts.length ? t("removes {count}", { count: counts.removed }) : tn("removes {count} package", "removes {count} packages", counts.removed));
    // A part with nothing active yet, such as a user's first home generation.
    if (review.home && !review.home.compared) parts.push(t("sets up your user's apps and settings for the first time"));
    if (review.system && !review.system.compared) parts.push(t("builds the system new, as no system is active to compare with"));
  } else parts.push(t("builds everything new, as nothing is active to compare with"));
  const sentences = [parts.length ? `${capitalize(list(parts))}.` : t("Nothing you use changes.")];
  if (review.inputs?.changes.length)
    sentences.unshift(tn("Updates {count} software source.", "Updates {count} software sources.", review.inputs.changes.length));
  const sizes = outputs.filter((output) => output.closureSize !== undefined && output.currentClosureSize !== undefined);
  const delta = sizes.reduce((total, output) => total + output.closureSize! - output.currentClosureSize!, 0);
  if (sizes.length && Math.abs(delta) >= 1024 * 1024)
    sentences.push(
      delta > 0 ? t("It needs about {size} more disk space.", { size: bytes(delta) }) : t("It frees about {size} of disk space.", { size: bytes(-delta) }),
    );
  if (review.downloaded && review.downloaded >= 1024 * 1024)
    sentences.push(t("{size} were downloaded to prepare it.", { size: bytes(review.downloaded) }));
  const kernel = review.system?.kernel;
  if (kernel?.after && kernel.before !== kernel.after) sentences.push(t("Restart afterwards to use the new kernel."));
  return sentences.join(" ");
}

/** Generations `nix-env --delete-generations +N` removes: all but the newest `retention` and the active one. */
export function removedGenerations(generations: Generation[], retention: number) {
  const kept = new Set(
    [...generations]
      .sort((a, b) => b.number - a.number)
      .slice(0, retention)
      .map((generation) => generation.number),
  );
  return generations.filter((generation) => !generation.current && !kept.has(generation.number));
}

const sameKind = (a: Request, b: Request) =>
  a.action === b.action && a.scope === b.scope && a.update === b.update && !!a.edits === !!b.edits && !a.dryRun;

/** Typical seconds for `request`: the median of the last five earlier runs of the same kind, if there were two. */
export function usualSeconds(operations: readonly Operation[], request: Request) {
  const seconds = operations
    .filter((operation) => operation.status === "succeeded" && operation.finishedAt && sameKind(operation.request, request))
    .sort((a, b) => b.startedAt.localeCompare(a.startedAt))
    .slice(0, 5)
    .map((operation) => (Date.parse(operation.finishedAt!) - Date.parse(operation.startedAt)) / 1000)
    .sort((a, b) => a - b);
  if (seconds.length < 2) return undefined;
  const middle = Math.floor(seconds.length / 2);
  return seconds.length % 2 ? seconds[middle] : (seconds[middle - 1] + seconds[middle]) / 2;
}

const roughly = (seconds: number) => {
  const minutes = Math.round(seconds / 60);
  if (minutes < 1) return t("less than a minute");
  if (minutes < 60) return tn("about {count} minute", "about {count} minutes", minutes);
  return tn("about {count} hour", "about {count} hours", Math.round(minutes / 6) / 10);
};

/** A plain estimate of the time left, or undefined without earlier runs. */
export function timeLeft(usual: number | undefined, elapsed: number) {
  if (usual === undefined) return undefined;
  if (elapsed > usual * 1.2 + 30) return t("Taking longer than usual");
  return capitalize(t("{time} left", { time: roughly(Math.max(0, usual - elapsed)) }));
}
