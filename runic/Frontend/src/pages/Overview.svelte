<script lang="ts">
  import CloudDownloadIcon from "@lucide/svelte/icons/cloud-download";
  import InfoIcon from "@lucide/svelte/icons/info";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { actionLabel, m, stored } from "$lib/i18n";
  import { duration, relative, remaining, scopeText } from "$lib/format";
  import BuildProgress from "$lib/components/app/BuildProgress.svelte";
  import StatusBadge from "$lib/components/app/StatusBadge.svelte";

  const running = $derived(app.active);
  const runningStep = $derived(running?.steps?.find((step) => step.status === "running"));
  const left = $derived(running && remaining(app.history, running, app.now));
  const updates = $derived(app.inputUpdates.length + app.packageUpdates.length);
  const recent = $derived(app.history.slice(0, 5));
  const host = $derived(app.config?.hosts.find((item) => item.name === app.host));
</script>

<div class="space-y-8">
  <header class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 class="title">{app.host || m.overview_this_computer()}</h1>
      {#if host}<p class="mt-1 text-muted-foreground">{host.system}</p>{/if}
    </div>
    {#if updates}
      <Button variant="outline" onclick={() => app.navigate("updates")}>
        <CloudDownloadIcon />
        {m.updates_available({ count: updates })}
      </Button>
    {/if}
  </header>

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

    <aside class="space-y-5">
      <div>
        <h2 class="label">{m.overview_configuration()}</h2>
        <p class="mt-1 font-mono text-[0.83rem] break-all">{app.config?.flake}</p>
      </div>
      <div class="flex gap-2 rounded-lg bg-muted/60 p-3 text-[0.83rem] text-muted-foreground">
        <InfoIcon class="mt-0.5 size-4 shrink-0" />
        <p>{m.placeholder_overview()}</p>
      </div>
    </aside>
  </div>
</div>
