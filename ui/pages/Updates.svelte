<script lang="ts">
  import RefreshCwIcon from "@lucide/svelte/icons/refresh-cw";
  import ExternalLinkIcon from "@lucide/svelte/icons/external-link";
  import CircleCheckIcon from "@lucide/svelte/icons/circle-check";
  import CircleAlertIcon from "@lucide/svelte/icons/circle-alert";
  import { Button } from "$lib/components/ui/button";
  import { t, tn } from "$lib/i18n";
  import { app } from "$lib/app.svelte";
  import { day, daysBetween, relative, shortRev } from "$lib/format";
  import { actionLabels, type Action, type Scope } from "../../src/domain";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import TechName from "$lib/components/app/TechName.svelte";

  let selected = $state<string[]>([]);
  let scope = $state<Scope>("all");
  const checking = $derived(app.active?.request.action === "check");
  const names = $derived(app.inputUpdates.map((item) => item.name));
  const all = $derived(names.length > 0 && names.every((name) => selected.includes(name)));

  // Drop selections for inputs that are no longer outdated.
  $effect(() => {
    const keep = selected.filter((name) => names.includes(name));
    if (keep.length !== selected.length) selected = keep;
  });

  const check = () => app.run(app.request("check"));
  const prepare = () =>
    app.prepare(scope, all ? { update: true } : { inputs: [...selected] });

  const pinAction = (name: string) => `${name}-update` as Action;
  const pinName = (name: string) => actionLabels[pinAction(name)]?.replace(/^Update /, "") ?? name;

  function updatePin(name: string, latest: string) {
    app.confirm({
      title: t("Update {name} to {version}?", { name: pinName(name), version: latest }),
      description: t("Downloads the release, then writes its version into your configuration."),
      request: app.request(pinAction(name), { scope: "home" }),
      confirmLabel: t("Update pin"),
      points: [
        t("Your configuration is changed; Git shows exactly what changed."),
        t("Nothing changes on the computer yet. Prepare your user's changes to build and apply the new version."),
      ],
    });
  }
</script>

<div class="space-y-6">
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div>
      <h1 class="title">{t("Updates")}</h1>
      <p class="mt-1 text-muted-foreground">
        {#if app.updates}{t("Last checked {time}", { time: relative(app.updates.checkedAt, app.now) })}{:else}{t("Not checked yet")}{/if}
      </p>
    </div>
    <Button variant="outline" disabled={app.busy || !app.connected} onclick={check}>
      <RefreshCwIcon class={checking ? "animate-spin" : ""} />
      {checking ? t("Checking…") : t("Check now")}
    </Button>
  </header>

  {#if app.updates?.errors.length}
    <div class="space-y-1 rounded-xl border border-destructive/30 bg-destructive/5 px-4 py-3">
      <p class="flex items-center gap-2 font-medium text-destructive"><CircleAlertIcon class="size-4" /> {t("Some sources could not be checked")}</p>
      {#each app.updates.errors as error}<p class="text-xs break-words text-muted-foreground">{t(error)}</p>{/each}
    </div>
  {/if}

  {#if !app.updates}
    <section class="panel">
      <EmptyState icon={RefreshCwIcon} title={t("No update check yet")}>
        {t("Looks for newer versions of your software sources and pinned apps without changing anything.")}
        {#snippet actions()}<Button disabled={app.busy} onclick={check}>{t("Check for updates")}</Button>{/snippet}
      </EmptyState>
    </section>
  {:else if !app.inputUpdates.length && !app.packageUpdates.length}
    <section class="panel">
      {#if app.updates.errors.length}
        <EmptyState icon={CircleCheckIcon} title={t("No updates found")}>
          {t("Everything that could be checked is current. Fix the sources above and check again.")}
        </EmptyState>
      {:else}
        <EmptyState icon={CircleCheckIcon} title={t("Everything is up to date")}>
          {t("All software sources and pinned apps are at their latest versions.")}
        </EmptyState>
      {/if}
    </section>
  {/if}

  {#if app.inputUpdates.length}
    <section class="panel overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b px-4 py-3">
        <div class="flex-1">
          <h2 class="heading">{t("Software sources")}<TechName name="flake inputs" /></h2>
          <p class="text-xs text-muted-foreground">{t("Where your software comes from. Updates are built and shown for review before anything changes.")}</p>
        </div>
        <Segmented
          label={t("What to prepare")}
          bind:value={scope}
          options={[{ value: "home", label: t("Your user") }, { value: "system", label: t("System") }, { value: "all", label: t("Both") }]}
        />
        <Button disabled={!selected.length || app.busy || !app.connected} onclick={prepare}>
          {selected.length ? tn("Prepare {count} update", "Prepare {count} updates", selected.length) : t("Prepare updates")}
        </Button>
      </div>
      <table class="w-full text-left">
        <thead class="text-xs text-muted-foreground">
          <tr class="border-b">
            <th class="w-10 py-2 pl-4">
              <input
                type="checkbox"
                aria-label={t("Select all sources")}
                class="accent-primary"
                checked={all}
                indeterminate={selected.length > 0 && !all}
                onchange={() => (selected = all ? [] : [...names])}
              />
            </th>
            <th class="py-2 font-medium">{t("Source")}</th>
            <th class="py-2 font-medium">{t("Current")}</th>
            <th class="py-2 font-medium">{t("Latest")}</th>
            <th class="py-2 pr-4 text-right font-medium">{t("Behind")}</th>
          </tr>
        </thead>
        <tbody class="divide-y">
          {#each app.inputUpdates as item (item.name)}
            {@const behind = daysBetween(item.currentDate, item.latestDate)}
            <tr class="hover:bg-muted/30">
              <td class="py-2.5 pl-4"><input type="checkbox" class="accent-primary" aria-label={t("Select {name}", { name: item.name })} bind:group={selected} value={item.name} /></td>
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
                  <a class="inline-flex items-center gap-1 font-mono text-xs text-primary hover:underline" href={item.url} target="_blank" rel="noreferrer" aria-label={t("{name} at {rev}, opens in a new window", { name: item.name, rev: shortRev(item.latest) })}>
                    {shortRev(item.latest)}<ExternalLinkIcon class="size-3" />
                  </a>
                {:else}<p class="font-mono text-xs text-muted-foreground">{shortRev(item.latest)}</p>{/if}
              </td>
              <td class="py-2.5 pr-4 text-right tabular-nums {behind && behind > 30 ? 'text-warning' : 'text-muted-foreground'}">
                {behind === undefined ? "—" : behind <= 0 ? t("< 1 day") : tn("{count} day", "{count} days", behind)}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </section>
  {/if}

  {#if app.packageUpdates.length}
    <section class="panel overflow-hidden">
      <div class="border-b px-4 py-3">
        <h2 class="heading">{t("Pinned packages")}</h2>
        <p class="text-xs text-muted-foreground">{t("Packages built from upstream releases pinned in your configuration.")}</p>
      </div>
      <ul class="divide-y">
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
              <Button variant="ghost" size="sm" href={item.url} target="_blank" rel="noreferrer">{t("Release notes")} <ExternalLinkIcon /></Button>
            {/if}
            <Button variant="outline" size="sm" disabled={app.busy} onclick={() => updatePin(item.name, item.latest)}>{t("Update")}</Button>
          </li>
        {/each}
      </ul>
    </section>
  {/if}
</div>
