<script lang="ts">
  import type { Operation } from "../../../generated/types.js";
  import { formatSeconds } from "$lib/format";
  import { app } from "$lib/app.svelte";
  import { text } from "$lib/i18n";
  import StatusBadge from "./StatusBadge.svelte";
  import BuildProgress from "./BuildProgress.svelte";

  let { operation }: { operation: Operation } = $props();
  const elapsed = (start?: string | null, end?: string | null) =>
    start ? formatSeconds(((end ? Date.parse(end) : app.now) - Date.parse(start)) / 1000) : "";
</script>

{#if operation.steps?.length}
  <ol role="list" class="space-y-1">
    {#each operation.steps as step, index (step.id)}
      <li class="rounded-lg px-3 py-2 {step.status === 'running' ? 'bg-primary/5' : ''}">
        <div class="flex items-center gap-3">
          <span class="w-5 text-right text-xs text-muted-foreground tabular-nums">{index + 1}</span>
          <span class="min-w-0 flex-1 truncate {step.status === 'pending' || step.status === 'skipped' ? 'text-muted-foreground' : ''}">
            {text(step.label)}
          </span>
          <span class="text-xs text-muted-foreground tabular-nums">{elapsed(step.startedAt, step.finishedAt)}</span>
          <StatusBadge status={step.status} />
        </div>
        <!-- A failed build keeps its progress to show how far it got. -->
        {#if step.status === "running" || (step.status === "failed" && step.progress && !step.progress.evaluating)}
          <div class="mt-2 pl-8"><BuildProgress progress={step.progress} /></div>
        {/if}
      </li>
    {/each}
  </ol>
{:else}
  <p class="px-3 text-muted-foreground">{text(operation.step)}</p>
{/if}
