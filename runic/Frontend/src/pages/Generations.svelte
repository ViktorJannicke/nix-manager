<script lang="ts">
  import HistoryIcon from "@lucide/svelte/icons/history";
  import ScrollTextIcon from "@lucide/svelte/icons/scroll-text";
  import { Button } from "$lib/components/ui/button";
  import type { Generation } from "../generated/types.js";
  import { app } from "$lib/app.svelte";
  import { m, slot } from "$lib/i18n";
  import { bytes, dateTime, removedGenerations } from "$lib/format";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import TechName from "$lib/components/app/TechName.svelte";
  import Markup from "$lib/components/app/Markup.svelte";
  import GenerationList from "$lib/components/app/GenerationList.svelte";
  import ChangeHistory from "$lib/components/app/ChangeHistory.svelte";
  import RetentionField from "$lib/components/app/RetentionField.svelte";

  let profile = $state<"system" | "home">("home");
  let retention = $state(3);
  let cleanupScope = $state<"home" | "all">("home");

  const systemView = $derived(app.systemView);
  const generations = $derived(app.system?.generations);
  const list = $derived(generations?.[profile]);
  const history = $derived(app.reviewsView?.state?.changeHistory);
  const removable = (key: "home" | "system") => removedGenerations(generations?.[key] ?? [], retention).length;
  const removeHome = $derived(removable("home"));
  const removeSystem = $derived(cleanupScope === "all" ? removable("system") : 0);
  const removed = $derived(removeHome + removeSystem);

  // The backend measures what the cleanup frees for the scope and retention it was given. It answers again when
  // the choice or the restore points change, as /api/cleanup-estimate did; a new observation with the same
  // restore points keeps the answer.
  const key = $derived(JSON.stringify([cleanupScope, retention, generations?.home.map((g) => g.output), generations?.system.map((g) => g.output)]));
  let measured = $state<{ key: string; bytes: number }>();
  let asked: { key: string; scope: string; retention: number } | undefined;
  const estimate = $derived(measured?.key === key ? measured.bytes : undefined);
  $effect(() => {
    const state = systemView?.state;
    if (state?.estimate && asked && state.cleanupScope === asked.scope && state.retention === asked.retention)
      measured = { key: asked.key, bytes: Number(state.estimate.bytes) };
  });
  $effect(() => {
    const client = systemView?.client;
    const request = { key, scope: cleanupScope, retention };
    if (!client || !app.system || !removed || measured?.key === key) return;
    // Waits for the choice to settle, as the Bun version did.
    const timer = setTimeout(() => {
      asked = request;
      void (async () => {
        await client.setCleanupScope(request.scope);
        await client.setRetention(request.retention);
        await client.estimateCleanup();
      })().catch(() => {});
    }, 250);
    return () => clearTimeout(timer);
  });

  function goBack(generation: Generation) {
    const home = profile === "home";
    void app.confirm({
      title: home ? m.restore_go_back_home_title({ generation: String(generation.number) }) : m.restore_go_back_system_title({ generation: String(generation.number) }),
      description: m.restore_go_back_description({ date: dateTime(generation.createdAt) }),
      request: app.request("review-build", { scope: profile, generation: generation.number }),
      confirmLabel: m.common_prepare(),
      points: [m.restore_go_back_builds(), m.restore_go_back_nothing_yet()],
    });
  }

  function cleanup() {
    const points = [
      m.restore_cleanup_keeps({ count: retention, scope: cleanupScope === "all" ? m.restore_scope_user_and_system() : m.scope_home_text() }),
    ];
    if (removeHome) points.push(m.restore_cleanup_removes_home({ count: removeHome }));
    if (cleanupScope === "all") points.push(m.restore_cleanup_removes_system({ count: removeSystem }));
    if (estimate) points.push(m.restore_cleanup_frees({ size: bytes(estimate) }));
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
      <h1 class="title">{m.nav_restore_points()}<TechName name="generations" /></h1>
      <p class="mt-1 text-muted-foreground">{m.restore_intro()}</p>
    </div>
    <Segmented
      label={m.restore_profile()}
      bind:value={profile}
      options={[
        { value: "home", label: m.scope_home(), count: generations?.home.length },
        { value: "system", label: m.scope_system(), count: generations?.system.length },
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
      <p class="px-4 py-6 text-center text-muted-foreground" role="status">{systemView?.state?.error ? m.restore_could_not_read() : m.common_loading()}</p>
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
      {#if !removed}
        {m.restore_cleanup_removes_none()}
      {:else if estimate === undefined}
        {m.restore_cleanup_measuring({ count: removed })}
      {:else}
        <Markup text={estimate ? m.restore_cleanup_freeing({ count: removed, size: slot("size"), note: "" }) : m.restore_cleanup_freeing({ count: removed, size: slot("size"), note: ` ${m.restore_cleanup_shared()}` })}>
          {#snippet children()}<span class="font-medium text-foreground">{bytes(estimate)}</span>{/snippet}
        </Markup>
      {/if}
      {m.restore_cleanup_automatic()}
    </p>
  </section>
</div>
