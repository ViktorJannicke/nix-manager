<script lang="ts">
  import type { Component, Snippet } from "svelte";

  let { icon: Icon, title, children, actions }: {
    icon: Component<{ class?: string }>;
    title: string;
    children?: Snippet;
    actions?: Snippet;
  } = $props();
</script>

<div class="flex flex-col items-center gap-2 px-6 py-10 text-center">
  <div class="mb-1 grid size-10 place-items-center rounded-full bg-muted text-muted-foreground">
    <Icon class="size-5" />
  </div>
  <p class="font-medium">{title}</p>
  {#if children}<div class="max-w-md text-muted-foreground">{@render children()}</div>{/if}
  {#if actions}<div class="mt-2 flex gap-2">{@render actions()}</div>{/if}
</div>
