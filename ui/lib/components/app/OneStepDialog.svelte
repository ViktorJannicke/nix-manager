<script lang="ts">
  import ChevronRightIcon from "@lucide/svelte/icons/chevron-right";
  import CircleCheckIcon from "@lucide/svelte/icons/circle-check";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { remaining } from "$lib/format";
  import { t } from "$lib/i18n";
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
  const left = $derived(current?.status === "running" ? remaining(operations, current, app.now) : undefined);
  /** Changes once per stage, never with each progress update, so screen readers are not flooded. */
  const announcement = $derived(
    !current ? "" :
    current.status === "failed" || current.status === "interrupted" ? "" :
    apply?.status === "succeeded" ? t("Installed") :
    apply ? t("Installing") :
    prepare?.status === "succeeded" ? (prepare.held ? t("Ready for your review") : t("Downloaded and checked")) :
    t("Downloading and checking"),
  );
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
          {#if apply?.status === "succeeded"}{step.done}{:else if prepare.held && prepare.status === "succeeded"}{t("Ready for your review")}{:else}{step.title}{/if}
        </Dialog.Title>
        <Dialog.Description>
          {#if apply?.status === "succeeded"}
            {t("Only your apps changed. You can undo it until you clean up old versions.")}
          {:else if prepare.held && prepare.status === "succeeded"}
            {t("It was prepared but not applied. {reason}", { reason: t(prepare.held) })}
          {:else if current?.status === "failed" || current?.status === "cancelled" || current?.status === "interrupted"}
            {#if current.request.action === "review-apply" && current.rollback?.done}
              {t("It could not be applied, so everything went back to how it was. Your change is still in your pending edits.")}
            {:else if current.request.action === "review-apply" && current.rollback && !current.rollback.error}
              {t("It could not be applied, and only part of it could be undone. Your change is still in your pending edits.")}
            {:else if current.request.action === "review-apply" && current.rollback}
              {t("It could not be applied, and going back did not work. The Restore points page can take you back.")}
            {:else if current.request.action === "review-apply"}
              {t("It could not be applied before anything changed. Your change is still in your pending edits.")}
            {:else}
              {t("It could not be prepared, so nothing changed. Your change is still in your pending edits.")}
            {/if}
          {:else if prepare.held}
            {t("It waits for your review when it is ready.")}
          {:else}
            {t("Only your apps change, so it is applied as soon as it is ready.")}
          {/if}
        </Dialog.Description>
      </Dialog.Header>

      <p class="sr-only" role="status">{announcement}</p>
      <ol class="space-y-2">
        <li class="flex items-center gap-3">
          <span class="flex-1">{t("Download and check")}</span>
          <StatusBadge status={prepare.status} />
        </li>
        {#if building}
          <li class="pl-1"><BuildProgress progress={app.stepProgress(prepare, building)} /></li>
        {:else if prepare.status === "running"}
          <li class="text-xs text-muted-foreground">{t(prepare.step)}…</li>
        {/if}
        <li class="flex items-center gap-3">
          <span class="flex-1 {apply ? '' : 'text-muted-foreground'}">{t("Install")}</span>
          <StatusBadge status={apply?.status ?? (prepare.held || prepare.status !== "succeeded" ? "skipped" : "pending")} />
        </li>
      </ol>
      {#if left}<p class="text-xs text-muted-foreground">{left}</p>{/if}

      {#if current && (current.status === "failed" || current.status === "interrupted")}
        <div class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-xs" role="alert">
          <TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0 text-destructive" />
          <div class="space-y-1">
            <p class="font-medium">{current.explanation ? t(current.explanation.title) : t("Something went wrong")}</p>
            <p class="break-words text-muted-foreground">{current.explanation ? t(current.explanation.body) : current.error && t(current.error)}</p>
          </div>
        </div>
      {:else if apply?.status === "succeeded"}
        <p class="flex gap-2 rounded-lg bg-success/10 px-3 py-2 text-xs">
          <CircleCheckIcon class="mt-0.5 size-3.5 shrink-0 text-success" />{t("Apps you just installed can take a moment to appear in your menu.")}
        </p>
      {/if}

      {#if current}
        <div>
          <button
            class="focus-ring flex items-center gap-1 rounded text-xs text-muted-foreground hover:text-foreground"
            aria-expanded={details}
            onclick={() => (details = !details)}
          >
            <ChevronRightIcon class="size-3.5 transition-transform {details ? 'rotate-90' : ''}" /> {t("Details")}
          </button>
          {#if details}
            <div class="mt-2 rounded-lg border p-1 text-xs"><StepList operation={current} /></div>
            <Button size="sm" variant="link" class="px-0" onclick={() => show(current)}>{t("Show the full log")}</Button>
          {/if}
        </div>
      {/if}

      <Dialog.Footer>
        {#if prepare.status === "running"}
          <Button variant="ghost" onclick={close}>{t("Hide")}</Button>
          {#if !prepare.held}
            <Button variant="outline" onclick={() => app.hold(prepare.id)}>{t("Review it first")}</Button>
          {/if}
          <Button variant="outline" onclick={() => app.cancel()}>{t("Stop")}</Button>
        {:else if running}
          <Button variant="ghost" onclick={close}>{t("Hide")}</Button>
        {:else if apply?.status === "succeeded"}
          {#if app.lastChange?.id === apply.id}
            <Button variant="outline" disabled={app.busy} onclick={() => { close(); void app.undo(apply); }}>{t("Undo")}</Button>
          {/if}
          <Button onclick={close}>{t("Done")}</Button>
        {:else if prepare.status === "succeeded" && prepare.held}
          <Button variant="ghost" onclick={close}>{t("Close")}</Button>
          <Button onclick={review}>{t("Open the review")}</Button>
        {:else}
          {#if current?.explanation?.fix}
            <Button variant="outline" onclick={() => { close(); app.applyFix(current.explanation!.fix!, "home"); }}>{t("Allow non-free software")}</Button>
          {/if}
          <Button onclick={close}>{t("Close")}</Button>
        {/if}
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
