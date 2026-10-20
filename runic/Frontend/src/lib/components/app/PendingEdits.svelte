<script lang="ts">
  import XIcon from "@lucide/svelte/icons/x";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import ChevronRightIcon from "@lucide/svelte/icons/chevron-right";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { describeChange } from "$lib/changes";
  import { software } from "$lib/software.svelte";
  import { m, text } from "$lib/i18n";
  import DiffView from "./DiffView.svelte";
  import PendingButton from "./PendingButton.svelte";

  let showFiles = $state(false);
  const draft = $derived(software.draft);
  const problems = $derived(new Map((draft?.problems ?? []).map((problem) => [problem.index, problem.message])));
  const simple = $derived(app.detail === "simple");
</script>

{#if draft?.changes.length}
  <section class="prepared p-4" aria-labelledby="pending-title">
    <div class="flex flex-wrap items-start gap-3">
      <div class="min-w-0 flex-1">
        <h2 id="pending-title" class="font-semibold">{m.pending_count({ count: draft.changes.length })}</h2>
        <p class="text-xs text-muted-foreground">{simple ? m.pending_nothing_changes() : m.pending_not_written()}</p>
      </div>
      <div class="flex gap-2">
        <Button variant="ghost" size="sm" disabled={software.saving} onclick={() => software.clear()}>{m.pending_clear()}</Button>
        <PendingButton />
      </div>
    </div>
    <ul role="list" aria-labelledby="pending-title" class="mt-3 divide-y divide-prepared/30">
      {#each draft.changes as change, index (index)}
        {@const described = describeChange(change)}
        <li class="flex items-start gap-2 py-1.5">
          <div class="min-w-0 flex-1">
            <p class="font-medium break-words">{described.title}</p>
            {#if !simple}<p class="font-mono text-[0.72rem] break-all text-muted-foreground">{described.detail}</p>{/if}
            {#if problems.has(index)}
              <p class="mt-0.5 flex gap-1.5 text-xs text-destructive"><TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0" />{text(problems.get(index))}</p>
            {/if}
          </div>
          <button
            class="focus-ring grid size-6 shrink-0 place-items-center rounded text-muted-foreground hover:bg-card hover:text-foreground"
            aria-label={m.pending_remove({ name: described.title })}
            disabled={software.saving}
            onclick={() => software.unstage(index)}
          ><XIcon class="size-3.5" /></button>
        </li>
      {/each}
    </ul>
    {#if draft.files.length && !simple}
      <button
        class="focus-ring mt-2 flex items-center gap-1 rounded text-xs font-medium text-prepared-foreground hover:underline"
        aria-expanded={showFiles}
        onclick={() => (showFiles = !showFiles)}
      >
        <ChevronRightIcon class="size-3.5 transition-transform {showFiles ? 'rotate-90' : ''}" />
        {showFiles ? m.pending_hide_lines({ count: draft.files.length }) : m.pending_show_lines({ count: draft.files.length })}
      </button>
      {#if showFiles}
        <div class="mt-2 space-y-2">
          {#each draft.files as file (file.path)}<DiffView {file} />{/each}
        </div>
      {/if}
    {/if}
  </section>
{/if}
