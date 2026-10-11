<script lang="ts">
  import type { BuildReview } from "../../../../src/contracts";
  import { dateTime, scopeLabel } from "$lib/format";

  let { review }: { review: BuildReview } = $props();
  const what = $derived(scopeLabel(review.scope).toLowerCase());
</script>

{#if review.undo}
  Undo the change from {dateTime(review.undo.appliedAt)} <span class="font-normal text-muted-foreground">({what})</span>
{:else if review.source === "generation"}
  Go back to restore point {review.generation} <span class="font-normal text-muted-foreground">({what})</span>
{:else if review.source === "inputs"}
  {#if review.inputs?.names.length}
    Update {review.inputs.names.length > 3 ? `${review.inputs.names.length} software sources` : review.inputs.names.join(", ")}
  {:else}
    Update all software sources
  {/if}
  <span class="font-normal text-muted-foreground">({what})</span>
{:else}
  Apply your configuration <span class="font-normal text-muted-foreground">({what})</span>
{/if}
