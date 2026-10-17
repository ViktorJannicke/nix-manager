<script lang="ts" module>
  // The Window serves an app's icon beside the page (AppIconResources); one it cannot serve, such as an app without
  // an icon or with app details turned off, shows the app's initial for the rest of the session.
  const missing = $state<Record<string, true>>({});
</script>

<script lang="ts">
  import type { AppInfo } from "../../../generated/types.js";

  let { app: info, name, class: className = "size-10" }: { app?: AppInfo | null; name: string; class?: string } = $props();
  const id = $derived(info?.icon ? info.id : undefined);
</script>

<!-- Decorative: the name always follows it. -->
{#if id && !missing[id]}
  <img src={`app-icon/${encodeURIComponent(id)}`} alt="" class="{className} shrink-0 object-contain" onerror={() => (missing[id] = true)} />
{:else}
  <span aria-hidden="true" class="{className} grid shrink-0 place-items-center rounded-[22%] bg-muted text-sm font-semibold text-muted-foreground uppercase">
    {name.slice(0, 1)}
  </span>
{/if}
