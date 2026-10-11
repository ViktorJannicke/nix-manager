<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { software } from "$lib/software.svelte";

  // Simple mode applies pending edits with one button where they qualify; otherwise both modes prepare a review.
  const simple = $derived(app.detail === "simple");
  const preparing = $derived(app.active?.request.action === "review-build" && app.active.request.edits);
  const problems = $derived(software.draft?.problems.length ?? 0);
  const label = $derived(
    preparing ? "Preparing…" : !simple ? "Prepare" : software.pendingInOneStep ? "Apply changes" : "Review and apply",
  );
</script>

<Button
  size="sm"
  disabled={app.busy || !app.connected || problems === software.changes.length}
  onclick={() => (simple ? software.applyPending() : software.prepare())}
>{label}</Button>
