<script lang="ts">
  import type { BuildReview } from "../../../generated/types.js";
  import { dateTime, scopeText } from "$lib/format";
  import { m, slot } from "$lib/i18n";
  import Markup from "./Markup.svelte";

  let { review }: { review: BuildReview } = $props();

  // What the review does, with the scope in a lighter tone where the translation puts it.
  const title = $derived.by(() => {
    const scope = slot("scope");
    if (review.undo) return m.review_title_undo({ date: dateTime(review.undo.appliedAt), scope });
    if (review.source === "generation") return m.review_title_generation({ generation: String(review.generation ?? ""), scope });
    if (review.source === "inputs") {
      const names = review.inputs?.names ?? [];
      if (!names.length) return m.review_title_all_sources({ scope });
      return names.length > 3 ? m.review_title_sources({ count: names.length, scope }) : m.review_title_names({ names: names.join(", "), scope });
    }
    return m.review_title_configuration({ scope });
  });
</script>

<Markup text={title}>{#snippet children()}<span class="font-normal text-muted-foreground">({scopeText(review.scope)})</span>{/snippet}</Markup>
