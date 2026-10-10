<script lang="ts">
  import type { BuildProgress } from "../../../../src/contracts";
  import { bytes, plural } from "$lib/format";
  import ProgressBar from "./ProgressBar.svelte";

  let { progress, compact = false }: { progress?: BuildProgress; compact?: boolean } = $props();

  const fraction = $derived.by(() => {
    if (!progress || progress.evaluating) return undefined;
    const total = progress.builds.expected + progress.downloads.expected;
    if (!total) return undefined;
    return (progress.builds.done + progress.downloads.done) / total;
  });
  const summary = $derived.by(() => {
    if (!progress) return "Starting…";
    if (progress.evaluating) return "Evaluating the configuration…";
    const parts: string[] = [];
    if (progress.builds.expected)
      parts.push(`Built ${progress.builds.done} of ${plural(progress.builds.expected, "package")}`);
    if (progress.downloads.expected)
      parts.push(`downloaded ${progress.downloads.done} of ${progress.downloads.expected}`);
    if (progress.bytes.expected)
      parts.push(`${bytes(progress.bytes.done)} of ${bytes(progress.bytes.expected)}`);
    if (progress.builds.failed) parts.push(`${progress.builds.failed} failed`);
    return parts.length ? parts.join(", ") : "Everything is already built";
  });
</script>

<div class="space-y-1.5">
  <ProgressBar value={fraction} label="Build progress" />
  <div class="flex items-baseline justify-between gap-3 text-xs text-muted-foreground">
    <span class="truncate">{summary[0].toUpperCase() + summary.slice(1)}</span>
    {#if fraction !== undefined}<span class="tabular-nums">{Math.round(fraction * 100)}%</span>{/if}
  </div>
  {#if !compact && progress?.current.length}
    <ul class="space-y-0.5 font-mono text-[0.7rem] text-muted-foreground">
      {#each progress.current.slice(0, 4) as name (name)}
        <li class="truncate">› {name}</li>
      {/each}
      {#if progress.current.length > 4}<li>and {progress.current.length - 4} more</li>{/if}
    </ul>
  {/if}
</div>
