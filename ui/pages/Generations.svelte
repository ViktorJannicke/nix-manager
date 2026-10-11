<script lang="ts">
  import MinusIcon from "@lucide/svelte/icons/minus";
  import PlusIcon from "@lucide/svelte/icons/plus";
  import HistoryIcon from "@lucide/svelte/icons/history";
  import ScrollTextIcon from "@lucide/svelte/icons/scroll-text";
  import type { HistoryEntry } from "../../src/history";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { managerApi } from "$lib/api";
  import { bytes, dateTime, plural, relative, removedGenerations } from "$lib/format";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";

  let profile = $state<"system" | "home">("home");
  let retention = $state(3);
  let cleanupScope = $state<"home" | "all">("home");

  const list = $derived(app.system?.generations[profile] ?? []);
  const sorted = $derived([...list].sort((a, b) => b.number - a.number));
  const removable = (key: "home" | "system") => removedGenerations(app.system?.generations[key] ?? [], retention).length;
  const removeHome = $derived(removable("home"));
  const removeSystem = $derived(cleanupScope === "all" ? removable("system") : 0);

  // The server measures what the cleanup frees; it answers again when the choice or the generations change.
  const estimateKey = $derived(
    JSON.stringify([cleanupScope, retention, app.system?.generations.home.map((g) => g.output), app.system?.generations.system.map((g) => g.output)]),
  );
  let estimate = $state<{ key: string; bytes: number }>();
  const freed = $derived(estimate?.key === estimateKey ? estimate.bytes : undefined);
  $effect(() => {
    const key = estimateKey;
    const [scope, keep] = [cleanupScope, retention];
    if (!app.connected || !app.system || !(removeHome + removeSystem)) return;
    const timer = setTimeout(() => {
      managerApi
        .cleanupEstimate(scope, keep)
        .then((result) => (estimate = { key, bytes: result.bytes }))
        .catch(() => {});
    }, 250);
    return () => clearTimeout(timer);
  });

  // The configuration's history is read again whenever its last commit changes.
  let history = $state<HistoryEntry[]>();
  $effect(() => {
    void app.system?.repository.revision;
    if (!app.connected) return;
    managerApi
      .history()
      .then((entries) => (history = entries))
      .catch(() => (history = []));
  });

  function cleanup() {
    const points = [`Keeps the newest ${plural(retention, "generation")} of each selected profile and the active one.`];
    if (removeHome) points.push(`Removes ${plural(removeHome, "home generation")}.`);
    if (cleanupScope === "all") points.push(`Removes ${plural(removeSystem, "system generation")} and their boot entries; you'll be asked for your password.`);
    if (freed) points.push(`Frees about ${bytes(freed)}, and more if other unused files are found.`);
    points.push("Deletes unreferenced store paths. Removed generations can't be restored.");
    app.confirm({
      title: "Clean up old generations?",
      description: "Frees disk space by deleting old configurations you can no longer roll back to.",
      request: app.request("gc", { scope: cleanupScope, retention }),
      confirmLabel: "Clean up",
      destructive: true,
      points,
    });
  }
</script>

