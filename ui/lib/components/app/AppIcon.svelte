<script lang="ts" module>
  import { imageUrl } from "$lib/api";

  // Icons stay loaded for the session; the server keeps them on disk.
  const icons = new Map<string, Promise<string | undefined>>();
  const load = (id: string) => {
    let icon = icons.get(id);
    if (!icon) {
      icon = imageUrl(`app-icon/${encodeURIComponent(id)}`).catch(() => undefined);
      icons.set(id, icon);
    }
    return icon;
  };
</script>

<script lang="ts">
  import type { AppInfo } from "../../../../src/apps";

  let { app, name, class: className = "size-10" }: { app?: AppInfo; name: string; class?: string } = $props();
  let url = $state<string>();

  $effect(() => {
    url = undefined;
    const id = app?.icon ? app.id : undefined;
    if (!id) return;
    let current = true;
    void load(id).then((value) => current && (url = value));
    return () => {
      current = false;
    };
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
