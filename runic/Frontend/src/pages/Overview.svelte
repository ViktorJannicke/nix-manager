<script lang="ts">
  import RotateCwIcon from "@lucide/svelte/icons/rotate-cw";
  import CloudDownloadIcon from "@lucide/svelte/icons/cloud-download";
  import PencilLineIcon from "@lucide/svelte/icons/pencil-line";
  import Undo2Icon from "@lucide/svelte/icons/undo-2";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { actionLabel, m, plain, stored } from "$lib/i18n";
  import { describeChange } from "$lib/changes";
  import { bytes, duration, relative, remaining, scopeText, shortRev } from "$lib/format";
  import BuildProgress from "$lib/components/app/BuildProgress.svelte";
  import StatusBadge from "$lib/components/app/StatusBadge.svelte";
  import ProgressBar from "$lib/components/app/ProgressBar.svelte";
  import TechName from "$lib/components/app/TechName.svelte";
  import GenerationRail from "$lib/components/app/GenerationRail.svelte";
  import PendingButton from "$lib/components/app/PendingButton.svelte";

  const system = $derived(app.system);
  const running = $derived(app.active);
  const runningStep = $derived(running?.steps?.find((step) => step.status === "running"));
  const left = $derived(running && remaining(app.history, running, app.now));
  const updates = $derived(app.inputUpdates.length + app.packageUpdates.length);
  const recent = $derived(app.history.slice(0, 5));
  const repo = $derived(system?.repository);
  const disk = $derived(system?.disk);
  const draft = $derived(app.draft);
  const last = $derived(app.lastChange);
  const undoing = $derived(Boolean(last && running?.request.undo === last.operation.id));
  const used = $derived(disk && Number(disk.total) ? 1 - Number(disk.free) / Number(disk.total) : undefined);
</script>

