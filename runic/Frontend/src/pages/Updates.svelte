<script lang="ts">
  import RefreshCwIcon from "@lucide/svelte/icons/refresh-cw";
  import ExternalLinkIcon from "@lucide/svelte/icons/external-link";
  import CircleCheckIcon from "@lucide/svelte/icons/circle-check";
  import CircleAlertIcon from "@lucide/svelte/icons/circle-alert";
  import DownloadIcon from "@lucide/svelte/icons/download";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { actionLabel, m, text } from "$lib/i18n";
  import { day, daysBetween, relative, shortRev } from "$lib/format";
  import type { Action, Scope } from "$lib/requests";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import TechName from "$lib/components/app/TechName.svelte";

  let selected = $state<string[]>([]);
  let scope = $state<Scope>("all");
  // What follows preparing the selected sources: a review first, or applying them now or on the next boot.
  let then = $state<"review" | "switch" | "boot">("review");
  let everything = $state(false);
  const checking = $derived(app.active?.request.action === "check");
  const names = $derived(app.inputUpdates.map((item) => item.name));
  const all = $derived(names.length > 0 && names.every((name) => selected.includes(name)));

  // Drop selections for inputs that are no longer outdated.
  $effect(() => {
    const keep = selected.filter((name) => names.includes(name));
    if (keep.length !== selected.length) selected = keep;
  });

  // Applying on the next boot needs the system configuration.
  $effect(() => {
    if (scope === "home" && then === "boot") then = "review";
  });

  const check = () => app.run(app.request("check"));
  // Changes prepared for another computer are not applied here; they wait for a review.
  const prepare = () =>
    app.prepare(scope, { ...(all ? { update: true } : { inputs: [...selected] }), then: then === "review" || app.foreignHost ? null : then });

  // The apps whose pins the configuration updates; names are not translated.
  const pins: Record<string, string> = { t3code: "T3 Code", opencode: "OpenCode", pince: "PINCE" };
  const pinName = (name: string) => pins[name] ?? name;
  const pinAction = (name: string) => `${name}-update` as Action;
  const updating = (name: string) => app.active?.request.action === pinAction(name);
  const queued = (name: string) => app.queue.some((entry) => !entry.held && entry.request.action === pinAction(name));
  // A pin whose update runs already is not updated again.
  const pinUpdates = $derived(app.packageUpdates.filter((item) => !updating(item.name)));

  type Channel = "stable" | "nightly";
  const channelLabel = (channel: string) => (channel === "nightly" ? m.updates_channel_nightly() : m.updates_channel_stable());

  // Every pin with its update, once a check has listed the pins; reports of earlier versions list only the updates.
  const pinRows = $derived(
    app.updates?.pins.length
      ? app.updates.pins.map((pin) => ({ name: pin.name, pin, item: app.packageUpdates.find((item) => item.name === pin.name) }))
      : app.packageUpdates.map((item) => ({ name: item.name, pin: undefined, item })),
  );

  function switchChannel(name: string, channel: Channel) {
    const label = channelLabel(channel);
    void app.confirm({
      title: m.updates_channel_title({ name: pinName(name), channel: label }),
      description: m.updates_channel_description({ channel: label }),
      request: app.request(pinAction(name), { scope: "home", channel }),
      confirmLabel: m.updates_channel_switch(),
      points: [m.updates_pin_point_configuration(), m.updates_pin_point_nothing_yet()],
    });
  }

  function updatePin(name: string, latest: string) {
    void app.confirm({
      title: m.updates_pin_title({ name: pinName(name), version: latest }),
      description: m.updates_pin_description(),
      request: app.request(pinAction(name), { scope: "home" }),
      confirmLabel: m.updates_update_pin(),
      points: [m.updates_pin_point_configuration(), m.updates_pin_point_nothing_yet()],
    });
  }

  /** Updates each pin, then prepares every source's update for your user and system, in that order. */
  function updateEverything(then: "switch" | null) {
    everything = false;
    void app.runAll([
      ...pinUpdates.map((item) => app.request(pinAction(item.name), { scope: "home" })),
      app.request("review-build", { scope: "all", update: true, then }),
    ]);
  }