<div class="space-y-6">
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div>
      <h1 class="title">Generations</h1>
      <p class="mt-1 text-muted-foreground">Every activation creates a generation you can return to.</p>
    </div>
    <Segmented
      label="Profile"
      bind:value={profile}
      options={[
        { value: "home", label: "Home", count: app.system?.generations.home.length },
        { value: "system", label: "System", count: app.system?.generations.system.length },
      ]}
    />
  </header>

  <section class="panel overflow-hidden">
    {#if sorted.length}
      <ul class="divide-y">
        {#each sorted as generation (generation.number)}
          <li class="flex items-center gap-4 px-4 py-3 {generation.current ? 'bg-primary/5' : ''}">
            <span class="w-12 font-mono text-muted-foreground tabular-nums">#{generation.number}</span>
            <div class="min-w-0 flex-1">
              <p class="font-medium">
                {dateTime(generation.createdAt)}
                <span class="ml-1 font-normal text-muted-foreground">{relative(generation.createdAt, app.now)}</span>
              </p>
              {#if generation.version || generation.kernel}
                <p class="text-xs text-muted-foreground">
                  {generation.version ? `NixOS ${generation.version}` : ""}{generation.version && generation.kernel ? ", " : ""}{generation.kernel ? `Linux ${generation.kernel}` : ""}
                </p>
              {/if}
            </div>
            {#if generation.current}<span class="rounded-full bg-primary/10 px-2 py-0.5 text-xs font-medium text-primary">Active</span>{/if}
            {#if generation.booted && !generation.current}<span class="rounded-full bg-muted px-2 py-0.5 text-xs font-medium">Running kernel</span>{/if}
            {#if !generation.current}
              <Button
                variant="outline"
                size="sm"
                disabled={app.busy || !app.connected}
                onclick={() => app.prepare(profile, { generation: generation.number })}
              >Review rollback</Button>
            {/if}
          </li>
        {/each}
      </ul>
    {:else}
      <EmptyState icon={HistoryIcon} title="No generations">
        No {profile === "home" ? "Home Manager" : "NixOS system"} profile was found on this computer.
      </EmptyState>
    {/if}
  </section>

  <section class="panel overflow-hidden">
    <div class="border-b px-4 py-3">
      <h2 class="heading">History</h2>
      <p class="text-muted-foreground">What changed in the configuration, newest first.</p>
    </div>
    {#if history?.length}
      <ul class="max-h-96 divide-y overflow-y-auto">
        {#each history as entry (entry.revision)}
          <li class="flex items-start gap-4 px-4 py-3">
            <div class="min-w-0 flex-1">
              <p class="font-medium">{entry.subject}</p>
              {#if entry.applied && entry.body}
                <p class="mt-0.5 text-xs whitespace-pre-line text-muted-foreground">{entry.body}</p>
              {/if}
            </div>
            <div class="shrink-0 text-right text-xs text-muted-foreground">
              <p title={dateTime(entry.date)}>{relative(entry.date, app.now)}</p>
              <p>{entry.applied ? "Applied here" : entry.author}</p>
            </div>
          </li>
        {/each}
      </ul>
    {:else if history}
      <EmptyState icon={ScrollTextIcon} title="No history yet">
        With “Keep a history of changes” on in Settings, every change you apply is listed here.
      </EmptyState>
    {/if}
  </section>

  <section class="panel p-5">
    <div class="flex flex-wrap items-center gap-6">
      <div class="min-w-60 flex-1">
        <h2 class="heading">Clean up</h2>
        <p class="text-muted-foreground">Delete old generations and unused store paths to free disk space.</p>
      </div>
      <div class="flex items-center gap-2">
        <span class="text-xs text-muted-foreground">Keep</span>
        <div class="flex items-center rounded-lg border">
          <Button variant="ghost" size="icon-sm" aria-label="Keep fewer" disabled={retention <= 1} onclick={() => retention--}><MinusIcon /></Button>
          <span class="w-8 text-center font-medium tabular-nums">{retention}</span>
          <Button variant="ghost" size="icon-sm" aria-label="Keep more" disabled={retention >= 50} onclick={() => retention++}><PlusIcon /></Button>
        </div>
        <span class="text-xs text-muted-foreground">newest</span>
      </div>
      <Segmented label="Profiles to clean" bind:value={cleanupScope} options={[{ value: "home", label: "Home" }, { value: "all", label: "Home & system" }]} />
      <Button variant="outline" disabled={app.busy || !app.connected} onclick={cleanup}>Clean up…</Button>
    </div>
    <p class="mt-3 text-xs text-muted-foreground">
      {#if !(removeHome + removeSystem)}
        No generations will be removed; unused store paths are still collected.
      {:else if freed === undefined}
        {plural(removeHome + removeSystem, "generation")} will be removed. Measuring the space this frees…
      {:else}
        {plural(removeHome + removeSystem, "generation")} will be removed, freeing about <span class="font-medium text-foreground">{bytes(freed)}</span>{freed ? "" : " (other generations still use their files)"}.
      {/if}
      Settings can clean up home generations automatically.
    </p>
  </section>
</div>
