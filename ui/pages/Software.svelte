<script lang="ts">
  import SearchIcon from "@lucide/svelte/icons/search";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import PackageIcon from "@lucide/svelte/icons/package";
  import RefreshIcon from "@lucide/svelte/icons/refresh-cw";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { origin, software } from "$lib/software.svelte";
  import { relative } from "$lib/format";
  import { t, tn } from "$lib/i18n";
  import { experiences, type Experience } from "../../src/curated";
  import type { CatalogEntry, InstalledPackage, Target } from "../../src/software";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import PendingEdits from "$lib/components/app/PendingEdits.svelte";
  import PackageDialog from "$lib/components/app/PackageDialog.svelte";
  import ExperienceDialog from "$lib/components/app/ExperienceDialog.svelte";
  import ExperienceIcon from "$lib/components/app/ExperienceIcon.svelte";
  import BundleDialog from "$lib/components/app/BundleDialog.svelte";
  import { bundles, type Bundle } from "../../src/bundles";
  import AppIcon from "$lib/components/app/AppIcon.svelte";
  import { arrows, typing } from "$lib/keyboard";
  import { appCategories, featuredApps, type AppCategory, type AppEntry } from "../../src/apps";

  let view = $state<"discover" | "installed">("discover");
  let query = $state("");
  let scope = $state<Target>("home");
  let filter = $state("");
  let showModules = $state(false);
  let changingOnly = $state(false);
  let entry = $state<CatalogEntry>();
  let experience = $state<Experience>();
  let bundle = $state<Bundle>();
  let start = $state(false);
  let searchInput = $state<HTMLInputElement>();
  let filterInput = $state<HTMLInputElement>();
  let content = $state<HTMLElement>();

  // "/" finds: the search on Discover, the filter on Installed.
  function shortcut(event: KeyboardEvent) {
    if (event.key !== "/" || typing(event) || entry || experience || bundle) return;
    event.preventDefault();
    (view === "discover" ? searchInput : filterInput)?.focus();
  }
  function searchKey(event: KeyboardEvent) {
    if (event.key === "ArrowDown") {
      const first = content?.querySelector<HTMLElement>("[data-nav]");
      if (first) {
        event.preventDefault();
        first.focus();
      }
    } else if (event.key === "Escape" && query) {
      event.preventDefault();
      query = "";
    }
  }

  $effect(() => {
    void app.host;
    void app.system?.repository.fingerprint;
    void software.load();
  });

  // Search as you type, once the index exists; the first search builds it.
  $effect(() => {
    const text = query;
    void software.free;
    void software.working;
    const ready = software.catalog?.state === "ready";
    if (text.trim() && software.catalog?.state === "missing") void software.buildCatalog();
    const timer = setTimeout(() => ready && software.search(text), 150);
    return () => clearTimeout(timer);
  });

  // The app list needs the package index: build it when Discover opens.
  const appsOn = $derived(software.appStatus !== undefined && software.appStatus.state !== "off");
  $effect(() => {
    if (view === "discover" && appsOn && software.catalog?.state === "missing") void software.buildCatalog();
  });
  $effect(() => {
    void software.catalog?.builtAt;
    void software.appStatus?.fetchedAt;
    if (view === "discover" && appsOn && software.catalog?.state === "ready") void software.loadApps();
  });

  let shelf = $state<AppCategory | "featured">("featured");
  let showAll = $state(false);
  $effect(() => {
    void shelf;
    showAll = false;
  });
  const byName = (a: AppEntry, b: AppEntry) => a.app!.name.localeCompare(b.app!.name);
  const featured = $derived(
    featuredApps.flatMap((id) => software.apps.find((item) => item.app?.id === id) ?? []),
  );
  const shelves = $derived(
    (Object.keys(appCategories) as AppCategory[])
      .map((id) => ({ id, apps: software.apps.filter((item) => item.app?.categories[0] === id).sort(byName) }))
      .filter((item) => item.apps.length),
  );
  const shown = $derived(shelf === "featured" ? featured : (shelves.find((item) => item.id === shelf)?.apps ?? []));
  const appResults = $derived(software.results.filter((result) => result.app || result.g));
  const otherResults = $derived(software.results.filter((result) => !result.app && !result.g));

  const inventory = $derived(software.inventory);

  // A failure's fix opens its feature, switched on.
  $effect(() => {
    const id = app.offer;
    if (!id || !inventory) return;
    app.offer = undefined;
    view = "discover";
    start = true;
    experience = experiences.find((item) => item.id === id);
  });
  $effect(() => {
    if (!experience) start = false;
  });
  const categories = [
    ["Games", t("Games")],
    ["Development", t("Development")],
    ["Internet", t("Internet")],
    ["Media", t("Media")],
    ["Productivity", t("Productivity")],
    ["System", t("System")],
  ] as const;
  const visible = $derived(inventory ? experiences.filter((item) => software.available(item)) : []);
  const pendingFor = (item: Experience) =>
    software.changes.some(
      (change) => change.scope === item.scope && (change.label === item.title || ("option" in change && Object.keys(item.enable).includes(change.option))),
    );

  /** Packages the next applied build installs or updates, compared with the running generation. */
  const changing = $derived((inventory?.[scope].packages ?? []).filter((item) => item.previous !== undefined && (showModules || !item.external)));
  const installed = $derived(
    (inventory?.[scope].packages ?? []).filter(
      (item) =>
        (showModules || !item.external) &&
        (!changingOnly || item.previous !== undefined) &&
        `${item.name} ${item.attr ?? ""} ${item.description ?? ""}`.toLowerCase().includes(filter.trim().toLowerCase()),
    ),
  );
  const moduleCount = $derived(inventory?.[scope].packages.filter((item) => item.external).length ?? 0);
  const category = (file: string) => file.split("/").at(-1)!.replace(/\.nix$/, "");

  function removeInstalled(item: InstalledPackage) {
    if (!item.attr) return;
    void software.stage([{ kind: "remove-package", scope, file: item.file, attr: item.attr }]);
  }
  const pendingRemoval = (item: InstalledPackage) =>
    software.changes.some((change) => change.kind === "remove-package" && change.attr === item.attr && change.file === item.file);