</script>

<div class="space-y-6">
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div>
      <h1 class="title">{m.nav_updates()}</h1>
      <p class="mt-1 text-muted-foreground">
        {#if app.updates}{m.updates_last_checked({ time: relative(app.updates.checkedAt, app.now) })}{:else}{m.updates_not_checked()}{/if}
      </p>
    </div>
    <div class="flex flex-wrap gap-2">
      <Button variant="outline" disabled={checking || !app.connected} onclick={check}>
        <RefreshCwIcon class={checking ? "animate-spin" : ""} />
        {checking ? m.updates_checking() : m.updates_check_now()}
      </Button>
      {#if app.inputUpdates.length || app.packageUpdates.length}
        <Button disabled={!app.connected} onclick={() => (everything = true)}><DownloadIcon /> {m.updates_everything_ellipsis()}</Button>
      {/if}
    </div>
  </header>

  {#if app.updates?.errors.length}
    <div class="space-y-1 rounded-xl border border-destructive/30 bg-destructive/5 px-4 py-3">
      <p class="flex items-center gap-2 font-medium text-destructive"><CircleAlertIcon class="size-4" /> {m.updates_some_failed()}</p>
      {#each app.updates.errors as error, index (index)}<p class="text-xs break-words text-muted-foreground">{text(error)}</p>{/each}
    </div>
  {/if}

  {#if !app.updates}
    <section class="panel">
      <EmptyState icon={RefreshCwIcon} title={m.updates_no_check()}>
        {m.updates_no_check_body()}
        {#snippet actions()}<Button disabled={checking || !app.connected} onclick={check}>{actionLabel("check")}</Button>{/snippet}
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

  {#if app.inputUpdates.length}
    <section class="panel overflow-hidden" aria-labelledby="sources-heading">
      <div class="flex flex-wrap items-center gap-3 border-b px-4 py-3">
        <div class="flex-1">
          <h2 id="sources-heading" class="heading">{m.updates_sources()}<TechName name="flake inputs" /></h2>
          <p class="text-xs text-muted-foreground">{m.updates_sources_body()}</p>
        </div>
        <Segmented
          label={m.updates_what_to_prepare()}
          bind:value={scope}
          options={[{ value: "home", label: m.scope_home() }, { value: "system", label: m.scope_system() }, { value: "all", label: m.updates_both() }]}
        />
        {#if !app.foreignHost}
          <Segmented
            label={m.updates_after_preparing()}
            bind:value={then}
            options={[
              { value: "review", label: m.updates_then_review() },
              { value: "switch", label: m.updates_then_apply_now() },
              ...(scope === "home" ? [] : [{ value: "boot" as const, label: m.updates_then_apply_on_boot() }]),
            ]}
          />
        {/if}
        <Button disabled={!selected.length || !app.connected} onclick={prepare}>
          {selected.length ? m.updates_prepare_count({ count: selected.length }) : m.updates_prepare()}
        </Button>
      </div>
      <table class="w-full text-left" aria-labelledby="sources-heading">
        <thead class="text-xs text-muted-foreground">
          <tr class="border-b">
            <th class="w-10 py-2 pl-4">
              <input
                type="checkbox"
                aria-label={m.updates_select_all()}
                class="accent-primary"
                checked={all}
                indeterminate={selected.length > 0 && !all}
                onchange={() => (selected = all ? [] : [...names])}
              />
            </th>
            <th class="py-2 font-medium">{m.updates_source()}</th>
            <th class="py-2 font-medium">{m.updates_current()}</th>
            <th class="py-2 font-medium">{m.updates_latest()}</th>
            <th class="py-2 pr-4 text-right font-medium">{m.updates_behind()}</th>
          </tr>
        </thead>
        <tbody class="divide-y">
          {#each app.inputUpdates as item (item.name)}
            {@const behind = daysBetween(item.currentDate, item.latestDate)}
            <tr class="hover:bg-muted/30">
              <td class="py-2.5 pl-4"><input type="checkbox" class="accent-primary" aria-label={m.updates_select({ name: item.name })} bind:group={selected} value={item.name} /></td>
              <td class="py-2.5">
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

  {#if pinRows.length}
    <section class="panel overflow-hidden" aria-labelledby="pins-heading">
      <div class="border-b px-4 py-3">
        <h2 id="pins-heading" class="heading">{m.updates_pinned()}</h2>
        <p class="text-xs text-muted-foreground">{m.updates_pinned_body()}</p>
      </div>
      <ul role="list" aria-labelledby="pins-heading" class="divide-y">
        {#each pinRows as { name, pin, item } (name)}
          <li class="flex flex-wrap items-center gap-4 px-4 py-3">
            <div class="min-w-0 flex-1">
              <p class="font-medium">{pinName(name)}</p>
              <p class="text-xs text-muted-foreground">
                {#if item}
                  {item.current} → <span class="font-medium text-foreground">{item.latest}</span>
                  {#if item.source}<span class="ml-2">{item.source}</span>{/if}
                {:else if pin}
                  {m.updates_pin_up_to_date({ version: pin.version, channel: channelLabel(pin.channel) })}
                {/if}
              </p>
            </div>
            {#if pin && pin.channels.length > 1}
              <Segmented
                label={m.updates_channel_of({ name: pinName(name) })}
                bind:value={() => pin.channel as Channel, (channel: Channel) => {
                  if (channel !== pin.channel && app.connected) switchChannel(name, channel);
                }}
                options={pin.channels.map((channel) => ({ value: channel as Channel, label: channelLabel(channel), disabled: !app.connected }))}
              />
            {/if}
            {#if item?.url}
              <Button variant="ghost" size="sm" href={item.url} target="_blank" rel="noreferrer" aria-label={m.updates_release_notes_of({ name: pinName(name) })}>{m.updates_release_notes()} <ExternalLinkIcon /></Button>
            {/if}
            {#if item && updating(name)}
              <span class="text-xs text-muted-foreground">{m.updates_updating()}</span>
            {:else if item && queued(name)}
              <span class="text-xs text-muted-foreground">{m.updates_queued()}</span>
            {:else if item}
              <Button variant="outline" size="sm" aria-label={m.updates_update_name({ name: pinName(name) })} disabled={!app.connected} onclick={() => updatePin(name, item.latest)}>{m.updates_update()}</Button>
            {/if}
          </li>
        {/each}
      </ul>
    </section>
  {/if}
</div>

<Dialog.Root open={everything} onOpenChange={(open) => (everything = open)}>
  <Dialog.Content class="sm:max-w-lg">
    <Dialog.Header>
      <Dialog.Title>{m.updates_everything_title()}</Dialog.Title>
      <!-- The steps belong to the description, so screen readers announce what confirming does when the dialog opens. -->
      <Dialog.Description>
        {#snippet child({ props })}
          <div {...props}>
            <p>{m.updates_everything_description()}</p>
            <ol role="list" class="mt-4 space-y-1.5 text-foreground">
              {#each pinUpdates as item (item.name)}
                <li class="flex gap-2">
                  <span class="mt-1.5 size-1.5 shrink-0 rounded-full bg-primary"></span>{m.updates_everything_pin({ name: pinName(item.name), version: item.latest })}
                </li>
              {/each}
              <li class="flex gap-2"><span class="mt-1.5 size-1.5 shrink-0 rounded-full bg-primary"></span>{m.updates_everything_sources()}</li>
              <li class="flex gap-2">
                <span class="mt-1.5 size-1.5 shrink-0 rounded-full bg-primary"></span>{app.foreignHost ? m.updates_everything_prepare_review() : m.updates_everything_prepare()}
              </li>
            </ol>
            <p class="mt-4">{m.updates_everything_waits()}</p>
          </div>
        {/snippet}
      </Dialog.Description>
    </Dialog.Header>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (everything = false)}>{m.common_cancel()}</Button>
      <Button variant={app.foreignHost ? "default" : "outline"} disabled={!app.connected} onclick={() => updateEverything(null)}>{m.updates_everything_review()}</Button>
      {#if !app.foreignHost}
        <Button disabled={!app.connected} onclick={() => updateEverything("switch")}>{m.updates_everything_apply()}</Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
