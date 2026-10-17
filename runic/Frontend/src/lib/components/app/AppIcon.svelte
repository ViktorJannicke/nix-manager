<script lang="ts" module>
  import { app } from "$lib/app.svelte";

  // The Window's AppIconsViewModel keeps only the latest icons it loaded; the page keeps every one it has seen for
  // the session. Icons asked for in one turn are loaded together.
  const seen = $state<Record<string, string>>({});
  const wanted = new Set<string>();
  let queued = false;

  function want(id: string) {
    if (id in seen) return;
    wanted.add(id);
    if (queued) return;
    queued = true;
    queueMicrotask(() => {
      queued = false;
      const ids = [...wanted];
      wanted.clear();
      const client = app.iconsView?.client;
      // Sixty at a time, as the ViewModel loads them.
      for (let start = 0; start < ids.length; start += 60) void client?.load(ids.slice(start, start + 60)).catch(() => {});
    });
  }

  $effect.root(() => {
    $effect(() => {
      for (const [id, url] of Object.entries(app.iconsView?.state?.icons ?? {})) if (!(id in seen)) seen[id] = url;
    });
  });
</script>

<script lang="ts">
  import type { AppInfo } from "../../../generated/types.js";

  let { app: info, name, class: className = "size-10" }: { app?: AppInfo | null; name: string; class?: string } = $props();
  const id = $derived(info?.icon ? info.id : undefined);
  const url = $derived(id ? seen[id] : undefined);
  $effect(() => {
    if (id && app.iconsView?.client) want(id);
  });
</script>

<!-- Decorative: the name always follows it. -->
{#if url}
  <img src={url} alt="" class="{className} shrink-0 object-contain" />
{:else}
  <span aria-hidden="true" class="{className} grid shrink-0 place-items-center rounded-[22%] bg-muted text-sm font-semibold text-muted-foreground uppercase">
    {name.slice(0, 1)}
  </span>
{/if}
