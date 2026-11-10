<script lang="ts">
  import ListIcon from "@lucide/svelte/icons/list";
  import RotateCcwIcon from "@lucide/svelte/icons/rotate-ccw";
  import CirclePauseIcon from "@lucide/svelte/icons/circle-pause";
  import type { Operation, Request } from "../generated/types.js";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { actionLabel, m, text } from "$lib/i18n";
  import { dateTime, duration, relative, scopeLabel } from "$lib/format";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import StatusBadge from "$lib/components/app/StatusBadge.svelte";
  import StepList from "$lib/components/app/StepList.svelte";
  import LogViewer from "$lib/components/app/LogViewer.svelte";

  let filter = $state<"all" | "problems">("all");
  const operations = $derived(app.history);
  const problems = (operation: Operation) => operation.status === "failed" || operation.status === "interrupted";
  const visible = $derived(filter === "all" ? operations : operations.filter(problems));
  const selected = $derived(app.operation(app.operations?.selectedOperationId) ?? operations[0]);

  const retryable = (operation: Operation) =>
    operation.status !== "running" &&
    operation.status !== "succeeded" &&
    (operation.request.action !== "review-apply" || app.reviews.some((r) => r.id === operation.request.reviewId));

  // A review is prepared again under a new ID; applying one again needs it to still exist.
  function retry(operation: Operation) {
    void app.run(operation.request.action === "review-build" ? { ...operation.request, reviewId: null } : operation.request);
  }

  const held = $derived(app.queue.some((entry) => entry.held));

  /** What a queued request does besides its action, such as what it prepares and what follows. */
  function queueDetail(r: Request) {
    const parts: string[] = [];
    // A pin update's action names the app, and checking for updates looks at everything.
    if (r.action !== "check" && !r.action.endsWith("-update")) parts.push(scopeLabel(r.scope));
    if (r.update) parts.push(m.activity_queue_all_sources());
    if (r.inputs?.length) parts.push(m.activity_updates_inputs({ inputs: r.inputs.join(", ") }));
    if (r.edits) parts.push(m.activity_queue_pending_edits());
    if (r.undo) parts.push(m.activity_queue_undo());
    if (r.then === "switch" || r.then === "apply") parts.push(m.activity_queue_then_apply_now());
    if (r.then === "boot") parts.push(m.activity_queue_then_apply_on_boot());
    if (r.activation === "boot") parts.push(m.activity_applies_on_next_boot());
    return parts.join(" · ");
  }
</script>

