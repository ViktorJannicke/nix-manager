<script lang="ts">
  import { app } from "$lib/app.svelte";

  // The name Nix and its documentation use, shown beside the plain one in detailed mode.
  let { name }: { name: string } = $props();
</script>

{#if app.detail === "detailed"}<span class="ml-1.5 font-mono text-xs font-normal text-muted-foreground">{name}</span>{/if}
