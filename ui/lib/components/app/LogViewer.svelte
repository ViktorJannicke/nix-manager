<script lang="ts">
  import { tick } from "svelte";
  import SearchIcon from "@lucide/svelte/icons/search";
  import WrapTextIcon from "@lucide/svelte/icons/wrap-text";
  import CopyIcon from "@lucide/svelte/icons/copy";
  import DownloadIcon from "@lucide/svelte/icons/download";
  import ArrowDownIcon from "@lucide/svelte/icons/arrow-down";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";

  let { id }: { id: string } = $props();
  let query = $state("");
  let errorsOnly = $state(false);
  let wrap = $state(true);
  let follow = $state(true);
  let viewport = $state<HTMLElement>();

  $effect(() => {
    if (app.log.id !== id) app.selectOperation(id);
  });

  const lines = $derived(app.log.id === id ? app.log.text.split("\n") : []);
  const errorPattern = /\b(error|failed|fatal|cannot|denied)\b/i;
  const filtered = $derived.by(() => {
    const needle = query.trim().toLowerCase();
    if (!needle && !errorsOnly) return undefined;
    return lines
      .map((line, index) => ({ line, index }))
      .filter(({ line }) =>
        (!needle || line.toLowerCase().includes(needle)) && (!errorsOnly || errorPattern.test(line)),
      );
  });

  $effect(() => {
    void app.log.text;
    if (follow && !filtered) void tick().then(() => viewport?.scrollTo({ top: viewport.scrollHeight }));
  });

  function onScroll() {
    if (!viewport) return;
    follow = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 24;
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(app.log.text);
      app.toast({ tone: "info", title: "Log copied" });
    } catch (error) {
      app.fail(error, "Could not copy the log");
    }
  }
  function download() {
    const link = document.createElement("a");
    link.href = URL.createObjectURL(new Blob([app.log.text], { type: "text/plain" }));
    link.download = `nix-manager-${id}.log`;
    link.click();
    URL.revokeObjectURL(link.href);
  }
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden rounded-xl border bg-card">
  <div class="flex flex-wrap items-center gap-2 border-b px-2 py-1.5">
    <label class="flex h-7 min-w-40 flex-1 items-center gap-2 rounded-md bg-muted px-2 text-muted-foreground focus-within:ring-2 focus-within:ring-ring/40">
      <SearchIcon class="size-3.5" />
      <input bind:value={query} aria-label="Filter log" placeholder="Filter log" class="w-full bg-transparent text-xs text-foreground outline-none placeholder:text-muted-foreground" />
    </label>
    <label class="flex items-center gap-1.5 text-xs text-muted-foreground">
      <input type="checkbox" bind:checked={errorsOnly} class="accent-primary" /> Errors only
    </label>
    {#if filtered}<span class="text-xs text-muted-foreground tabular-nums">{filtered.length} matches</span>{/if}
    <div class="ml-auto flex gap-0.5">
      <Button variant="ghost" size="icon-sm" title="Wrap lines" aria-label="Wrap lines" aria-pressed={wrap} onclick={() => (wrap = !wrap)} class={wrap ? "bg-muted" : ""}><WrapTextIcon /></Button>
      <Button variant="ghost" size="icon-sm" title="Copy log" aria-label="Copy log" onclick={copy} disabled={!app.log.text}><CopyIcon /></Button>
      <Button variant="ghost" size="icon-sm" title="Download log" aria-label="Download log" onclick={download} disabled={!app.log.text}><DownloadIcon /></Button>
    </div>
  </div>
  <div class="relative min-h-0 flex-1">
    <div
      bind:this={viewport}
      onscroll={onScroll}
      class="scroll-thin absolute inset-0 overflow-auto bg-muted/30 py-2 font-mono text-[0.72rem] leading-5"
    >
      {#if app.log.truncated}
        <p class="px-3 pb-1 text-muted-foreground italic">Earlier output is omitted here; download the log for the full text.</p>
      {/if}
      {#if app.log.loading && !lines.length}
        <p class="px-3 text-muted-foreground">Loading…</p>
      {:else if !app.log.text}
        <p class="px-3 text-muted-foreground">No output yet.</p>
      {:else}
        <pre class="px-3 {wrap ? 'break-all whitespace-pre-wrap' : 'whitespace-pre'}">{#if filtered}{#each filtered as item (item.index)}<span class="block {errorPattern.test(item.line) ? 'text-destructive' : ''}"><span class="mr-3 inline-block w-10 text-right text-muted-foreground/60 select-none">{item.index + 1}</span>{item.line}</span>{/each}{:else}{app.log.text}{/if}</pre>
      {/if}
    </div>
    {#if !follow && !filtered}
      <Button size="sm" variant="secondary" class="absolute right-4 bottom-3 shadow-md" onclick={() => { follow = true; viewport?.scrollTo({ top: viewport.scrollHeight }); }}>
        <ArrowDownIcon /> Follow output
      </Button>
    {/if}
  </div>
</div>
