<script lang="ts">
  import RotateCwIcon from "@lucide/svelte/icons/rotate-cw";
  import CloudDownloadIcon from "@lucide/svelte/icons/cloud-download";
  import { Button } from "$lib/components/ui/button";
  import PencilLineIcon from "@lucide/svelte/icons/pencil-line";
  import Undo2Icon from "@lucide/svelte/icons/undo-2";
  import { app } from "$lib/app.svelte";
  import { t, tn } from "$lib/i18n";
  import { describeChange, software } from "$lib/software.svelte";
  import { bytes, duration, relative, remaining, scopeText, shortRev } from "$lib/format";
  import { actionLabels } from "../../src/domain";
  import type { Scope } from "../../src/domain";
  import BuildProgress from "$lib/components/app/BuildProgress.svelte";
  import StatusBadge from "$lib/components/app/StatusBadge.svelte";
  import ProgressBar from "$lib/components/app/ProgressBar.svelte";
  import TechName from "$lib/components/app/TechName.svelte";
  import GenerationRail from "$lib/components/app/GenerationRail.svelte";
  import PendingButton from "$lib/components/app/PendingButton.svelte";

  const system = $derived(app.system);
  const running = $derived(app.active);
  const runningStep = $derived(running?.steps?.find((step) => step.status === "running"));
  const left = $derived(running && remaining(app.snapshot?.operations ?? [], running, app.now));
  const updates = $derived(app.inputUpdates.length + app.packageUpdates.length);
  const recent = $derived(app.snapshot?.operations.slice(0, 5) ?? []);
  const repo = $derived(system?.repository);
  const disk = $derived(system?.disk);
  const draft = $derived(software.draft);
  const last = $derived(app.lastChange);
  const undoing = $derived(Boolean(last && running?.request.undo === last.id));

  function open(id: string) {
    app.selectOperation(id);
    app.navigate("activity");
  }
</script>

