import type { Action } from "svelte/action";

const directions = new Set(["ArrowDown", "ArrowUp", "ArrowLeft", "ArrowRight", "Home", "End"]);

/**
 * The item in a direction from `from`, by position on screen, so lists and
 * grids split into sections both move the way the arrow points.
 */
function next(items: HTMLElement[], from: HTMLElement, key: string) {
  if (key === "Home") return items[0];
  if (key === "End") return items.at(-1);
  const a = from.getBoundingClientRect();
  let best: HTMLElement | undefined;
  let distance = Infinity;
  for (const item of items) {
    if (item === from) continue;
    const b = item.getBoundingClientRect();
    const dx = b.left + b.width / 2 - (a.left + a.width / 2);
    const dy = b.top + b.height / 2 - (a.top + a.height / 2);
    const vertical = key === "ArrowDown" || key === "ArrowUp";
    const ahead = { ArrowDown: dy > 1, ArrowUp: dy < -1, ArrowRight: dx > 1, ArrowLeft: dx < -1 }[key];
    if (!ahead || (!vertical && Math.abs(dy) > a.height / 2)) continue;
    const score = vertical ? Math.abs(dy) + Math.abs(dx) * 2 : Math.abs(dx);
    if (score < distance) [best, distance] = [item, score];
  }
  return best;
}

/**
 * Moves focus between the `[data-nav]` items inside a node with the arrow
 * keys, Home and End. Leaving upwards from the first row focuses `before`.
 */
export const arrows: Action<HTMLElement, { before?: () => HTMLElement | null | undefined } | undefined> = (node, options) => {
  let before = options?.before;
  const keydown = (event: KeyboardEvent) => {
    if (!directions.has(event.key) || event.altKey || event.ctrlKey || event.metaKey) return;
    const items = [...node.querySelectorAll<HTMLElement>("[data-nav]")];
    const from = items.find((item) => item.contains(document.activeElement));
    if (!from) return;
    const target = next(items, from, event.key) ?? (event.key === "ArrowUp" ? before?.() : undefined);
    if (!target) return;
    event.preventDefault();
    target.focus();
    target.scrollIntoView({ block: "nearest" });
  };
  node.addEventListener("keydown", keydown);
  return {
    update: (value) => (before = value?.before),
    destroy: () => node.removeEventListener("keydown", keydown),
  };
};

/** Whether a key press belongs to a text field or an open dialog rather than the page. */
export const typing = (event: KeyboardEvent) => {
  const target = event.target as HTMLElement | null;
  return (
    !!target?.closest("input, textarea, select, [contenteditable=true], [role=dialog]") ||
    event.ctrlKey ||
    event.metaKey ||
    event.altKey
  );
};
