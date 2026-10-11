import type { BuildReview, EditEffect, Generation, Operation, PackageChange } from "./contracts";
import type { Request } from "./domain";

/** "1 change", "2 changes". */
export const plural = (count: number, one: string, many = `${one}s`) =>
  `${count} ${count === 1 ? one : many}`;

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
  return `${sign}${size.toFixed(size >= 100 || index === 0 ? 0 : 1)} ${scale[index]}`;
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

/** "a", "a and b", "a, b and c". */
const and = (items: string[]) => (items.length < 2 ? items.join("") : `${items.slice(0, -1).join(", ")} and ${items.at(-1)}`);
/** Up to three names, then how many more. */
const names = (list: string[]) => {
  const unique = [...new Set(list)];
  return unique.length <= 3 ? and(unique) : and([...unique.slice(0, 2), `${unique.length - 2} more`]);
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
    if (added.length) parts.push(`installs ${names(added)}`);
    if (removed.length) parts.push(`removes ${names(removed)}`);
    if (settings) parts.push(`changes ${plural(settings, "setting")}`);
  } else if (outputs.every((output) => output.compared)) {
    const counts = summarize(outputs.flatMap((output) => output.changes));
    // Only the first count names what is counted: "updates 12 packages, adds 2 and removes 1".
    const clauses = ([["updates", counts.changed], ["adds", counts.added], ["removes", counts.removed]] as const).filter(([, count]) => count);
    parts.push(...clauses.map(([verb, count], index) => `${verb} ${index ? count : plural(count, "package")}`));
  } else parts.push("builds everything new, as nothing is active to compare with");
  const sentences = [parts.length ? `${capitalize(and(parts))}.` : "Nothing you use changes."];
  if (review.inputs?.changes.length) sentences.unshift(`Updates ${plural(review.inputs.changes.length, "software source")}.`);
  const sizes = outputs.filter((output) => output.closureSize !== undefined && output.currentClosureSize !== undefined);
  const delta = sizes.reduce((total, output) => total + output.closureSize! - output.currentClosureSize!, 0);
  if (sizes.length && Math.abs(delta) >= 1024 * 1024)
    sentences.push(delta > 0 ? `It needs about ${bytes(delta)} more disk space.` : `It frees about ${bytes(-delta)} of disk space.`);
  if (review.downloaded && review.downloaded >= 1024 * 1024) sentences.push(`${bytes(review.downloaded)} were downloaded to prepare it.`);
  const kernel = review.system?.kernel;
  if (kernel?.after && kernel.before !== kernel.after) sentences.push("Restart afterwards to use the new kernel.");
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
  if (minutes < 1) return "less than a minute";
  if (minutes < 60) return `about ${plural(minutes, "minute")}`;
  return `about ${plural(Math.round(minutes / 6) / 10, "hour")}`;
};

/** A plain estimate of the time left, or undefined without earlier runs. */
export function timeLeft(usual: number | undefined, elapsed: number) {
  if (usual === undefined) return undefined;
  if (elapsed > usual * 1.2 + 30) return "Taking longer than usual";
  return `${roughly(Math.max(0, usual - elapsed))} left`.replace(/^./, (c) => c.toUpperCase());
}
