<script lang="ts">
  import RotateCwIcon from "@lucide/svelte/icons/rotate-cw";
  import CloudDownloadIcon from "@lucide/svelte/icons/cloud-download";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { bytes, duration, plural, relative, scopeLabel, shortRev } from "$lib/format";
  import { actionLabels } from "../../src/domain";
  import BuildProgress from "$lib/components/app/BuildProgress.svelte";
  import StatusBadge from "$lib/components/app/StatusBadge.svelte";
  import ProgressBar from "$lib/components/app/ProgressBar.svelte";
  import GenerationRail from "$lib/components/app/GenerationRail.svelte";

  const system = $derived(app.system);
  const running = $derived(app.active);
  const runningStep = $derived(running?.steps?.find((step) => step.status === "running"));
  const updates = $derived(app.inputUpdates.length + app.packageUpdates.length);
  const recent = $derived(app.snapshot?.operations.slice(0, 5) ?? []);
  const repo = $derived(system?.repository);
  const disk = $derived(system?.disk);

  function open(id: string) {
    app.selectOperation(id);
    app.navigate("activity");
  }
</script>

<div class="space-y-8">
  <header class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 class="title">{app.host || "This computer"}</h1>
      <p class="mt-1 text-muted-foreground">
        {[system?.nixosVersion && `NixOS ${system.nixosVersion}`, system?.kernel.current && `Linux ${system.kernel.current}`]
          .filter(Boolean)
          .join(", ")}
      </p>
    </div>
    {#if updates}
      <Button variant="outline" onclick={() => app.navigate("updates")}>
        <CloudDownloadIcon />
        {plural(updates, "update")} available
      </Button>
    {/if}
  </header>

  {#if system?.rebootRequired}
    <div class="flex items-center gap-3 rounded-xl bg-warning/12 px-4 py-3">
      <RotateCwIcon class="size-4 shrink-0 text-warning" />
      <p>
        <span class="font-semibold">Restart to finish the update.</span>
        The system is still running kernel {system.kernel.booted ?? "unknown"}; the active configuration uses {system.kernel.current ?? "unknown"}.
      </p>
    </div>
  {/if}

  <section class="panel space-y-5 p-5" aria-labelledby="generations-heading">
    <div class="flex items-baseline justify-between gap-4">
      <h2 id="generations-heading" class="heading">Generations</h2>
      {#if !running && !app.reviews.some((r) => r.host === app.host)}
        <button
          class="focus-ring rounded text-primary hover:underline disabled:opacity-50"
          disabled={!app.connected}
          onclick={() => app.prepare("all")}
        >Prepare home and system together</button>
      {/if}
    </div>
    <GenerationRail profile="home" title="Home" />
    <GenerationRail profile="system" title="System" />
  </section>

  {#if running}
    <section class="panel space-y-4 p-5">
      <div class="flex items-start justify-between gap-4">
        <div>
          <h2 class="heading">{actionLabels[running.request.action]}</h2>
          <p class="text-muted-foreground">{runningStep?.label ?? running.step}, {duration(running, app.now)} so far</p>
        </div>
        <div class="flex gap-2">
          <Button variant="outline" size="sm" onclick={() => open(running.id)}>Show output</Button>
          <Button variant="ghost" size="sm" disabled={running.finishingPrivilegedStep} onclick={() => app.cancel()}>Cancel</Button>
        </div>
      </div>
      {#if runningStep}<BuildProgress progress={app.stepProgress(running, runningStep)} />{/if}
    </section>
  {/if}

  <div class="grid gap-8 lg:grid-cols-[1fr_17rem]">
    <section>
      <div class="mb-2 flex items-baseline justify-between">
        <h2 class="heading">Recent activity</h2>
        <button class="focus-ring rounded text-primary hover:underline" onclick={() => app.navigate("activity")}>All activity</button>
      </div>
      {#if recent.length}
        <ul class="divide-y border-y">
          {#each recent as operation (operation.id)}
            <li>
              <button class="focus-ring flex w-full items-center gap-3 px-1 py-2.5 text-left hover:bg-card/70" onclick={() => open(operation.id)}>
                <span class="min-w-0 flex-1 truncate">
                  <span class="font-medium">{actionLabels[operation.request.action]}</span>
                  <span class="text-muted-foreground">{scopeLabel(operation.request.scope).toLowerCase()}</span>
                </span>
                <span class="text-[0.83rem] text-muted-foreground">{relative(operation.startedAt, app.now)}</span>
                <StatusBadge status={operation.status} />
              </button>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="border-y py-6 text-center text-muted-foreground">Nothing has run yet. Prepared builds and updates appear here.</p>
      {/if}
    </section>

    <aside class="space-y-5">
      <div>
        <h2 class="label">Checkout</h2>
        {#if repo}
          <p class="mt-1 font-semibold">{repo.branch ?? "Detached"} <code class="font-normal text-muted-foreground">{shortRev(repo.revision)}</code></p>
          {#if repo.subject}<p class="truncate text-muted-foreground" title={repo.subject}>{repo.subject}</p>{/if}
          <p class="mt-1 text-[0.83rem] {repo.clean ? 'text-muted-foreground' : 'text-warning'}">
            {repo.clean ? "No uncommitted changes" : plural(repo.changes.length, "uncommitted change")}{repo.lockModified ? ", flake.lock edited" : ""}
          </p>
        {:else}<p class="mt-1 text-muted-foreground">Reading…</p>{/if}
      </div>
      <div>
        <h2 class="label">Nix store disk</h2>
        {#if disk}
          <p class="mt-1 font-semibold">{bytes(disk.free)} free <span class="font-normal text-muted-foreground">of {bytes(disk.total)}</span></p>
          <ProgressBar class="mt-2" value={1 - disk.free / disk.total} label="Disk used" />
          {#if disk.free / disk.total < 0.1}
            <button class="focus-ring mt-2 rounded text-[0.83rem] text-primary hover:underline" onclick={() => app.navigate("generations")}>Free space by cleaning up generations</button>
          {/if}
        {:else}<p class="mt-1 text-muted-foreground">Unknown</p>{/if}
      </div>
    </aside>
  </div>
</div>
