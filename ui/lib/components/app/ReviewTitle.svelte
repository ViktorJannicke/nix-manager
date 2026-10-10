<script lang="ts">
  import type { BuildReview } from "../../../../src/contracts";
  import { scopeLabel } from "$lib/format";

  let { review }: { review: BuildReview } = $props();
  const what = $derived(scopeLabel(review.scope).toLowerCase());
</script>

{#if review.source === "generation"}
  Return {review.scope} to generation {review.generation}
{:else if review.source === "inputs"}
  {#if review.inputs?.names.length}
    Update {review.inputs.names.length > 3 ? `${review.inputs.names.length} inputs` : review.inputs.names.join(", ")}
  {:else}
    Update all flake inputs
  {/if}
  <span class="font-normal text-muted-foreground">({what})</span>
{:else}
  Apply your checkout <span class="font-normal text-muted-foreground">({what})</span>
{/if}
