<script lang="ts">
  import type { BuildReview } from "../../../../src/contracts";
  import { dateTime, scopeText } from "$lib/format";
  import T from "./T.svelte";

  let { review }: { review: BuildReview } = $props();
  const what = $derived(scopeText(review.scope));
</script>

{#if review.undo}
  <T text={"Undo the change from {date} {scope}"} values={{ date: dateTime(review.undo.appliedAt) }}>
    {#snippet children()}{@render scope()}{/snippet}
  </T>
{:else if review.source === "generation"}
  <T text={"Go back to restore point {generation} {scope}"} values={{ generation: review.generation ?? "" }}>
    {#snippet children()}{@render scope()}{/snippet}
  </T>
{:else if review.source === "inputs"}
  {#if review.inputs?.names.length}
    {#if review.inputs.names.length > 3}
      <T text={"Update {count} software sources {scope}"} values={{ count: review.inputs.names.length }}>
        {#snippet children()}{@render scope()}{/snippet}
      </T>
    {:else}
      <T text={"Update {names} {scope}"} values={{ names: review.inputs.names.join(", ") }}>
        {#snippet children()}{@render scope()}{/snippet}
      </T>
    {/if}
  {:else}
    <T text={"Update all software sources {scope}"}>
      {#snippet children()}{@render scope()}{/snippet}
    </T>
  {/if}
{:else}
  <T text={"Apply your configuration {scope}"}>
    {#snippet children()}{@render scope()}{/snippet}
  </T>
{/if}

{#snippet scope()}<span class="font-normal text-muted-foreground">({what})</span>{/snippet}
