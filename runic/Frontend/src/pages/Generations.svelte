<script lang="ts">
  import HistoryIcon from "@lucide/svelte/icons/history";
  import ScrollTextIcon from "@lucide/svelte/icons/scroll-text";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { m } from "$lib/i18n";
  import type { Generation, HistoryEntry } from "$lib/restore-points";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import GenerationList from "$lib/components/app/GenerationList.svelte";
  import ChangeHistory from "$lib/components/app/ChangeHistory.svelte";
  import RetentionField from "$lib/components/app/RetentionField.svelte";

  let profile = $state<"system" | "home">("home");
  let retention = $state(3);
  let cleanupScope = $state<"home" | "all">("home");

  // Observing generations and the configuration's history is part of the remaining backend (W300-033); until
  // then both lists are unknown, and going back to one is prepared for review there too.
  const observed: { home?: readonly Generation[]; system?: readonly Generation[]; history?: readonly HistoryEntry[] } = {};
  const generations = { home: observed.home, system: observed.system };
  const history = observed.history;
  const list = $derived(generations[profile]);
  const goBack = (_generation: Generation) => {};

  function cleanup() {
    const scope = cleanupScope === "all" ? m.scope_all_text() : m.scope_home_text();
    const points = [m.restore_cleanup_keeps({ count: retention, scope })];
    if (cleanupScope === "all") points.push(m.restore_cleanup_system_password());
    points.push(m.restore_cleanup_no_going_back());
    void app.confirm({
      title: m.restore_cleanup_title(),
      description: m.restore_cleanup_description(),
      request: app.request("gc", { scope: cleanupScope, retention }),
      confirmLabel: m.restore_clean_up(),
      destructive: true,
      points,
    });
  }
</script>

<div class="space-y-6">
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div>
      <h1 class="title">{m.nav_restore_points()}</h1>
      <p class="mt-1 text-muted-foreground">{m.restore_intro()}</p>
    </div>
    <Segmented
      label={m.restore_profile()}
      bind:value={profile}
      options={[
        { value: "home", label: m.scope_home(), count: generations.home?.length },
        { value: "system", label: m.scope_system(), count: generations.system?.length },
      ]}
    />
  </header>

  <section class="panel overflow-hidden" aria-labelledby="restore-points-heading">
    <h2 id="restore-points-heading" class="sr-only">{profile === "home" ? m.restore_list_home() : m.restore_list_system()}</h2>
    {#if list?.length}
      <GenerationList
        generations={list}
        {profile}
        label={profile === "home" ? m.restore_list_home() : m.restore_list_system()}
        now={app.now}
        disabled={app.busy || !app.connected}
        onGoBack={goBack}
      />
    {:else if list}
      <EmptyState icon={HistoryIcon} title={m.restore_none()}>
        {profile === "home" ? m.restore_none_home() : m.restore_none_system()}
      </EmptyState>
    {:else}
      <EmptyState icon={HistoryIcon} title={m.placeholder_restore_points_title()}>{m.placeholder_restore_points()}</EmptyState>
    {/if}
  </section>

  <section class="panel overflow-hidden" aria-labelledby="history-heading">
    <div class="border-b px-4 py-3">
      <h2 id="history-heading" class="heading">{m.restore_history()}</h2>
      <p class="text-muted-foreground">{m.restore_history_intro()}</p>
    </div>
    {#if history?.length}
      <ChangeHistory entries={history} labelledby="history-heading" now={app.now} />
    {:else if history}
      <EmptyState icon={ScrollTextIcon} title={m.restore_no_history()}>{m.restore_no_history_body()}</EmptyState>
    {:else}
      <EmptyState icon={ScrollTextIcon} title={m.placeholder_history_title()}>{m.placeholder_history()}</EmptyState>
    {/if}
  </section>

  <section class="panel p-5" aria-labelledby="cleanup-heading">
    <div class="flex flex-wrap items-center gap-6">
      <div class="min-w-60 flex-1">
        <h2 id="cleanup-heading" class="heading">{m.restore_clean_up()}</h2>
        <p class="text-muted-foreground">{m.restore_cleanup_intro()}</p>
      </div>
      <RetentionField bind:value={retention} />
      <Segmented label={m.restore_what_to_clean_up()} bind:value={cleanupScope} options={[{ value: "home", label: m.scope_home() }, { value: "all", label: m.scope_all() }]} />
      <Button variant="outline" disabled={app.busy || !app.connected} onclick={cleanup}>{m.restore_clean_up_ellipsis()}</Button>
    </div>
    <p class="mt-3 text-xs text-muted-foreground">
      {m.restore_cleanup_keeps({ count: retention, scope: cleanupScope === "all" ? m.scope_all_text() : m.scope_home_text() })}
    </p>
  </section>
</div>
