<script lang="ts" generics="T extends string">
  import { cn } from "$lib/utils";

  let { value = $bindable(), options, label, class: className }: {
    value: T;
    options: { value: T; label: string; count?: number; disabled?: boolean }[];
    label: string;
    class?: string;
  } = $props();

  let buttons = $state<HTMLButtonElement[]>([]);

  function move(event: KeyboardEvent, index: number) {
    const last = options.length - 1;
    const target =
      event.key === "ArrowRight" || event.key === "ArrowDown" ? (index === last ? 0 : index + 1)
      : event.key === "ArrowLeft" || event.key === "ArrowUp" ? (index === 0 ? last : index - 1)
      : event.key === "Home" ? 0
      : event.key === "End" ? last
      : undefined;
    if (target === undefined) return;
    event.preventDefault();
    const step = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1;
    let next = target;
    // Skip disabled options, moving in the direction of travel.
    for (let tries = 0; options[next]?.disabled && tries < options.length; tries++) next = (next + step + options.length) % options.length;
    const option = options[next];
    if (!option || option.disabled) return;
    value = option.value;
    buttons[next]?.focus();
  }
</script>

<div role="radiogroup" aria-label={label} class={cn("inline-flex rounded-lg bg-muted p-0.5", className)}>
  {#each options as option, index (option.value)}
    <button
      bind:this={buttons[index]}
      type="button"
      role="radio"
      aria-checked={value === option.value}
      tabindex={value === option.value ? 0 : -1}
      disabled={option.disabled}
      onclick={() => (value = option.value)}
      onkeydown={(event) => move(event, index)}
      class="focus-ring inline-flex h-7 items-center gap-1.5 rounded-md px-3 text-xs font-medium text-muted-foreground transition-colors hover:text-foreground disabled:opacity-40 aria-checked:bg-card aria-checked:text-foreground aria-checked:shadow-xs"
    >
      {option.label}
      {#if option.count !== undefined}<span class="tabular-nums opacity-60">{option.count}</span>{/if}
    </button>
  {/each}
</div>