</script>

<svelte:window onkeydown={shortcut} />

{#snippet resultRow(result: AppEntry)}
  {@const owned = software.installed(result)}
  {@const pending = software.pendingPackage(`pkgs.${result.a}`)}
  <button data-nav class="focus-ring flex w-full items-start gap-3 px-4 py-3 text-left hover:bg-muted/40 focus-visible:bg-muted/40" onclick={() => (entry = result)}>
    {#if result.app || result.g}<AppIcon app={result.app} name={result.app?.name ?? result.n ?? result.a} class="size-9" />{/if}
    <div class="min-w-0 flex-1">
      <p class="flex flex-wrap items-baseline gap-x-2">
        <span class="font-semibold">{result.app?.name ?? result.n ?? result.a}</span>
        <span class="font-mono text-[0.72rem] text-muted-foreground">{result.v ?? ""}</span>
        {#if result.a !== (result.app?.name ?? result.n)}<span class="font-mono text-[0.72rem] text-muted-foreground">{result.a}</span>{/if}
      </p>
      <p class="line-clamp-2 text-muted-foreground">{result.app?.summary ?? result.d ?? t("No description.")}</p>
    </div>
    <span class="shrink-0 pt-0.5 text-xs font-medium">
      {#if pending}<span class="text-prepared-foreground">{pending.kind === "add-package" ? t("Will be installed") : t("Will be removed")}</span>
      {:else if owned}<span class="text-primary">{t("Installed")}</span>
      {:else if result.b}<span class="text-warning">{t("Broken")}</span>
      {:else if !result.f}<span class="text-muted-foreground">{t("Not free")}</span>{/if}
    </span>
  </button>
{/snippet}

<div class="space-y-6" bind:this={content}>
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div class="min-w-0">
      <h1 class="title">{t("Software")}</h1>
      <p class="mt-1 text-muted-foreground">{t("Install packages and turn on features for {host}. Your edits wait here until you prepare them.", { host: app.host })}</p>
    </div>
    <Segmented
      label={t("View")}
      bind:value={view}
      options={[
        { value: "discover", label: t("Discover") },
        { value: "installed", label: t("Installed"), count: inventory ? inventory.home.packages.filter((p) => !p.external).length + inventory.system.packages.filter((p) => !p.external).length : undefined },
      ]}
    />
  </header>

  <PendingEdits />

  {#if software.error}
    <p class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-destructive">
      <TriangleAlertIcon class="mt-0.5 size-4 shrink-0" />
      <span class="flex-1">{t("Could not read {host}'s configuration.", { host: app.host })} {software.error}</span>
      <Button variant="ghost" size="sm" onclick={() => software.load(true)}>{t("Try again")}</Button>
    </p>
  {/if}

  {#if view === "discover"}
    <label class="flex h-12 items-center gap-3 rounded-xl border border-field bg-card px-4 shadow-xs focus-within:ring-3 focus-within:ring-ring/30">
      <SearchIcon class="size-5 text-muted-foreground" />
      <input
        bind:this={searchInput}
        bind:value={query}
        onkeydown={searchKey}
        aria-label={t("Search apps and packages")}
        aria-keyshortcuts="/"
        placeholder={t("Search apps and over a hundred thousand packages")}
        class="peer w-full bg-transparent text-base outline-none placeholder:text-muted-foreground"
      />
      {#if !query}<kbd class="rounded border bg-muted px-1.5 font-mono text-xs text-muted-foreground peer-focus:hidden" title={t("Press / to search")}>/</kbd>{/if}
      {#if software.catalog?.state === "building"}<LoaderIcon class="size-4 animate-spin text-muted-foreground" aria-hidden="true" /><span class="sr-only">{t("Building the package index")}</span>{/if}
    </label>

    <p class="sr-only" role="status">{query.trim() && software.searched === query ? (software.results.length ? tn("{count} result", "{count} results", software.results.length) : t("No packages match")) : ""}</p>

    {#if query.trim()}
      <div class="-mt-3 flex flex-wrap gap-4 px-1 text-xs">
        <label class="flex items-center gap-1.5"><input type="checkbox" class="focus-ring accent-primary" checked={software.free} onchange={(event) => software.setFilter("free", event.currentTarget.checked)} /> {t("Free software only")}</label>
        <label class="flex items-center gap-1.5"><input type="checkbox" class="focus-ring accent-primary" checked={software.working} onchange={(event) => software.setFilter("working", event.currentTarget.checked)} /> {t("Hide packages marked as broken")}</label>
      </div>
      {#if software.catalog?.state === "ready"}
        {#if software.results.length}
          <div class="space-y-5" use:arrows={{ before: () => searchInput }}>
            {#if appResults.length}
              <section class="space-y-2" aria-labelledby="results-apps">
                <h2 id="results-apps" class="heading">{t("Apps")}</h2>
                <ul class="panel divide-y overflow-hidden">
                  {#each appResults as result (result.a)}<li>{@render resultRow(result)}</li>{/each}
                </ul>
              </section>
            {/if}
            {#if otherResults.length}
              <section class="space-y-2" aria-labelledby="results-other">
                <h2 id="results-other" class="heading">{appResults.length ? t("Other software") : t("Packages")}</h2>
                <p class="-mt-1 text-xs text-muted-foreground">{t("Command-line tools, libraries and parts other software uses.")}</p>
                <ul class="panel divide-y overflow-hidden">
                  {#each otherResults as result (result.a)}<li>{@render resultRow(result)}</li>{/each}
                </ul>
              </section>
            {/if}
          </div>
        {:else if software.searched === query}
          <section class="panel"><EmptyState icon={SearchIcon} title={t("No packages match “{query}”", { query })}>{t("Try the program's name, or a word from what it does.")}</EmptyState></section>
        {/if}
        <p class="text-xs text-muted-foreground">
          {t("Index of {count} packages from your locked nixpkgs and overlays, built {when}.", { count: software.catalog.count ?? 0, when: relative(software.catalog.builtAt, app.now) })}
          <button class="focus-ring rounded text-primary hover:underline" onclick={() => software.buildCatalog(true)}>{t("Rebuild")}</button>
        </p>
      {:else if software.catalog?.state === "failed"}
        <section class="panel">
          <EmptyState icon={TriangleAlertIcon} title={t("The package index could not be built")}>
            {software.catalog.error}
            {#snippet actions()}<Button variant="outline" onclick={() => software.buildCatalog(true)}><RefreshIcon /> {t("Try again")}</Button>{/snippet}
          </EmptyState>
        </section>
      {:else}
        <section class="panel">
          <EmptyState icon={LoaderIcon} title={t("Building the package index")}>
            {t("Nix Manager reads every package in your locked nixpkgs once, which takes about half a minute. It is built again after nixpkgs updates.")}
          </EmptyState>
        </section>
      {/if}
    {:else if !inventory}
      <section class="panel">
        <EmptyState icon={LoaderIcon} title={t("Reading {host}'s configuration", { host: app.host })}>
          {t("Evaluating what the home and system configurations contain. This takes a few seconds.")}
        </EmptyState>
      </section>
    {:else}
      <div class="space-y-6" use:arrows={{ before: () => searchInput }}>
      <section class="space-y-3" aria-labelledby="bundles-heading">
        <h2 id="bundles-heading" class="heading">{t("Starting points")}</h2>
        <ul class="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
          {#each bundles as item (item.id)}
            <li>
              <button
                data-nav
                class="focus-ring flex h-full w-full flex-col gap-2 rounded-xl border bg-card p-3 text-left transition-colors hover:border-primary/40"
                onclick={() => (bundle = item)}
              >
                <span class="grid size-9 place-items-center rounded-lg bg-primary/10 text-primary"><ExperienceIcon name={item.icon} class="size-4.5" /></span>
                <span class="font-semibold">{t(item.title)}</span>
                <span class="text-muted-foreground">{t(item.summary)}</span>
              </button>
            </li>
          {/each}
        </ul>
      </section>
      {#if appsOn}
        <section class="space-y-3" aria-labelledby="apps-heading">
          <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
            <h2 id="apps-heading" class="heading">{t("Apps")}</h2>
            {#if software.apps.length}<p class="text-xs text-muted-foreground">{t("{count} apps with details from Flathub", { count: software.apps.length })}</p>{/if}
          </div>
          {#if software.apps.length}
            <div class="flex flex-wrap gap-1.5" role="group" aria-label={t("App categories")}>
              {#each [{ id: "featured" as const, label: t("Popular") }, ...shelves.map((item) => ({ id: item.id, label: t(appCategories[item.id]) }))] as item (item.id)}
                <button
                  class="focus-ring rounded-full border px-3 py-1 text-xs font-medium transition-colors
                    {shelf === item.id ? 'border-primary bg-primary text-primary-foreground' : 'bg-card text-muted-foreground hover:border-primary/40 hover:text-foreground'}"
                  aria-pressed={shelf === item.id}
                  onclick={() => (shelf = item.id)}
                >{item.label}</button>
              {/each}
            </div>
            <ul class="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
              {#each showAll ? shown : shown.slice(0, 18) as item (item.a)}
                {@const owned = software.installed(item)}
                {@const pending = software.pendingPackage(`pkgs.${item.a}`)}
                <li>
                  <button
                    data-nav
                    class="focus-ring flex h-full w-full items-center gap-3 rounded-xl border bg-card p-3 text-left transition-colors hover:border-primary/40
                      {pending ? 'border-2 border-dashed border-prepared/70 bg-accent/40' : ''}"
                    onclick={() => (entry = item)}
                  >
                    <AppIcon app={item.app} name={item.app!.name} />
                    <span class="min-w-0 flex-1">
                      <span class="flex items-baseline gap-2">
                        <span class="truncate font-semibold">{item.app!.name}</span>
                        {#if pending}<span class="shrink-0 text-xs font-medium text-prepared-foreground">{t("Pending")}</span>
                        {:else if owned}<span class="shrink-0 text-xs font-medium text-primary">{t("Installed")}</span>{/if}
                      </span>
                      <span class="line-clamp-2 text-muted-foreground">{item.app!.summary ?? item.d ?? ""}</span>
                    </span>
                  </button>
                </li>
              {/each}
            </ul>
            {#if shown.length > 18}
              <Button variant="outline" size="sm" onclick={() => (showAll = !showAll)}>
                {showAll ? t("Show fewer") : t("Show all {count} apps", { count: shown.length })}
              </Button>
            {/if}
          {:else if software.appStatus?.state === "failed"}
            <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2 text-xs">
              <TriangleAlertIcon class="size-4 shrink-0 text-warning" />
              <span class="flex-1">{software.appStatus.error} {t("Searching still finds every package.")}</span>
              <button class="focus-ring rounded text-primary hover:underline" onclick={() => software.loadApps(true)}>{t("Try again")}</button>
            </p>
          {:else if software.catalog?.state === "failed"}
            <p class="text-muted-foreground">{t("The app list needs the package index, which could not be built.")} {software.catalog.error}</p>
          {:else}
            <p class="flex items-center gap-2 text-muted-foreground">
              <LoaderIcon class="size-4 animate-spin" />
              {software.catalog?.state === "ready" ? t("Downloading app details from Flathub…") : t("Reading every package in your nixpkgs once. This takes about half a minute.")}
            </p>
          {/if}
        </section>
        <h2 class="heading">{t("Features")}</h2>
      {/if}
      {#each categories as [name, title] (name)}
        {@const items = visible.filter((item) => item.category === name)}
        {#if items.length}
          <section class="space-y-2" aria-labelledby="category-{name}">
            <svelte:element this={appsOn ? "h3" : "h2"} id="category-{name}" class={appsOn ? "label" : "heading"}>{title}</svelte:element>
            <ul class="grid gap-2 sm:grid-cols-2">
              {#each items as item (item.id)}
                {@const on = software.enabled(item)}
                {@const pending = pendingFor(item)}
                <li>
                  <button
                    data-nav
                    class="focus-ring flex h-full w-full items-start gap-3 rounded-xl border bg-card p-3 text-left transition-colors hover:border-primary/40
                      {pending ? 'border-2 border-dashed border-prepared/70 bg-accent/40' : ''}"
                    onclick={() => (experience = item)}
                  >
                    <span class="grid size-9 shrink-0 place-items-center rounded-lg {on ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground'}">
                      <ExperienceIcon name={item.icon} class="size-4.5" />
                    </span>
                    <span class="min-w-0 flex-1">
                      <span class="flex items-baseline gap-2">
                        <span class="font-semibold">{t(item.title)}</span>
                        {#if pending}<span class="text-xs font-medium text-prepared-foreground">{t("Pending")}</span>
                        {:else if on}<span class="text-xs font-medium text-primary">{t("On")}</span>{/if}
                      </span>
                      <span class="block text-muted-foreground">{t(item.summary)}</span>
                      <span class="mt-0.5 block text-[0.72rem] text-muted-foreground">{item.scope === "system" ? t("System") : t("Your user")}</span>
                    </span>
                  </button>
                </li>
              {/each}
            </ul>
          </section>
        {/if}
      {/each}
      </div>
    {/if}
  {:else}
    <section class="panel p-4">
      <div class="mb-3 flex flex-wrap items-center gap-2">
        <Segmented
          label={t("Configuration")}
          bind:value={scope}
          options={[
            { value: "home" as Target, label: t("Your user"), count: inventory?.home.packages.filter((p) => !p.external).length },
            { value: "system" as Target, label: t("System"), count: inventory?.system.packages.filter((p) => !p.external).length },
          ]}
        />
        {#if changing.length}
          <button
            class="focus-ring rounded-md px-2 py-1 text-xs font-medium {changingOnly ? 'bg-accent text-accent-foreground' : 'text-primary hover:underline'}"
            aria-pressed={changingOnly}
            onclick={() => (changingOnly = !changingOnly)}
            title={t("Compared with the version in use now. Apply your configuration to get them.")}
          >{tn("{count} package changes on the next apply", "{count} packages change on the next apply", changing.length)}</button>
        {/if}
        <label class="ml-auto flex h-8 w-56 items-center gap-2 rounded-md border border-field bg-card px-2 text-muted-foreground focus-within:ring-2 focus-within:ring-ring/40">
          <SearchIcon class="size-3.5" />
          <input bind:this={filterInput} bind:value={filter} aria-label={t("Filter installed packages")} aria-keyshortcuts="/" placeholder={t("Filter (/)")} class="w-full bg-transparent text-xs text-foreground outline-none placeholder:text-muted-foreground" />
        </label>
      </div>
      {#if !inventory}
        <EmptyState icon={software.loading ? LoaderIcon : PackageIcon} title={software.loading ? t("Reading {host}'s configuration", { host: app.host }) : t("Not read yet")} />
      {:else if installed.length}
        <div class="overflow-hidden rounded-lg border">
          <table class="w-full text-left text-xs">
            <thead class="bg-muted/50 text-muted-foreground">
              <tr>
                <th class="px-3 py-2 font-medium">{t("Package")}</th>
                <th class="px-3 py-2 font-medium">{t("Version")}</th>
                <th class="px-3 py-2 font-medium">{t("Installed by")}</th>
                <th class="w-24 px-3 py-2"><span class="sr-only">{t("Actions")}</span></th>
              </tr>
            </thead>
            <tbody class="divide-y">
              {#each installed as item, index (`${item.out}-${item.file}-${index}`)}
                {@const removing = pendingRemoval(item)}
                <tr class="hover:bg-muted/30 {removing ? 'bg-accent/40' : ''}">
                  <td class="px-3 py-1.5">
                    <p class="font-medium {removing ? 'line-through decoration-prepared-foreground' : ''}">{item.name}</p>
                    {#if item.description}<p class="line-clamp-1 text-muted-foreground">{item.description}</p>{/if}
                  </td>
                  <td class="px-3 py-1.5 font-mono text-[0.7rem]">
                    {#if item.previous}
                      <span title={t("The version in use has {previous}; your configuration builds {version}.", { previous: item.previous, version: item.version ?? "?" })}>
                        <span class="text-muted-foreground">{item.previous} →</span> <span class="font-semibold text-primary">{item.version ?? "?"}</span>
                      </span>
                    {:else}
                      {item.version ?? ""}
                      {#if item.previous === null}<span class="ml-1 font-sans text-[0.68rem] font-medium text-primary" title={t("Not in the version in use now.")}>{t("new")}</span>{/if}
                    {/if}
                  </td>
                  <td class="px-3 py-1.5" title={item.file}>
                    {#if item.external}<span class="text-muted-foreground">{origin(item.file)}</span>
                    {:else}{category(item.file)}<span class="block font-mono text-[0.68rem] text-muted-foreground">{item.file}</span>{/if}
                  </td>
                  <td class="px-3 py-1.5 text-right">
                    {#if removing}<span class="font-medium text-prepared-foreground">{t("Will be removed")}</span>
                    {:else if item.attr}<Button variant="ghost" size="xs" disabled={software.saving} onclick={() => removeInstalled(item)}>{t("Remove")}</Button>
                    {:else if !item.external}<span class="text-muted-foreground" title={t("This item is computed; edit it in the file.")}>{t("Computed")}</span>{/if}
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {:else}
        <p class="rounded-lg border border-dashed px-3 py-6 text-center text-muted-foreground">{filter || changingOnly ? t("No installed packages match.") : t("Nothing installed here yet.")}</p>
      {/if}
      {#if moduleCount}
        <button class="focus-ring mt-3 rounded text-xs font-medium text-primary hover:underline" onclick={() => (showModules = !showModules)}>
          {showModules
            ? tn("Hide {count} package added by settings and modules", "Hide {count} packages added by settings and modules", moduleCount)
            : tn("Show {count} package added by settings and modules", "Show {count} packages added by settings and modules", moduleCount)}
        </button>
      {/if}
    </section>
  {/if}
</div>

<PackageDialog bind:entry />
<ExperienceDialog bind:experience {start} />
<BundleDialog bind:bundle />
