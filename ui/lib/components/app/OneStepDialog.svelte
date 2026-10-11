<script lang="ts">
  import ChevronRightIcon from "@lucide/svelte/icons/chevron-right";
  import CircleCheckIcon from "@lucide/svelte/icons/circle-check";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import type { Operation } from "../../../../src/contracts";
  import BuildProgress from "./BuildProgress.svelte";
  import StatusBadge from "./StatusBadge.svelte";
  import StepList from "./StepList.svelte";

  let details = $state(false);
  // The last change shown stays rendered while the dialog fades out.
  let step = $state(app.oneStep);
  $effect(() => {
    if (app.oneStep) step = app.oneStep;
  });
  const operations = $derived(app.snapshot?.operations ?? []);
  const prepare = $derived(operations.find((operation) => operation.id === step?.prepare));
  const apply = $derived(
    operations.find((operation) => operation.request.action === "review-apply" && operation.request.reviewId === step?.reviewId),
  );
  /** The operation the dialog is about now. */
  const current = $derived(apply ?? prepare);
  const running = $derived(current?.status === "running" || (prepare?.status === "succeeded" && !prepare.held && !apply));
  const building = $derived(prepare?.steps?.find((item) => item.status === "running" && item.label.startsWith("Build")));

  function close() {
    // Closing while it runs only hides the progress; the outcome then arrives as a notification.
    app.oneStep = undefined;
    details = false;
  }
  function show(operation: Operation) {
    close();
    app.selectOperation(operation.id);
    app.navigate("activity");
  }
  function review() {
    if (!step) return;
    app.selectedReview = step.reviewId;
    close();
    app.navigate("changes");
  }
</script>

<Dialog.Root open={Boolean(app.oneStep)} onOpenChange={(open) => { if (!open) close(); }}>
  <Dialog.Content class="sm:max-w-md">
    {#if step && prepare}
      <Dialog.Header>
        <Dialog.Title>
          {#if apply?.status === "succeeded"}{step.done}{:else if prepare.held && prepare.status === "succeeded"}Ready for your review{:else}{step.title}{/if}
        </Dialog.Title>
        <Dialog.Description>
          {#if apply?.status === "succeeded"}
            Only your apps changed. You can undo it until you clean up old versions.
          {:else if prepare.held && prepare.status === "succeeded"}
            It was prepared but not applied. {prepare.held}
          {:else if current?.status === "failed" || current?.status === "cancelled" || current?.status === "interrupted"}
            {#if current.request.action === "review-apply"}
              It could not be applied, though your configuration files may already contain the change. The Generations page can take you back.
            {:else}
              It could not be prepared, so nothing changed. Your change is still in your pending edits.
            {/if}
          {:else if prepare.held}
            It waits for your review when it is ready.
          {:else}
            Only your apps change, so it is applied as soon as it is ready.
          {/if}
        </Dialog.Description>
      </Dialog.Header>

      <ol class="space-y-2" aria-live="polite">
        <li class="flex items-center gap-3">
          <span class="flex-1">Download and check</span>
          <StatusBadge status={prepare.status} />
        </li>
        {#if building}
          <li class="pl-1"><BuildProgress progress={app.stepProgress(prepare, building)} /></li>
        {:else if prepare.status === "running"}
          <li class="text-xs text-muted-foreground">{prepare.step}…</li>
        {/if}
        <li class="flex items-center gap-3">
          <span class="flex-1 {apply ? '' : 'text-muted-foreground'}">Install</span>
          <StatusBadge status={apply?.status ?? (prepare.held || prepare.status !== "succeeded" ? "skipped" : "pending")} />
        </li>
      </ol>

      {#if current && (current.status === "failed" || current.status === "interrupted")}
        <div class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-xs">
          <TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0 text-destructive" />
          <div class="space-y-1">
            <p class="font-medium">{current.explanation?.title ?? "Something went wrong"}</p>
            <p class="break-words text-muted-foreground">{current.explanation?.body ?? current.error}</p>
          </div>
        </div>
      {:else if apply?.status === "succeeded"}
        <p class="flex gap-2 rounded-lg bg-success/10 px-3 py-2 text-xs">
          <CircleCheckIcon class="mt-0.5 size-3.5 shrink-0 text-success" />Apps you just installed can take a moment to appear in your menu.
        </p>
      {/if}

      {#if current}
        <div>
          <button
            class="focus-ring flex items-center gap-1 rounded text-xs text-muted-foreground hover:text-foreground"
            aria-expanded={details}
            onclick={() => (details = !details)}
          >
            <ChevronRightIcon class="size-3.5 transition-transform {details ? 'rotate-90' : ''}" /> Details
          </button>
          {#if details}
            <div class="mt-2 rounded-lg border p-1 text-xs"><StepList operation={current} /></div>
            <Button size="sm" variant="link" class="px-0" onclick={() => show(current)}>Show the full log</Button>
          {/if}
        </div>
      {/if}

      <Dialog.Footer>
        {#if prepare.status === "running"}
          <Button variant="ghost" onclick={close}>Hide</Button>
          {#if !prepare.held}
            <Button variant="outline" onclick={() => app.hold(prepare.id)}>Review it first</Button>
          {/if}
          <Button variant="outline" onclick={() => app.cancel()}>Stop</Button>
        {:else if running}
          <Button variant="ghost" onclick={close}>Hide</Button>
        {:else if apply?.status === "succeeded"}
          {#if app.lastChange?.id === apply.id}
            <Button variant="outline" disabled={app.busy} onclick={() => { close(); void app.undo(apply); }}>Undo</Button>
          {/if}
          <Button onclick={close}>Done</Button>
        {:else if prepare.status === "succeeded" && prepare.held}
          <Button variant="ghost" onclick={close}>Close</Button>
          <Button onclick={review}>Open the review</Button>
        {:else}
          {#if current?.explanation?.fix}
            <Button variant="outline" onclick={() => { close(); app.applyFix(current.explanation!.fix!, "home"); }}>Allow non-free software</Button>
          {/if}
          <Button onclick={close}>Close</Button>
        {/if}
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
