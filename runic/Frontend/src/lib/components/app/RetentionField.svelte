<script lang="ts">
  import MinusIcon from "@lucide/svelte/icons/minus";
  import PlusIcon from "@lucide/svelte/icons/plus";
  import { Button } from "$lib/components/ui/button";
  import { m } from "$lib/i18n";

  let { value = $bindable(), min = 1, max = 50 }: { value: number; min?: number; max?: number } = $props();
  let spin: HTMLElement | undefined = $state();

  const set = (next: number) => (value = Math.min(max, Math.max(min, next)));

  // The spin button's keys (WAI-ARIA spinbutton pattern).
  function keydown(event: KeyboardEvent) {
    const next = { ArrowUp: value + 1, ArrowDown: value - 1, PageUp: value + 5, PageDown: value - 5, Home: min, End: max }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    set(next);
  }

  // A press of "Keep more" or "Keep fewer", such as from a screen reader's browse mode, leaves focus on the value, so the
  // new number is read out; a mouse press does not move focus away from it.
  function step(by: number) {
    set(value + by);
    spin?.focus();
  }
</script>

<!-- The value is a spin button, the Tab stop of the field, and the buttons beside it are for the pointer (WAI-ARIA
     spinbutton pattern). Screen readers read a spin button's new value as it changes. A live region would not do:
     WebKitGTK 2.54 sends no AT-SPI event when the text of a live region changes, so Orca never hears it
     (README.md, Desktop and screen-reader VM tests). -->
<div class="flex items-center gap-2" role="group" aria-labelledby="retention-label retention-newest">
  <span id="retention-label" class="text-xs text-muted-foreground">{m.restore_keep()}</span>
  <div class="flex items-center rounded-lg border">
    <Button variant="ghost" size="icon-sm" tabindex={-1} aria-label={m.restore_keep_fewer()} disabled={value <= min}
      onmousedown={(event: MouseEvent) => event.preventDefault()} onclick={() => step(-1)}><MinusIcon /></Button>
    <span
      bind:this={spin}
      role="spinbutton"
      tabindex="0"
      aria-labelledby="retention-label retention-newest"
      aria-valuenow={value}
      aria-valuemin={min}
      aria-valuemax={max}
      class="focus-ring w-8 rounded text-center font-medium tabular-nums"
      onkeydown={keydown}>{value}</span
    >
    <Button variant="ghost" size="icon-sm" tabindex={-1} aria-label={m.restore_keep_more()} disabled={value >= max}
      onmousedown={(event: MouseEvent) => event.preventDefault()} onclick={() => step(1)}><PlusIcon /></Button>
  </div>
  <span id="retention-newest" class="text-xs text-muted-foreground">{m.restore_newest()}</span>
</div>
