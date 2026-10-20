<script lang="ts">
  import ChevronRightIcon from "@lucide/svelte/icons/chevron-right";
  import CircleCheckIcon from "@lucide/svelte/icons/circle-check";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import type { Operation } from "../../../generated/types.js";
  import { app } from "$lib/app.svelte";
  import { remaining } from "$lib/format";
  import { m, text, textKey } from "$lib/i18n";
  import BuildProgress from "./BuildProgress.svelte";
  import StatusBadge from "./StatusBadge.svelte";
  import StepList from "./StepList.svelte";

  let details = $state(false);
  // The last change shown stays rendered while the dialog fades out.
  let step = $state(app.oneStep);
  $effect(() => {
    if (app.oneStep) step = app.oneStep;
  });
  const operations = $derived(app.history);
  const prepare = $derived(operations.find((operation) => operation.id === step?.prepare));
  const apply = $derived(
    operations.find((operation) => operation.request.action === "review-apply" && operation.request.reviewId === step?.reviewId),
  );
  /** The operation the dialog is about now. */
  const current = $derived(apply ?? prepare);
  const failed = $derived(current?.status === "failed" || current?.status === "cancelled" || current?.status === "interrupted");
  const running = $derived(current?.status === "running" || (prepare?.status === "succeeded" && !prepare.held && !apply));
  const left = $derived(current?.status === "running" ? remaining(operations, current, app.now) : undefined);
  /** Changes once per stage, never with each progress update, so screen readers are not flooded. */
  const announcement = $derived(
    !current || current.status === "failed" || current.status === "interrupted" ? ""
    : apply?.status === "succeeded" ? m.onestep_installed()
    : apply ? m.onestep_installing()
    : prepare?.status === "succeeded" ? (prepare.held ? m.onestep_ready_for_review() : m.onestep_downloaded_and_checked())
    : m.onestep_downloading_and_checking(),
  );
  /** The steps that build a configuration (Domain/Plan.cs). */
  const buildSteps = new Set(["stored_build_home_configuration", "stored_build_system_configuration"]);
  const building = $derived(prepare?.steps?.find((item) => item.status === "running" && buildSteps.has(textKey(item.label) ?? "")));
  const lastChange = $derived(app.lastChange?.operation.id);

  function close() {
    // Closing while it runs only hides the progress; the outcome then arrives as a notification.
    app.oneStep = undefined;
    details = false;
  }
  function show(operation: Operation) {
    close();
    app.show(operation.id);
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
          {#if apply?.status === "succeeded"}{step.done}{:else if prepare.held && prepare.status === "succeeded"}{m.onestep_ready_for_review()}{:else}{step.title}{/if}
        </Dialog.Title>
        <Dialog.Description>
          {#if apply?.status === "succeeded"}
            {m.onestep_only_apps_changed()}
          {:else if prepare.held && prepare.status === "succeeded"}
            {m.onestep_held({ reason: text(prepare.held) })}
          {:else if failed && current}
            {#if current.request.action === "review-apply" && current.rollback?.done}
              {m.onestep_failed_went_back()}
            {:else if current.request.action === "review-apply" && current.rollback && !current.rollback.error}
              {m.onestep_failed_partly_undone()}
            {:else if current.request.action === "review-apply" && current.rollback}
              {m.onestep_failed_not_undone()}
            {:else if current.request.action === "review-apply"}
              {m.onestep_failed_before_change()}
            {:else}
              {m.onestep_failed_to_prepare()}
            {/if}
          {:else if prepare.held}
            {m.onestep_waits_for_review()}
          {:else}
            {m.onestep_applied_when_ready()}
          {/if}
        </Dialog.Description>
      </Dialog.Header>

      <p class="sr-only" role="status">{announcement}</p>
      <ol role="list" class="space-y-2" aria-label={m.onestep_stages()}>
        <li class="flex items-center gap-3">
          <span class="flex-1">{m.onestep_download_and_check()}</span>
          <StatusBadge status={prepare.status} />
        </li>
        {#if building}
          <li class="pl-1"><BuildProgress progress={building.progress} /></li>
        {:else if prepare.status === "running"}
          <li class="text-xs text-muted-foreground">{text(prepare.step)}…</li>
        {/if}
        <li class="flex items-center gap-3">
          <span class="flex-1 {apply ? '' : 'text-muted-foreground'}">{m.onestep_install()}</span>
          <StatusBadge status={apply?.status ?? (prepare.held || prepare.status !== "succeeded" ? "skipped" : "pending")} />
        </li>
      </ol>
      {#if left}<p class="text-xs text-muted-foreground">{left}</p>{/if}

      {#if current && (current.status === "failed" || current.status === "interrupted")}
        <div class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-xs" role="alert">
          <TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0 text-destructive" />
          <div class="space-y-1">
            <p class="font-medium">{current.explanation ? text(current.explanation.title) : m.common_something_went_wrong()}</p>
            <p class="break-words text-muted-foreground">{text(current.explanation?.body ?? current.error)}</p>
          </div>
        </div>
      {:else if apply?.status === "succeeded"}
        <p class="flex gap-2 rounded-lg bg-success/10 px-3 py-2 text-xs">
          <CircleCheckIcon class="mt-0.5 size-3.5 shrink-0 text-success" />{m.onestep_menu_delay()}
        </p>
      {/if}

      {#if current}
        <div>
          <button
            class="focus-ring flex items-center gap-1 rounded text-xs text-muted-foreground hover:text-foreground"
            aria-expanded={details}
            onclick={() => (details = !details)}
          >
            <ChevronRightIcon class="size-3.5 transition-transform {details ? 'rotate-90' : ''}" /> {m.onestep_details()}
          </button>
          {#if details}
            <div class="mt-2 rounded-lg border p-1 text-xs"><StepList operation={current} /></div>
            <Button size="sm" variant="link" class="px-0" onclick={() => show(current)}>{m.onestep_full_log()}</Button>
          {/if}
        </div>
      {/if}

      <Dialog.Footer>
        {#if prepare.status === "running"}
          <Button variant="ghost" onclick={close}>{m.onestep_hide()}</Button>
          {#if !prepare.held}
            <Button variant="outline" onclick={() => app.hold(prepare.id)}>{m.onestep_review_first()}</Button>
          {/if}
          <Button variant="outline" onclick={() => app.cancel()}>{m.onestep_stop()}</Button>
        {:else if running}
          <Button variant="ghost" onclick={close}>{m.onestep_hide()}</Button>
        {:else if apply?.status === "succeeded"}
          {#if lastChange === apply.id}
            <Button variant="outline" disabled={app.busy} onclick={() => { close(); void app.undo(apply); }}>{m.common_undo()}</Button>
          {/if}
          <Button onclick={close}>{m.common_done()}</Button>
        {:else if prepare.status === "succeeded" && prepare.held}
          <Button variant="ghost" onclick={close}>{m.common_close()}</Button>
          <Button onclick={review}>{m.onestep_open_review()}</Button>
        {:else}
          {#if current?.explanation?.fix}
            {@const fix = current.explanation.fix}
            <Button variant="outline" onclick={() => { close(); app.applyFix(fix, "home"); }}>{m.toast_allow_non_free()}</Button>
          {/if}
          <Button onclick={close}>{m.common_close()}</Button>
        {/if}
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
