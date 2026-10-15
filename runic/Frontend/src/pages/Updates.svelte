<script lang="ts">
  import RefreshCwIcon from "@lucide/svelte/icons/refresh-cw";
  import ExternalLinkIcon from "@lucide/svelte/icons/external-link";
  import CircleCheckIcon from "@lucide/svelte/icons/circle-check";
  import CircleAlertIcon from "@lucide/svelte/icons/circle-alert";
  import InfoIcon from "@lucide/svelte/icons/info";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { actionLabel, m, stored } from "$lib/i18n";
  import { day, daysBetween, relative, shortRev } from "$lib/format";
  import EmptyState from "$lib/components/app/EmptyState.svelte";

  const checking = $derived(app.active?.request.action === "check");
  const check = () => app.run(app.request("check"));
  // The apps whose pins the configuration updates; names are not translated.
  const pins: Record<string, string> = { t3code: "T3 Code", opencode: "OpenCode", pince: "PINCE" };
  const pinName = (name: string) => pins[name] ?? name;
</script>

<div class="space-y-6">
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div>
      <h1 class="title">{m.nav_updates()}</h1>
      <p class="mt-1 text-muted-foreground">
        {#if app.updates}{m.updates_last_checked({ time: relative(app.updates.checkedAt, app.now) })}{:else}{m.updates_not_checked()}{/if}
      </p>
    </div>
    <Button variant="outline" disabled={app.busy || !app.connected} onclick={check}>
      <RefreshCwIcon class={checking ? "animate-spin" : ""} />
      {checking ? m.updates_checking() : m.updates_check_now()}
    </Button>
  </header>

  {#if app.updates?.errors.length}
    <div class="space-y-1 rounded-xl border border-destructive/30 bg-destructive/5 px-4 py-3">
      <p class="flex items-center gap-2 font-medium text-destructive"><CircleAlertIcon class="size-4" /> {m.updates_some_failed()}</p>
      {#each app.updates.errors as error, index (index)}<p class="text-xs break-words text-muted-foreground">{stored(error)}</p>{/each}
    </div>
  {/if}

  {#if !app.updates}
    <section class="panel">
      <EmptyState icon={RefreshCwIcon} title={m.updates_no_check()}>
        {m.updates_no_check_body()}
        {#snippet actions()}<Button disabled={app.busy || !app.connected} onclick={check}>{actionLabel("check")}</Button>{/snippet}
      </EmptyState>
    </section>
  {:else if !app.inputUpdates.length && !app.packageUpdates.length}
    <section class="panel">
      {#if app.updates.errors.length}
        <EmptyState icon={CircleCheckIcon} title={m.updates_none_found()}>{m.updates_none_found_body()}</EmptyState>
      {:else}
        <EmptyState icon={CircleCheckIcon} title={m.updates_up_to_date()}>{m.updates_up_to_date_body()}</EmptyState>
      {/if}
    </section>
  {/if}

  {#if app.inputUpdates.length || app.packageUpdates.length}
    <div class="flex gap-2 rounded-lg bg-muted/60 p-3 text-[0.83rem] text-muted-foreground">
      <InfoIcon class="mt-0.5 size-4 shrink-0" />
      <p>{m.placeholder_updates()}</p>
    </div>
  {/if}

  {#if app.inputUpdates.length}
    <section class="panel overflow-hidden" aria-labelledby="sources-heading">
      <div class="border-b px-4 py-3">
        <h2 id="sources-heading" class="heading">{m.updates_sources()}</h2>
        <p class="text-xs text-muted-foreground">{m.updates_sources_body()}</p>
      </div>
      <table class="w-full text-left" aria-labelledby="sources-heading">
        <thead class="text-xs text-muted-foreground">
          <tr class="border-b">
            <th class="py-2 pl-4 font-medium">{m.updates_source()}</th>
            <th class="py-2 font-medium">{m.updates_current()}</th>
            <th class="py-2 font-medium">{m.updates_latest()}</th>
            <th class="py-2 pr-4 text-right font-medium">{m.updates_behind()}</th>
          </tr>
        </thead>
        <tbody class="divide-y">
          {#each app.inputUpdates as item (item.name)}
            {@const behind = daysBetween(item.currentDate, item.latestDate)}
            <tr>
              <td class="py-2.5 pl-4">
                <p class="font-medium">{item.name}</p>
                {#if item.source}<p class="text-xs text-muted-foreground">{item.source}</p>{/if}
              </td>
              <td class="py-2.5">
                <p>{day(item.currentDate)}</p>
                <p class="font-mono text-xs text-muted-foreground">{shortRev(item.current)}</p>
              </td>
              <td class="py-2.5">
                <p>{day(item.latestDate)}</p>
                {#if item.url}
                  <a class="inline-flex items-center gap-1 font-mono text-xs text-primary hover:underline" href={item.url} target="_blank" rel="noreferrer" aria-label={m.updates_revision_link({ name: item.name, rev: shortRev(item.latest) })}>
                    {shortRev(item.latest)}<ExternalLinkIcon class="size-3" />
                  </a>
                {:else}<p class="font-mono text-xs text-muted-foreground">{shortRev(item.latest)}</p>{/if}
              </td>
              <td class="py-2.5 pr-4 text-right tabular-nums {behind && behind > 30 ? 'text-warning' : 'text-muted-foreground'}">
                {behind === undefined ? "—" : behind <= 0 ? m.updates_less_than_a_day() : m.updates_days({ count: behind })}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </section>
  {/if}

  {#if app.packageUpdates.length}
    <section class="panel overflow-hidden" aria-labelledby="pins-heading">
      <div class="border-b px-4 py-3">
        <h2 id="pins-heading" class="heading">{m.updates_pinned()}</h2>
        <p class="text-xs text-muted-foreground">{m.updates_pinned_body()}</p>
      </div>
      <ul role="list" aria-labelledby="pins-heading" class="divide-y">
        {#each app.packageUpdates as item (item.name)}
          <li class="flex items-center gap-4 px-4 py-3">
            <div class="min-w-0 flex-1">
              <p class="font-medium">{pinName(item.name)}</p>
              <p class="text-xs text-muted-foreground">
                {item.current} → <span class="font-medium text-foreground">{item.latest}</span>
                {#if item.source}<span class="ml-2">{item.source}</span>{/if}
              </p>
            </div>
            {#if item.url}
              <Button variant="ghost" size="sm" href={item.url} target="_blank" rel="noreferrer" aria-label={m.updates_release_notes_of({ name: item.name })}>{m.updates_release_notes()} <ExternalLinkIcon /></Button>
            {/if}
          </li>
        {/each}
      </ul>
    </section>
  {/if}
</div>
