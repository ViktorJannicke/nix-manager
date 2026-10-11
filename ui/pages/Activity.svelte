<script lang="ts">
  import ListIcon from "@lucide/svelte/icons/list";
  import RotateCcwIcon from "@lucide/svelte/icons/rotate-ccw";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { t } from "$lib/i18n";
  import type { Operation } from "../../src/contracts";
  import { actionLabels } from "../../src/domain";
  import { dateTime, duration, relative, scopeLabel } from "$lib/format";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import StatusBadge from "$lib/components/app/StatusBadge.svelte";
  import StepList from "$lib/components/app/StepList.svelte";
  import LogViewer from "$lib/components/app/LogViewer.svelte";

  let filter = $state<"all" | "problems">("all");
  const operations = $derived(app.snapshot?.operations ?? []);
  const problems = (operation: Operation) => operation.status === "failed" || operation.status === "interrupted";
  const visible = $derived(filter === "all" ? operations : operations.filter(problems));
  const selected = $derived(
    operations.find((operation) => operation.id === app.selectedOperation) ?? operations[0],
  );

  $effect(() => {
    if (selected && app.log.id !== selected.id) app.selectOperation(selected.id);
  });

  const retryable = (operation: Operation) =>
    operation.status !== "running" &&
    operation.status !== "succeeded" &&
    (operation.request.action !== "review-apply" || app.reviews.some((r) => r.id === operation.request.reviewId));

  function retry(operation: Operation) {
    const { reviewId, ...rest } = operation.request;
    void app.run(operation.request.action === "review-build" ? rest : operation.request);
  }
</script>

<div class="flex h-full min-h-0 gap-5">
  <aside class="flex w-72 shrink-0 flex-col">
    <div class="mb-3 flex items-center justify-between gap-2">
      <h1 class="title">{t("Activity")}</h1>
      <Segmented
        label={t("Filter")}
        bind:value={filter}
        options={[{ value: "all", label: t("All") }, { value: "problems", label: t("Problems"), count: operations.filter(problems).length }]}
      />
    </div>
    <ul class="scroll-thin -mx-1 min-h-0 flex-1 space-y-0.5 overflow-auto px-1">
      {#each visible as operation (operation.id)}
        <li>
          <button
            class="focus-ring w-full rounded-lg px-3 py-2 text-left transition-colors hover:bg-muted/60 {selected?.id === operation.id ? 'bg-muted' : ''}"
            onclick={() => app.selectOperation(operation.id)}
          >
            <div class="flex items-center gap-2">
              <span class="min-w-0 flex-1 truncate font-medium">{t(actionLabels[operation.request.action])}</span>
              <StatusBadge status={operation.status} />
            </div>
            <p class="mt-0.5 text-xs text-muted-foreground">
              {t("{scope}, {when}", { scope: scopeLabel(operation.request.scope), when: relative(operation.startedAt, app.now) })}
            </p>
          </button>
        </li>
      {:else}
        <li class="px-3 py-6 text-center text-muted-foreground">{t("Nothing to show.")}</li>
      {/each}
    </ul>
  </aside>

  <section class="flex min-w-0 flex-1 flex-col gap-4">
    {#if selected}
      <div class="panel p-4">
        <div class="flex flex-wrap items-start gap-3">
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <h2 class="text-lg font-semibold">{t(actionLabels[selected.request.action])}</h2>
              <StatusBadge status={selected.status} />
            </div>
            <p class="text-xs text-muted-foreground">
              {t("{scope} on {host}, started {started}, took {duration}", {
                scope: scopeLabel(selected.request.scope),
                host: selected.request.host,
                started: dateTime(selected.startedAt),
                duration: duration(selected, app.now),
              })}
              {#if selected.request.update}<br />{t("Updates all flake inputs")}{/if}
              {#if selected.request.inputs?.length}<br />{t("Updates {inputs}", { inputs: selected.request.inputs.join(", ") })}{/if}
              {#if selected.request.activation === "boot"}<br />{t("Applies on next boot")}{/if}
            </p>
          </div>
          {#if selected.status === "running"}
            <Button variant="outline" size="sm" disabled={selected.finishingPrivilegedStep} onclick={() => app.cancel()}>
              {selected.finishingPrivilegedStep ? t("Finishing…") : t("Cancel")}
            </Button>
          {:else if retryable(selected)}
            <Button variant="outline" size="sm" disabled={app.busy} onclick={() => retry(selected)}><RotateCcwIcon /> {t("Try again")}</Button>
          {/if}
        </div>
        {#if selected.explanation}
          <div class="mt-3 rounded-lg bg-destructive/10 px-3 py-2">
            <p class="font-medium text-destructive">{t(selected.explanation.title)}</p>
            <p class="mt-0.5 text-xs">{t(selected.explanation.body)}</p>
            {#if selected.explanation.fix}
              <Button size="sm" class="mt-2" disabled={app.busy} onclick={() => app.applyFix(selected.explanation!.fix!, selected.request.scope)}>{t("Allow non-free software")}</Button>
            {/if}
            {#if selected.error}
              <details class="mt-1.5 text-xs text-muted-foreground">
                <summary class="cursor-pointer select-none">{t("What Nix reported")}</summary>
                <p class="mt-1 font-mono text-[0.7rem] break-words whitespace-pre-wrap">{selected.error}</p>
              </details>
            {/if}
          </div>
        {:else if selected.error}
          <p class="mt-3 rounded-lg bg-destructive/10 px-3 py-2 text-xs break-words text-destructive">{selected.error}</p>
        {/if}
      </div>
      <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
      <div tabindex="0" role="region" aria-label={t("Steps")} class="focus-ring panel scroll-thin max-h-60 shrink-0 overflow-auto p-2"><StepList operation={selected} /></div>
      <LogViewer id={selected.id} />
    {:else}
      <div class="panel"><EmptyState icon={ListIcon} title={t("No activity yet")}>{t("Operations you start appear here with their steps and output.")}</EmptyState></div>
    {/if}
  </section>
</div>
