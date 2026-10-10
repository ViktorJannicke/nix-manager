<script lang="ts">
  import { app } from "$lib/app.svelte";
  import { software } from "$lib/software.svelte";
  import type { Target } from "../../../../src/software";

  let { scope, file = $bindable(), importer = $bindable() }: { scope: Target; file: string; importer: string } = $props();

  // The importer follows the path until it is chosen by hand.
  let chosen = $state(false);
  const importers = $derived(software.importers(scope, file));
  const problem = $derived(software.newFileProblem(scope, file));
  $effect(() => {
    if (!chosen || !importers.includes(importer)) importer = importers[0] ?? "";
  });
</script>

<div class="space-y-2 rounded-lg border border-dashed p-3">
  <label class="block space-y-1">
    <span class="text-xs font-medium">New file</span>
    <input class="focus-ring h-8 w-full rounded-md border bg-card px-2 font-mono text-xs" bind:value={file} spellcheck="false" aria-invalid={Boolean(problem)} />
    {#if problem}<span class="block text-xs text-destructive">{problem}</span>{/if}
  </label>
  <label class="block space-y-1">
    <span class="text-xs font-medium">Imported from</span>
    {#if importers.length}
      <select class="focus-ring h-8 w-full rounded-md border bg-card px-2 font-mono text-xs" bind:value={importer} onchange={() => (chosen = true)}>
        {#each importers as item (item)}<option value={item}>{item}</option>{/each}
      </select>
    {:else}
      <span class="block text-xs text-destructive">No file {app.host} imports has an imports list Nix Manager can add to.</span>
    {/if}
  </label>
  <p class="text-xs text-muted-foreground">The file starts as an empty module; its path is added to the imports of the file above.</p>
</div>
