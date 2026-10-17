<script lang="ts">
  import { tick } from "svelte";
  import SearchIcon from "@lucide/svelte/icons/search";
  import WrapTextIcon from "@lucide/svelte/icons/wrap-text";
  import CopyIcon from "@lucide/svelte/icons/copy";
  import ArrowDownIcon from "@lucide/svelte/icons/arrow-down";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { m } from "$lib/i18n";

  // The OperationsViewModel keeps the end of the selected operation's log, live while it runs.
  let { id }: { id: string } = $props();
  let query = $state("");
  let errorsOnly = $state(false);
  let wrap = $state(true);
  let follow = $state(true);
  let viewport = $state<HTMLElement>();

  $effect(() => app.selectOperation(id));

  const shown = $derived(app.operations?.selectedOperationId === id);
  const text = $derived(shown ? (app.operations?.log ?? "") : "");
  const lines = $derived(text.split("\n"));
  const errorPattern = /\b(error|failed|fatal|cannot|denied)\b/i;
  const filtered = $derived.by(() => {
    const needle = query.trim().toLowerCase();
    if (!needle && !errorsOnly) return undefined;
    return lines
      .map((line, index) => ({ line, index }))
      .filter(({ line }) => (!needle || line.toLowerCase().includes(needle)) && (!errorsOnly || errorPattern.test(line)));
  });

  $effect(() => {
    void text;
    if (follow && !filtered) void tick().then(() => viewport?.scrollTo({ top: viewport.scrollHeight }));
  });

  function onScroll() {
    if (!viewport) return;
    follow = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 24;
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(text);
      app.toast({ tone: "info", title: m.log_copied() });
    } catch (error) {
      app.fail(error, m.log_copy_failed());
    }
  }
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden rounded-xl border bg-card">
  <div class="flex flex-wrap items-center gap-2 border-b px-2 py-1.5">
    <label class="flex h-7 min-w-40 flex-1 items-center gap-2 rounded-md border border-field bg-muted px-2 text-muted-foreground focus-within:ring-2 focus-within:ring-ring/40">
      <SearchIcon class="size-3.5" />
      <input bind:value={query} aria-label={m.log_filter()} placeholder={m.log_filter()} class="w-full bg-transparent text-xs text-foreground outline-none placeholder:text-muted-foreground" />
    </label>
    <label class="flex items-center gap-1.5 text-xs text-muted-foreground">
      <input type="checkbox" bind:checked={errorsOnly} class="accent-primary" /> {m.log_errors_only()}
    </label>
    <span class="text-xs text-muted-foreground tabular-nums" role="status">{#if filtered}{m.log_matches({ count: filtered.length })}{/if}</span>
    <div class="ml-auto flex gap-0.5">
      <Button variant="ghost" size="icon-sm" title={m.log_wrap()} aria-label={m.log_wrap()} aria-pressed={wrap} onclick={() => (wrap = !wrap)} class={wrap ? "bg-muted" : ""}><WrapTextIcon /></Button>
      <Button variant="ghost" size="icon-sm" title={m.log_copy()} aria-label={m.log_copy()} onclick={copy} disabled={!text}><CopyIcon /></Button>
    </div>
  </div>
  <div class="relative min-h-0 flex-1">
    <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
    <div
      bind:this={viewport}
      onscroll={onScroll}
      tabindex="0"
      role="region"
      aria-label={m.log_output()}
      class="focus-ring scroll-thin absolute inset-0 overflow-auto bg-muted/30 py-2 font-mono text-[0.72rem] leading-5"
    >
      {#if shown && app.operations?.logTruncated}
        <p class="px-3 pb-1 text-muted-foreground italic">{m.log_truncated()}</p>
      {/if}
      {#if !shown}
        <p class="px-3 text-muted-foreground">{m.common_loading()}</p>
      {:else if !text}
        <p class="px-3 text-muted-foreground">{m.log_empty()}</p>
      {:else}
        <pre class="px-3 {wrap ? 'break-all whitespace-pre-wrap' : 'whitespace-pre'}">{#if filtered}{#each filtered as item (item.index)}<span class="block {errorPattern.test(item.line) ? 'text-destructive' : ''}"><span class="mr-3 inline-block w-10 text-right text-muted-foreground/60 select-none">{item.index + 1}</span>{item.line}</span>{/each}{:else}{text}{/if}</pre>
      {/if}
    </div>
    {#if !follow && !filtered}
      <Button size="sm" variant="secondary" class="absolute right-4 bottom-3 shadow-md" onclick={() => { follow = true; viewport?.scrollTo({ top: viewport.scrollHeight }); }}>
        <ArrowDownIcon /> {m.log_follow()}
      </Button>
    {/if}
  </div>
</div>
