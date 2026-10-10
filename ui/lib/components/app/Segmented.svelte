<script lang="ts" generics="T extends string">
  import { cn } from "$lib/utils";

  let { value = $bindable(), options, label, class: className }: {
    value: T;
    options: { value: T; label: string; count?: number; disabled?: boolean }[];
    label: string;
    class?: string;
  } = $props();
</script>

<div role="radiogroup" aria-label={label} class={cn("inline-flex rounded-lg bg-muted p-0.5", className)}>
  {#each options as option (option.value)}
    <button
      type="button"
      role="radio"
      aria-checked={value === option.value}
      disabled={option.disabled}
      onclick={() => (value = option.value)}
      class="focus-ring inline-flex h-7 items-center gap-1.5 rounded-md px-3 text-xs font-medium text-muted-foreground transition-colors hover:text-foreground disabled:opacity-40 aria-checked:bg-card aria-checked:text-foreground aria-checked:shadow-xs"
    >
      {option.label}
      {#if option.count !== undefined}<span class="tabular-nums opacity-60">{option.count}</span>{/if}
    </button>
  {/each}
</div>
