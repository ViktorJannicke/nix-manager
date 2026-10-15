<script lang="ts">
  import type { BuildProgress } from "../../../generated/types.js";
  import { bytes } from "$lib/format";
  import { m } from "$lib/i18n";
  import ProgressBar from "./ProgressBar.svelte";

  let { progress, compact = false }: { progress?: BuildProgress | null; compact?: boolean } = $props();

  // Counts are C# longs, which arrive as bigint.
  const counts = $derived(
    progress && {
      builds: { done: Number(progress.builds.done), expected: Number(progress.builds.expected), failed: Number(progress.builds.failed) },
      downloads: { done: Number(progress.downloads.done), expected: Number(progress.downloads.expected) },
      bytes: { done: progress.bytes.done, expected: progress.bytes.expected },
    },
  );
  const fraction = $derived.by(() => {
    if (!progress || !counts || progress.evaluating) return undefined;
    const total = counts.builds.expected + counts.downloads.expected;
    if (!total) return undefined;
    return (counts.builds.done + counts.downloads.done) / total;
  });
  const summary = $derived.by(() => {
    if (!progress || !counts) return m.progress_starting();
    if (progress.evaluating) return m.progress_evaluating();
    const parts: string[] = [];
    if (counts.builds.expected) parts.push(m.progress_built({ done: counts.builds.done, count: counts.builds.expected }));
    if (counts.downloads.expected) parts.push(m.progress_downloaded({ done: counts.downloads.done, expected: counts.downloads.expected }));
    if (counts.bytes.expected) parts.push(m.progress_bytes({ done: bytes(counts.bytes.done), expected: bytes(counts.bytes.expected) }));
    if (counts.builds.failed) parts.push(m.progress_failed({ count: counts.builds.failed }));
    const text = parts.length ? parts.join(", ") : m.progress_nothing_to_build();
    return text[0].toUpperCase() + text.slice(1);
  });
</script>

<div class="space-y-1.5">
  <ProgressBar value={fraction} label={m.progress_label()} />
  <div class="flex items-baseline justify-between gap-3 text-xs text-muted-foreground">
    <span class="truncate" title={summary}>{summary}</span>
    {#if fraction !== undefined}<span class="tabular-nums">{Math.round(fraction * 100)}%</span>{/if}
  </div>
  {#if !compact && progress?.current.length}
    <ul role="list" class="space-y-0.5 font-mono text-[0.7rem] text-muted-foreground">
      {#each progress.current.slice(0, 4) as name (name)}
        <li class="truncate">› {name}</li>
      {/each}
      {#if progress.current.length > 4}<li>{m.common_and_more({ count: progress.current.length - 4 })}</li>{/if}
    </ul>
  {/if}
</div>
