<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { m } from "$lib/i18n";

  // Simple mode applies pending edits with one button where they qualify; otherwise both modes prepare a review.
  const simple = $derived(app.detail === "simple");
  const preparing = $derived(app.active?.request.action === "review-build" && Boolean(app.active.request.edits));
  const problems = $derived(app.draft?.problems.length ?? 0);
  const label = $derived(
    preparing ? m.common_preparing() : !simple ? m.common_prepare() : app.pendingInOneStep ? m.pending_apply() : m.pending_review_and_apply(),
  );
</script>

<Button
  size="sm"
  disabled={app.busy || !app.connected || problems === app.pendingChanges.length}
  onclick={() => (simple ? app.applyPending() : app.preparePending())}
>{label}</Button>