<div class="space-y-8">
  <header class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 class="title">{app.host || m.overview_this_computer()}</h1>
      <p class="mt-1 text-muted-foreground">
        {[system?.nixosVersion && `NixOS ${system.nixosVersion}`, system?.kernel.current && `Linux ${system.kernel.current}`]
          .filter(Boolean)
          .join(", ")}
      </p>
    </div>
    {#if updates}
      <Button variant="outline" onclick={() => app.navigate("updates")}>
        <CloudDownloadIcon />
        {m.updates_available({ count: updates })}
      </Button>
    {/if}
  </header>

  {#if system?.rebootRequired}
    <div class="flex items-center gap-3 rounded-xl bg-warning/12 px-4 py-3">
      <RotateCwIcon class="size-4 shrink-0 text-warning" />
      <p>
        <span class="font-semibold">{m.overview_restart_to_finish()}</span>
        {m.overview_restart_kernel({ booted: system.kernel.booted ?? m.format_unknown(), current: system.kernel.current ?? m.format_unknown() })}
      </p>
    </div>
  {/if}

  {#if draft?.changes.length}
    <section class="prepared flex flex-wrap items-start gap-4 p-4" aria-labelledby="pending-overview">
      <PencilLineIcon class="mt-0.5 size-4 shrink-0 text-prepared-foreground" />
      <div class="min-w-0 flex-1">
        <h2 id="pending-overview" class="font-semibold">{m.overview_pending_edits({ count: draft.changes.length })}</h2>
        <ul role="list" aria-labelledby="pending-overview" class="mt-1 text-muted-foreground">
          {#each draft.changes.slice(0, 3) as change, index (index)}<li class="truncate">{describeChange(change).title}</li>{/each}
          {#if draft.changes.length > 3}<li>{m.common_and_more({ count: draft.changes.length - 3 })}</li>{/if}
        </ul>
        {#if draft.problems.length}
          <p class="mt-1 text-xs text-destructive">{m.overview_edits_cannot_apply({ count: draft.problems.length })}</p>
        {/if}
      </div>
      <div class="flex gap-2">
        <Button variant="ghost" size="sm" onclick={() => app.navigate("software")}>{m.overview_show_edits()}</Button>
        <PendingButton />
      </div>
    </section>
  {/if}

  {#if last}
    <section class="panel flex flex-wrap items-start gap-4 p-4" aria-labelledby="last-change">
      <Undo2Icon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
      <div class="min-w-0 flex-1">
        <h2 id="last-change" class="font-semibold">{m.overview_last_change({ when: relative(last.operation.finishedAt ?? last.operation.startedAt, app.now) })}</h2>
        <ul role="list" aria-labelledby="last-change" class="mt-1 text-muted-foreground">
          {#each last.undo.changes.slice(0, 3) as change, index (index)}<li class="truncate">{stored(change)}</li>{/each}
          {#if last.undo.changes.length > 3}<li>{m.common_and_more({ count: last.undo.changes.length - 3 })}</li>{/if}
        </ul>
        <p class="mt-1 text-xs text-muted-foreground">
          {m.overview_undo_explanation({
            what: last.operation.request.scope === "home" ? m.overview_undo_your_setup() : m.overview_undo_this_computer(),
            files: last.undo.files.length ? m.overview_undo_files_included() : "",
          })}
        </p>
      </div>
      <Button variant="outline" size="sm" aria-describedby="last-change" disabled={app.busy || !app.connected} onclick={() => app.undo(last.operation)}>
        {undoing ? m.common_preparing() : m.common_undo()}
      </Button>
    </section>
  {/if}

  <section class="panel space-y-5 p-5" aria-labelledby="generations-heading">
    <div class="flex items-baseline justify-between gap-4">
      <h2 id="generations-heading" class="heading">{plain(m.nav_restore_points())}<TechName name="generations" /></h2>
      {#if !running && !app.reviews.some((r) => r.host === app.host)}
        <button class="focus-ring rounded text-primary hover:underline disabled:opacity-50" disabled={!app.connected} onclick={() => app.prepare("all")}>
          {m.overview_prepare_both()}
        </button>
      {/if}
    </div>
    <GenerationRail profile="home" title={m.scope_home()} />
    <GenerationRail profile="system" title={m.scope_system()} />
  </section>

  {#if running}
    <section class="panel space-y-4 p-5" aria-labelledby="running-heading">
      <div class="flex items-start justify-between gap-4">
        <div>
          <h2 id="running-heading" class="heading">{actionLabel(running.request.action)}</h2>
          <p class="text-muted-foreground">{m.overview_step_so_far({ step: stored(runningStep?.label ?? running.step), duration: duration(running, app.now) })}</p>
          {#if left}<p class="text-xs text-muted-foreground">{left}</p>{/if}
        </div>
        <div class="flex gap-2">
          <Button variant="outline" size="sm" onclick={() => app.show(running.id)}>{m.overview_show_output()}</Button>
          <Button variant="ghost" size="sm" disabled={Boolean(running.finishingPrivilegedStep)} onclick={() => app.cancel()}>{m.common_cancel()}</Button>
        </div>
      </div>
      {#if runningStep}<BuildProgress progress={runningStep.progress} />{/if}
    </section>
  {/if}

  <div class="grid gap-8 lg:grid-cols-[1fr_17rem]">
    <section aria-labelledby="recent-heading">
      <div class="mb-2 flex items-baseline justify-between">
        <h2 id="recent-heading" class="heading">{m.overview_recent_activity()}</h2>
        <button class="focus-ring rounded text-primary hover:underline" onclick={() => app.navigate("activity")}>{m.overview_all_activity()}</button>
      </div>
      {#if recent.length}
        <ul role="list" aria-labelledby="recent-heading" class="divide-y border-y">
          {#each recent as operation (operation.id)}
            <li>
              <button class="focus-ring flex w-full items-center gap-3 px-1 py-2.5 text-left hover:bg-card/70" onclick={() => app.show(operation.id)}>
                <span class="min-w-0 flex-1 truncate">
                  <span class="font-medium">{actionLabel(operation.request.action)}</span>
                  <span class="text-muted-foreground">{scopeText(operation.request.scope)}</span>
                </span>
                <span class="text-[0.83rem] text-muted-foreground">{relative(operation.startedAt, app.now)}</span>
                <StatusBadge status={operation.status} />
              </button>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="border-y py-6 text-center text-muted-foreground">{m.overview_nothing_yet()}</p>
      {/if}
    </section>

    <aside class="space-y-5" aria-label={m.overview_state()}>
      <div>
        <h2 class="label">{m.overview_configuration()}</h2>
        {#if repo}
          <p class="mt-1 font-semibold">{repo.branch ?? m.overview_detached()} <code class="font-normal text-muted-foreground">{shortRev(repo.revision)}</code></p>
          {#if repo.subject}<p class="truncate text-muted-foreground" title={repo.subject}>{repo.subject}</p>{/if}
          <p class="mt-1 text-[0.83rem] {repo.clean ? 'text-muted-foreground' : 'text-warning'}">
            {repo.clean ? m.overview_everything_saved() : m.overview_unsaved_changes({ count: repo.changes.length })}{repo.lockModified ? m.overview_sources_edited() : ""}
          </p>
        {:else}<p class="mt-1 text-muted-foreground">{m.overview_reading()}</p>{/if}
      </div>
      <div>
        <h2 class="label">{m.overview_disk_space()}</h2>
        {#if disk && used !== undefined}
          <p class="mt-1 font-semibold">{m.overview_free({ size: bytes(disk.free) })} <span class="font-normal text-muted-foreground">{m.overview_of_total({ total: bytes(disk.total) })}</span></p>
          <ProgressBar class="mt-2" value={used} label={m.overview_disk_used()} />
          {#if used > 0.9}
            <button class="focus-ring mt-2 rounded text-[0.83rem] text-primary hover:underline" onclick={() => app.navigate("generations")}>{m.overview_free_space()}</button>
          {/if}
        {:else}<p class="mt-1 text-muted-foreground">{m.overview_unknown()}</p>{/if}
      </div>
    </aside>
  </div>
</div>
