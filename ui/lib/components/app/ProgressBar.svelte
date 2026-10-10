<script lang="ts">
  import { cn } from "$lib/utils";

  /** `value` between 0 and 1; undefined shows an indeterminate bar. */
  let { value, class: className, label }: { value?: number; class?: string; label?: string } = $props();
</script>

<div
  role="progressbar"
  aria-label={label}
  aria-valuemin={0}
  aria-valuemax={100}
  aria-valuenow={value === undefined ? undefined : Math.round(value * 100)}
  class={cn("relative h-1.5 w-full overflow-hidden rounded-full bg-muted", className)}
>
  {#if value === undefined}
    <div class="indeterminate absolute inset-y-0 w-1/3 rounded-full bg-primary/70"></div>
  {:else}
    <div
      class="h-full rounded-full bg-primary transition-[width] duration-300"
      style:width={`${Math.max(2, Math.min(100, value * 100))}%`}
    ></div>
  {/if}
</div>

<style>
  .indeterminate {
    animation: slide 1.4s ease-in-out infinite;
  }
  @keyframes slide {
    from { left: -33%; }
    to { left: 100%; }
  }
</style>
