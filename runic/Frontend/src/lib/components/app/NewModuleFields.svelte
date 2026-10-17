<script lang="ts">
  import { m } from "$lib/i18n";
  import { app } from "$lib/app.svelte";
  import { software, type Target } from "$lib/software.svelte";

  let { scope, file = $bindable(), importer = $bindable() }: { scope: Target; file: string; importer: string } = $props();

  // The importer follows the path until it is chosen by hand.
  const uid = $props.id();
  let chosen = $state(false);
  const importers = $derived(software.importers(scope, file));
  const problem = $derived(software.newFileProblem(scope, file));
  $effect(() => {
    if (!chosen || !importers.includes(importer)) importer = importers[0] ?? "";
  });
</script>

<div class="space-y-2 rounded-lg border border-dashed p-3">
  <div class="space-y-1">
    <label class="block space-y-1">
      <span class="text-xs font-medium">{m.newfile_new_file()}</span>
      <input class="focus-ring h-8 w-full rounded-md border bg-card px-2 font-mono text-xs" bind:value={file} spellcheck="false" aria-invalid={Boolean(problem)} aria-describedby={problem ? `${uid}-problem` : undefined} />
    </label>
    {#if problem}<span id="{uid}-problem" class="block text-xs text-destructive">{problem}</span>{/if}
  </div>
  <label class="block space-y-1">
    <span class="text-xs font-medium">{m.newfile_imported_from()}</span>
    {#if importers.length}
      <select class="focus-ring h-8 w-full rounded-md border bg-card px-2 font-mono text-xs" bind:value={importer} onchange={() => (chosen = true)}>
        {#each importers as item (item)}<option value={item}>{item}</option>{/each}
      </select>
    {:else}
      <span class="block text-xs text-destructive">{m.newfile_no_importer({ host: app.host })}</span>
    {/if}
  </label>
  <p class="text-xs text-muted-foreground">{m.newfile_starts_empty()}</p>
</div>
