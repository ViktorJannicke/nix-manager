<script lang="ts">
  import MinusIcon from "@lucide/svelte/icons/minus";
  import PlusIcon from "@lucide/svelte/icons/plus";
  import HistoryIcon from "@lucide/svelte/icons/history";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { dateTime, plural, relative } from "$lib/format";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";

  let profile = $state<"system" | "home">("home");
  let retention = $state(3);
  let cleanupScope = $state<"home" | "all">("home");

  const list = $derived(app.system?.generations[profile] ?? []);
  const sorted = $derived([...list].sort((a, b) => b.number - a.number));
  /** Generations `nix-env --delete-generations +N` removes; the current one is always kept. */
  const removable = (key: "home" | "system") => {
    const all = app.system?.generations[key] ?? [];
    const kept = new Set(
      [...all].sort((a, b) => b.number - a.number).slice(0, retention).map((g) => g.number),
    );
    return all.filter((g) => !g.current && !kept.has(g.number)).length;
  };
  const removeHome = $derived(removable("home"));
  const removeSystem = $derived(cleanupScope === "all" ? removable("system") : 0);

  function cleanup() {
    const points = [`Keeps the newest ${plural(retention, "generation")} of each selected profile and the active one.`];
    if (removeHome) points.push(`Removes ${plural(removeHome, "home generation")}.`);
    if (cleanupScope === "all") points.push(`Removes ${plural(removeSystem, "system generation")} and their boot entries; you'll be asked for your password.`);
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
      {removeHome + removeSystem
        ? `${plural(removeHome + removeSystem, "generation")} will be removed.`
        : "No generations will be removed; unused store paths are still collected."}
    </p>
  </section>
</div>
