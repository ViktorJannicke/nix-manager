<script lang="ts">
  import XIcon from "@lucide/svelte/icons/x";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import ChevronRightIcon from "@lucide/svelte/icons/chevron-right";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { describeChange, software } from "$lib/software.svelte";
  import { plural } from "$lib/format";
  import DiffView from "./DiffView.svelte";

  let showFiles = $state(false);
  const draft = $derived(software.draft);
  const problems = $derived(new Map((draft?.problems ?? []).map((problem) => [problem.index, problem.message])));
  const preparing = $derived(app.active?.request.action === "review-build" && app.active.request.edits);
</script>

{#if draft?.changes.length}
  <section class="prepared p-4" aria-labelledby="pending-title">
    <div class="flex flex-wrap items-start gap-3">
      <div class="min-w-0 flex-1">
        <h2 id="pending-title" class="font-semibold">{plural(draft.changes.length, "pending edit")}</h2>
        <p class="text-xs text-muted-foreground">
          Not written yet. Prepare builds them from a copy and shows what they change; your files change when you apply.
        </p>
      </div>
      <div class="flex gap-2">
        <Button variant="ghost" size="sm" disabled={software.saving} onclick={() => software.clear()}>Clear</Button>
        <Button size="sm" disabled={app.busy || !app.connected || problems.size === draft.changes.length} onclick={() => software.prepare()}>
          {preparing ? "Preparing…" : "Prepare"}
        </Button>
      </div>
    </div>
    <ul class="mt-3 divide-y divide-prepared/30">
      {#each draft.changes as change, index (index)}
        {@const text = describeChange(change)}
        <li class="flex items-start gap-2 py-1.5">
          <div class="min-w-0 flex-1">
            <p class="font-medium break-words">{text.title}</p>
            <p class="font-mono text-[0.72rem] break-all text-muted-foreground">{text.detail}</p>
            {#if problems.has(index)}
              <p class="mt-0.5 flex gap-1.5 text-xs text-destructive"><TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0" />{problems.get(index)}</p>
            {/if}
          </div>
          <button
            class="focus-ring grid size-6 shrink-0 place-items-center rounded text-muted-foreground hover:bg-card hover:text-foreground"
            aria-label="Remove this edit"
            disabled={software.saving}
            onclick={() => software.unstage(index)}
          ><XIcon class="size-3.5" /></button>
        </li>
      {/each}
    </ul>
    {#if draft.files.length}
      <button
        class="focus-ring mt-2 flex items-center gap-1 rounded text-xs font-medium text-prepared-foreground hover:underline"
        aria-expanded={showFiles}
        onclick={() => (showFiles = !showFiles)}
      >
        <ChevronRightIcon class="size-3.5 transition-transform {showFiles ? 'rotate-90' : ''}" />
        {showFiles ? "Hide" : "Show"} the edited lines in {plural(draft.files.length, "file")}
      </button>
      {#if showFiles}
        <div class="mt-2 space-y-2">
          {#each draft.files as file (file.path)}<DiffView {file} />{/each}
        </div>
      {/if}
    {/if}
  </section>
{/if}