<div class="flex h-full min-h-0 gap-5">
  <aside class="flex w-72 shrink-0 flex-col">
    <div class="mb-3 flex items-center justify-between gap-2">
      <h1 id="activity-heading" class="title">{m.nav_activity()}</h1>
      <Segmented
        label={m.activity_filter()}
        bind:value={filter}
        options={[{ value: "all", label: m.activity_all() }, { value: "problems", label: m.activity_problems(), count: operations.filter(problems).length }]}
      />
    </div>
    {#if app.queue.length}
      <!-- What waits for the running operation, in the order it starts; held entries say why they do not start. -->
      <section class="mb-4 flex max-h-[45%] shrink-0 flex-col" aria-labelledby="queue-heading">
        <h2 id="queue-heading" class="label mb-1.5 px-1">{m.activity_queue()}</h2>
        <ol role="list" aria-labelledby="queue-heading" class="scroll-thin -mx-1 min-h-0 space-y-1 overflow-auto px-1">
          {#each app.queue as entry (entry.id)}
            {@const label = actionLabel(entry.request.action)}
            {@const detail = queueDetail(entry.request)}
            <li class="rounded-lg border px-3 py-2">
              <div class="flex items-start gap-2">
                <div class="min-w-0 flex-1">
                  <p class="font-medium">{label}</p>
                  {#if detail}<p class="mt-0.5 text-xs text-muted-foreground">{detail}</p>{/if}
                </div>
                <Button variant="ghost" size="xs" class="-mr-1.5" disabled={!app.connected} aria-label={m.activity_queue_remove_label({ label })} onclick={() => app.unqueue(entry.id)}>
                  {m.activity_queue_remove()}
                </Button>
              </div>
              {#if entry.held}
                <p class="mt-1.5 flex gap-1.5 text-xs text-warning"><CirclePauseIcon class="mt-px size-3.5 shrink-0" />{text(entry.held)}</p>
              {/if}
            </li>
          {/each}
        </ol>
        {#if held}
          <p class="mt-2 px-1 text-xs text-muted-foreground">{m.activity_queue_held_hint()}</p>
          <div class="mt-2 flex flex-wrap gap-2 px-1">
            <Button size="sm" disabled={!app.connected} onclick={() => app.continueQueue()}>{m.activity_queue_continue()}</Button>
            <Button size="sm" variant="outline" disabled={!app.connected} onclick={() => app.clearQueue()}>{m.activity_queue_clear()}</Button>
          </div>
        {/if}
      </section>
      <h2 class="sr-only">{m.activity_history()}</h2>
    {/if}
    <ul role="list" aria-labelledby="activity-heading" class="scroll-thin -mx-1 min-h-0 flex-1 space-y-0.5 overflow-auto px-1">
      {#each visible as operation (operation.id)}
        <li>
          <button
            class="focus-ring w-full rounded-lg px-3 py-2 text-left transition-colors hover:bg-muted/60 {selected?.id === operation.id ? 'bg-muted' : ''}"
            aria-current={selected?.id === operation.id ? "true" : undefined}
            onclick={() => app.selectOperation(operation.id)}
          >
            <div class="flex items-center gap-2">
              <span class="min-w-0 flex-1 truncate font-medium">{actionLabel(operation.request.action)}</span>
              <StatusBadge status={operation.status} />
            </div>
            <p class="mt-0.5 text-xs text-muted-foreground">
              {m.activity_scope_when({ scope: scopeLabel(operation.request.scope), when: relative(operation.startedAt, app.now) })}
            </p>
          </button>
        </li>
      {:else}
        <li class="px-3 py-6 text-center text-muted-foreground">{m.activity_nothing()}</li>
      {/each}
    </ul>
  </aside>

  <section class="flex min-w-0 flex-1 flex-col gap-4" aria-label={selected ? actionLabel(selected.request.action) : m.nav_activity()}>
    {#if selected}
      <div class="panel p-4">
        <div class="flex flex-wrap items-start gap-3">
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <h2 class="text-lg font-semibold">{actionLabel(selected.request.action)}</h2>
              <StatusBadge status={selected.status} />
            </div>
            <p class="text-xs text-muted-foreground">
              {m.activity_details({
                scope: scopeLabel(selected.request.scope),
                host: selected.request.host,
                started: dateTime(selected.startedAt),
                duration: duration(selected, app.now),
              })}
              {#if selected.request.update}<br />{m.activity_updates_all_inputs()}{/if}
              {#if selected.request.inputs?.length}<br />{m.activity_updates_inputs({ inputs: selected.request.inputs.join(", ") })}{/if}
              {#if selected.request.activation === "boot"}<br />{m.activity_applies_on_next_boot()}{/if}
            </p>
          </div>
          {#if selected.status === "running"}
            <Button variant="outline" size="sm" disabled={Boolean(selected.finishingPrivilegedStep) || selected.id !== app.active?.id} onclick={() => app.cancel()}>
              {selected.finishingPrivilegedStep ? m.activity_finishing() : m.common_cancel()}
            </Button>
          {:else if retryable(selected)}
            <Button variant="outline" size="sm" disabled={!app.connected} onclick={() => retry(selected)}><RotateCcwIcon /> {m.activity_try_again()}</Button>
          {/if}
        </div>
        {#if selected.explanation}
          <div class="mt-3 rounded-lg bg-destructive/10 px-3 py-2">
            <p class="font-medium text-destructive">{text(selected.explanation.title)}</p>
            <p class="mt-0.5 text-xs">{text(selected.explanation.body)}</p>
            {#if selected.explanation.fix}
              {@const fix = selected.explanation.fix}
              <Button size="sm" class="mt-2" onclick={() => app.applyFix(fix, selected.request.scope)}>{m.toast_allow_non_free()}</Button>
            {/if}
            {#if selected.error}
              <details class="mt-1.5 text-xs text-muted-foreground">
                <summary class="cursor-pointer select-none">{m.activity_what_nix_reported()}</summary>
                <p class="mt-1 font-mono text-[0.7rem] break-words whitespace-pre-wrap">{selected.error}</p>
              </details>
            {/if}
          </div>
        {:else if selected.error}
          <p class="mt-3 rounded-lg bg-destructive/10 px-3 py-2 text-xs break-words text-destructive">{text(selected.error)}</p>
        {/if}
      </div>
      <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
      <div tabindex="0" role="region" aria-label={m.activity_steps()} class="focus-ring panel scroll-thin max-h-60 shrink-0 overflow-auto p-2"><StepList operation={selected} /></div>
      <LogViewer id={selected.id} />
    {:else}
      <div class="panel"><EmptyState icon={ListIcon} title={m.activity_none_yet()}>{m.activity_none_yet_body()}</EmptyState></div>
    {/if}
  </section>
</div>