<div class="space-y-8">
  <header class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 class="title">{app.host || t("This computer")}</h1>
      <p class="mt-1 text-muted-foreground">
        {[system?.nixosVersion && `NixOS ${system.nixosVersion}`, system?.kernel.current && `Linux ${system.kernel.current}`]
          .filter(Boolean)
          .join(", ")}
      </p>
    </div>
    {#if updates}
      <Button variant="outline" onclick={() => app.navigate("updates")}>
        <CloudDownloadIcon />
        {tn("{count} update available", "{count} updates available", updates)}
      </Button>
    {/if}
  </header>

  {#if system?.rebootRequired}
    <div class="flex items-center gap-3 rounded-xl bg-warning/12 px-4 py-3">
      <RotateCwIcon class="size-4 shrink-0 text-warning" />
      <p>
        <span class="font-semibold">{t("Restart to finish the update.")}</span>
        {t("The system is still running kernel {booted}; the active configuration uses {current}.", {
          booted: system.kernel.booted ?? t("unknown"),
          current: system.kernel.current ?? t("unknown"),
        })}
      </p>
    </div>
  {/if}

  {#if draft?.changes.length}
    <section class="prepared flex flex-wrap items-start gap-4 p-4" aria-labelledby="pending-overview">
      <PencilLineIcon class="mt-0.5 size-4 shrink-0 text-prepared-foreground" />
      <div class="min-w-0 flex-1">
        <h2 id="pending-overview" class="font-semibold">{tn("{count} pending edit to your configuration", "{count} pending edits to your configuration", draft.changes.length)}</h2>
        <ul class="mt-1 text-muted-foreground">
          {#each draft.changes.slice(0, 3) as change, index (index)}<li class="truncate">{describeChange(change).title}</li>{/each}
          {#if draft.changes.length > 3}<li>{t("and {count} more", { count: draft.changes.length - 3 })}</li>{/if}
        </ul>
        {#if draft.problems.length}
          <p class="mt-1 text-xs text-destructive">{tn("{count} edit cannot be applied to your configuration as it is now.", "{count} edits cannot be applied to your configuration as it is now.", draft.problems.length)}</p>
        {/if}
      </div>
      <div class="flex gap-2">
        <Button variant="ghost" size="sm" onclick={() => app.navigate("software")}>{t("Show edits")}</Button>
        <PendingButton />
      </div>
    </section>
  {/if}

  {#if last}
    <section class="panel flex flex-wrap items-start gap-4 p-4" aria-labelledby="last-change">
      <Undo2Icon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
      <div class="min-w-0 flex-1">
        <h2 id="last-change" class="font-semibold">{t("Last change, {when}", { when: relative(last.finishedAt ?? last.startedAt, app.now) })}</h2>
        <ul class="mt-1 text-muted-foreground">
          {#each last.undo.changes.slice(0, 3) as change, index (index)}<li class="truncate">{change}</li>{/each}
          {#if last.undo.changes.length > 3}<li>{t("and {count} more", { count: last.undo.changes.length - 3 })}</li>{/if}
        </ul>
        <p class="mt-1 text-xs text-muted-foreground">
          {t("Undo goes back to how {what} was before{files}. You see what changes before anything is applied.", {
            what: last.request.scope === "home" ? t("your user's setup") : t("this computer"),
            files: last.undo.files.length ? t(", configuration files included") : "",
          })}
        </p>
      </div>
      <Button variant="outline" size="sm" disabled={app.busy || !app.connected} onclick={() => app.undo(last)}>
        {undoing ? t("Preparing…") : t("Undo")}
      </Button>
    </section>
  {/if}

  <section class="panel space-y-5 p-5" aria-labelledby="generations-heading">
    <div class="flex items-baseline justify-between gap-4">
      <h2 id="generations-heading" class="heading">{t("Restore points")}<TechName name="generations" /></h2>
      {#if !running && !app.reviews.some((r) => r.host === app.host)}
        <button
          class="focus-ring rounded text-primary hover:underline disabled:opacity-50"
          disabled={!app.connected}
          onclick={() => app.prepare("all")}
        >{t("Prepare your user and system together")}</button>
      {/if}
    </div>
    <GenerationRail profile="home" title={t("Your user")} />
    <GenerationRail profile="system" title={t("System")} />
  </section>

  {#if running}
    <section class="panel space-y-4 p-5">
      <div class="flex items-start justify-between gap-4">
        <div>
          <h2 class="heading">{t(actionLabels[running.request.action])}</h2>
          <p class="text-muted-foreground">{t("{step}, {duration} so far", { step: t(runningStep?.label ?? running.step), duration: duration(running, app.now) })}</p>
          {#if left}<p class="text-xs text-muted-foreground">{left}</p>{/if}
        </div>
        <div class="flex gap-2">
          <Button variant="outline" size="sm" onclick={() => open(running.id)}>{t("Show output")}</Button>
          <Button variant="ghost" size="sm" disabled={running.finishingPrivilegedStep} onclick={() => app.cancel()}>{t("Cancel")}</Button>
        </div>
      </div>
      {#if runningStep}<BuildProgress progress={app.stepProgress(running, runningStep)} />{/if}
    </section>
  {/if}

  <div class="grid gap-8 lg:grid-cols-[1fr_17rem]">
    <section>
      <div class="mb-2 flex items-baseline justify-between">
        <h2 class="heading">{t("Recent activity")}</h2>
        <button class="focus-ring rounded text-primary hover:underline" onclick={() => app.navigate("activity")}>{t("All activity")}</button>
      </div>
      {#if recent.length}
        <ul class="divide-y border-y">
          {#each recent as operation (operation.id)}
            <li>
              <button class="focus-ring flex w-full items-center gap-3 px-1 py-2.5 text-left hover:bg-card/70" onclick={() => open(operation.id)}>
                <span class="min-w-0 flex-1 truncate">
                  <span class="font-medium">{t(actionLabels[operation.request.action])}</span>
                  <span class="text-muted-foreground">{scopeText(operation.request.scope)}</span>
                </span>
                <span class="text-[0.83rem] text-muted-foreground">{relative(operation.startedAt, app.now)}</span>
                <StatusBadge status={operation.status} />
              </button>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="border-y py-6 text-center text-muted-foreground">{t("Nothing has run yet. Prepared builds and updates appear here.")}</p>
      {/if}
    </section>

    <aside class="space-y-5">
      <div>
        <h2 class="label">{t("Configuration")}</h2>
        {#if repo}
          <p class="mt-1 font-semibold">{repo.branch ?? t("Detached")} <code class="font-normal text-muted-foreground">{shortRev(repo.revision)}</code></p>
          {#if repo.subject}<p class="truncate text-muted-foreground" title={repo.subject}>{repo.subject}</p>{/if}
          <p class="mt-1 text-[0.83rem] {repo.clean ? 'text-muted-foreground' : 'text-warning'}">
            {repo.clean ? t("Everything is saved") : tn("{count} unsaved change in the folder", "{count} unsaved changes in the folder", repo.changes.length)}{repo.lockModified ? t(", source versions edited") : ""}
          </p>
        {:else}<p class="mt-1 text-muted-foreground">{t("Reading…")}</p>{/if}
      </div>
      <div>
        <h2 class="label">{t("Disk space for software")}</h2>
        {#if disk}
          <p class="mt-1 font-semibold">{t("{size} free", { size: bytes(disk.free) })} <span class="font-normal text-muted-foreground">{t("of {total}", { total: bytes(disk.total) })}</span></p>
          <ProgressBar class="mt-2" value={1 - disk.free / disk.total} label={t("Disk used")} />
          {#if disk.free / disk.total < 0.1}
            <button class="focus-ring mt-2 rounded text-[0.83rem] text-primary hover:underline" onclick={() => app.navigate("generations")}>{t("Free space by removing old restore points")}</button>
          {/if}
        {:else}<p class="mt-1 text-muted-foreground">{t("Unknown")}</p>{/if}
      </div>
    </aside>
  </div>
</div>
