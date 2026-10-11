<script lang="ts">
  import type { BuildProgress } from "../../../../src/contracts";
  import { bytes } from "$lib/format";
  import { t, tn } from "$lib/i18n";
  import ProgressBar from "./ProgressBar.svelte";

  let { progress, compact = false }: { progress?: BuildProgress; compact?: boolean } = $props();

  const fraction = $derived.by(() => {
    if (!progress || progress.evaluating) return undefined;
    const total = progress.builds.expected + progress.downloads.expected;
    if (!total) return undefined;
    return (progress.builds.done + progress.downloads.done) / total;
  });
  const summary = $derived.by(() => {
    if (!progress) return t("Starting…");
    if (progress.evaluating) return t("Evaluating the configuration…");
    const parts: string[] = [];
    if (progress.builds.expected)
      parts.push(tn("Built {done} of {count} package", "Built {done} of {count} packages", progress.builds.expected, { done: progress.builds.done }));
    if (progress.downloads.expected)
      parts.push(t("downloaded {done} of {expected}", { done: progress.downloads.done, expected: progress.downloads.expected }));
    if (progress.bytes.expected)
      parts.push(t("{done} of {expected}", { done: bytes(progress.bytes.done), expected: bytes(progress.bytes.expected) }));
    if (progress.builds.failed) parts.push(t("{count} failed", { count: progress.builds.failed }));
    return parts.length ? parts.join(", ") : t("Everything is already built");
  });
</script>

<div class="space-y-1.5">
  <ProgressBar value={fraction} label={t("Build progress")} />
  <div class="flex items-baseline justify-between gap-3 text-xs text-muted-foreground">
    <span class="truncate" title={summary[0].toUpperCase() + summary.slice(1)}>{summary[0].toUpperCase() + summary.slice(1)}</span>
    {#if fraction !== undefined}<span class="tabular-nums">{Math.round(fraction * 100)}%</span>{/if}
  </div>
  {#if !compact && progress?.current.length}
    <ul class="space-y-0.5 font-mono text-[0.7rem] text-muted-foreground">
      {#each progress.current.slice(0, 4) as name (name)}
        <li class="truncate">› {name}</li>
      {/each}
      {#if progress.current.length > 4}<li>{t("and {count} more", { count: progress.current.length - 4 })}</li>{/if}
    </ul>
  {/if}
</div>
