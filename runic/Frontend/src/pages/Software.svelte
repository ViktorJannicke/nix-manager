<script lang="ts">
  import SearchIcon from "@lucide/svelte/icons/search";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import PackageIcon from "@lucide/svelte/icons/package";
  import RefreshIcon from "@lucide/svelte/icons/refresh-cw";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import type { AppEntry, InstalledPackage } from "../generated/types.js";
  import { software, type Target } from "$lib/software.svelte";
  import { origin } from "$lib/changes";
  import { relative } from "$lib/format";
  import { m, stored } from "$lib/i18n";
  import { experiences, type Experience } from "$lib/curated";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import PendingEdits from "$lib/components/app/PendingEdits.svelte";
  import PackageDialog from "$lib/components/app/PackageDialog.svelte";
  import ExperienceDialog from "$lib/components/app/ExperienceDialog.svelte";
  import ExperienceIcon from "$lib/components/app/ExperienceIcon.svelte";
  import BundleDialog from "$lib/components/app/BundleDialog.svelte";
  import { bundles, type Bundle } from "$lib/bundles";
  import AppIcon from "$lib/components/app/AppIcon.svelte";
  import { arrows, typing } from "$lib/keyboard";
  import { appCategories, appCategoryLabel, featuredApps, type AppCategory } from "$lib/apps";

  let view = $state<"discover" | "installed">("discover");
  let query = $state("");
  let scope = $state<Target>("home");
  let filter = $state("");
  let showModules = $state(false);
  let changingOnly = $state(false);
  let entry = $state<AppEntry>();
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

  // The ViewModel reads what the host installs for the observed checkout; the backend caches it by the
  // checkout's state.
  $effect(() => {
    void app.host;
    void app.system?.repository.fingerprint;
    void app.softwareView?.client;
    void app.dashboard?.appDetails;
    void software.load();
  });
  $effect(() => {
    if (software.state) void software.applyFilters();
  });

  // Search as you type, once the index exists; the first search builds it. The ViewModel searches again when the
  // filters change.
  $effect(() => {
    const text = query;
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
  const byName = (a: AppEntry, b: AppEntry) => a.app!.name.localeCompare(b.app!.name, app.dashboard?.locale);
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
    ["Games", m.software_games()],
    ["Development", m.software_development()],
    ["Internet", m.software_internet()],
    ["Media", m.software_media()],
    ["Productivity", m.software_productivity()],
    ["System", m.scope_system()],
  ] as const;
  const visible = $derived(inventory ? experiences.filter((item) => software.available(item)) : []);
  const pendingFor = (item: Experience) =>
    software.changes.some(
      (change) => change.scope === item.scope && (change.label === item.title || ("option" in change && Object.keys(item.enable).includes(change.option))),
    );

  // The Bridge sends a package the running generation lacks and one it has unchanged both with a null `previous`,
  // so only updated packages are known here: those with the version in use.
  const updated = (item: InstalledPackage) => typeof item.previous === "string";
  /** Packages the next applied build updates, compared with the running generation. */
  const changing = $derived((inventory?.[scope].packages ?? []).filter((item) => updated(item) && (showModules || !item.external)));
  const installed = $derived(
    (inventory?.[scope].packages ?? []).filter(
      (item) =>
        (showModules || !item.external) &&
        (!changingOnly || updated(item)) &&
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
      <p class="line-clamp-2 text-muted-foreground">{result.app?.summary ?? result.d ?? m.package_no_description()}</p>
    </div>
    <span class="shrink-0 pt-0.5 text-xs font-medium">
      {#if pending}<span class="text-prepared-foreground">{pending.kind === "add-package" ? m.software_will_be_installed() : m.software_will_be_removed()}</span>
      {:else if owned}<span class="text-primary">{m.onestep_installed()}</span>
      {:else if result.b}<span class="text-warning">{m.software_broken()}</span>
      {:else if !result.f}<span class="text-muted-foreground">{m.software_not_free()}</span>{/if}
    </span>
  </button>
{/snippet}

<div class="space-y-6" bind:this={content}>
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div class="min-w-0">
      <h1 class="title">{m.nav_software()}</h1>
      <p class="mt-1 text-muted-foreground">{m.software_install_packages_and_turn_on_features({ host: app.host })}</p>
    </div>
    <Segmented
      label={m.software_view()}
      bind:value={view}
      options={[
        { value: "discover", label: m.software_discover() },
        { value: "installed", label: m.onestep_installed(), count: inventory ? inventory.home.packages.filter((p) => !p.external).length + inventory.system.packages.filter((p) => !p.external).length : undefined },
      ]}
    />
  </header>

  <PendingEdits />

  {#if software.error}
    <p class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-destructive">
      <TriangleAlertIcon class="mt-0.5 size-4 shrink-0" />
      <span class="flex-1">{m.software_could_not_read_s_configuration({ host: app.host })} {stored(software.error)}</span>
      <Button variant="ghost" size="sm" onclick={() => software.load(true)}>{m.activity_try_again()}</Button>
    </p>
  {/if}

  {#if view === "discover"}
    <label class="flex h-12 items-center gap-3 rounded-xl border border-field bg-card px-4 shadow-xs focus-within:ring-3 focus-within:ring-ring/30">
      <SearchIcon class="size-5 text-muted-foreground" />
      <input
        bind:this={searchInput}
        bind:value={query}
        onkeydown={searchKey}
        aria-label={m.software_search_apps_and_packages()}
        aria-keyshortcuts="/"
        placeholder={m.software_search_apps_and_over_a_hundred()}
        class="peer w-full bg-transparent text-base outline-none placeholder:text-muted-foreground"
      />
      {#if !query}<kbd class="rounded border bg-muted px-1.5 font-mono text-xs text-muted-foreground peer-focus:hidden" title={m.software_press_to_search()}>/</kbd>{/if}
      {#if software.catalog?.state === "building"}<LoaderIcon class="size-4 animate-spin text-muted-foreground" aria-hidden="true" /><span class="sr-only">{m.software_building_the_package_index()}</span>{/if}
    </label>

    <p class="sr-only" role="status">{query.trim() && software.searched === query ? (software.results.length ? m.software_result({ count: software.results.length }) : m.software_no_packages_match()) : ""}</p>

    {#if query.trim()}
      <div class="-mt-3 flex flex-wrap gap-4 px-1 text-xs">
        <label class="flex items-center gap-1.5"><input type="checkbox" class="focus-ring accent-primary" checked={software.free} onchange={(event) => software.setFilter("free", event.currentTarget.checked)} /> {m.software_free_software_only()}</label>
        <label class="flex items-center gap-1.5"><input type="checkbox" class="focus-ring accent-primary" checked={software.working} onchange={(event) => software.setFilter("working", event.currentTarget.checked)} /> {m.software_hide_packages_marked_as_broken()}</label>
      </div>
      {#if software.catalog?.state === "ready"}
        {#if software.results.length}
          <div class="space-y-5" use:arrows={{ before: () => searchInput }}>
            {#if appResults.length}
              <section class="space-y-2" aria-labelledby="results-apps">
                <h2 id="results-apps" class="heading">{m.software_apps()}</h2>
                <ul role="list" aria-labelledby="results-apps" class="panel divide-y overflow-hidden">
                  {#each appResults as result (result.a)}<li>{@render resultRow(result)}</li>{/each}
                </ul>
              </section>
            {/if}
            {#if otherResults.length}
              <section class="space-y-2" aria-labelledby="results-other">
                <h2 id="results-other" class="heading">{appResults.length ? m.software_other_software() : m.changes_packages()}</h2>
                <p class="-mt-1 text-xs text-muted-foreground">{m.software_command_line_tools_libraries_and_parts()}</p>
                <ul role="list" aria-labelledby="results-other" class="panel divide-y overflow-hidden">
                  {#each otherResults as result (result.a)}<li>{@render resultRow(result)}</li>{/each}
                </ul>
              </section>
            {/if}
          </div>
        {:else if software.searched === query}
          <section class="panel"><EmptyState icon={SearchIcon} title={m.software_no_packages_match_2({ query })}>{m.software_try_the_program_s_name_or()}</EmptyState></section>
        {/if}
        <p class="text-xs text-muted-foreground">
          {m.software_index_of_packages_from_your_locked({ count: software.catalog.count ?? 0, when: relative(software.catalog.builtAt, app.now) })}
          <button class="focus-ring rounded text-primary hover:underline" onclick={() => software.buildCatalog(true)}>{m.software_rebuild()}</button>
        </p>
      {:else if software.catalog?.state === "failed"}
        <section class="panel">
          <EmptyState icon={TriangleAlertIcon} title={m.software_the_package_index_could_not_be()}>
            {stored(software.catalog.error)}
            {#snippet actions()}<Button variant="outline" onclick={() => software.buildCatalog(true)}><RefreshIcon /> {m.activity_try_again()}</Button>{/snippet}
          </EmptyState>
        </section>
      {:else}
        <section class="panel">
          <EmptyState icon={LoaderIcon} title={m.software_building_the_package_index()}>
            {m.software_nix_manager_reads_every_package_in()}
          </EmptyState>
        </section>
      {/if}
    {:else if !inventory}
      <section class="panel">
        <EmptyState icon={LoaderIcon} title={m.software_reading_s_configuration({ host: app.host })}>
          {m.software_evaluating_what_the_home_and_system()}
        </EmptyState>
      </section>
    {:else}
      <div class="space-y-6" use:arrows={{ before: () => searchInput }}>
      <section class="space-y-3" aria-labelledby="bundles-heading">
        <h2 id="bundles-heading" class="heading">{m.software_starting_points()}</h2>
        <ul role="list" aria-labelledby="bundles-heading" class="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
          {#each bundles as item (item.id)}
            <li>
              <button
                data-nav
                class="focus-ring flex h-full w-full flex-col gap-2 rounded-xl border bg-card p-3 text-left transition-colors hover:border-primary/40"
                onclick={() => (bundle = item)}
              >
                <span class="grid size-9 place-items-center rounded-lg bg-primary/10 text-primary"><ExperienceIcon name={item.icon} class="size-4.5" /></span>
                <span class="font-semibold">{stored(item.title)}</span>
                <span class="text-muted-foreground">{stored(item.summary)}</span>
              </button>
            </li>
          {/each}
        </ul>
      </section>
      {#if appsOn}
        <section class="space-y-3" aria-labelledby="apps-heading">
          <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
            <h2 id="apps-heading" class="heading">{m.software_apps()}</h2>
            {#if software.apps.length}<p class="text-xs text-muted-foreground">{m.software_apps_with_details_from_flathub({ count: software.apps.length })}</p>{/if}
          </div>
          {#if software.apps.length}
            <div class="flex flex-wrap gap-1.5" role="group" aria-label={m.software_app_categories()}>
              {#each [{ id: "featured" as const, label: m.software_popular() }, ...shelves.map((item) => ({ id: item.id, label: appCategoryLabel(item.id) }))] as item (item.id)}
                <button
                  class="focus-ring rounded-full border px-3 py-1 text-xs font-medium transition-colors
                    {shelf === item.id ? 'border-primary bg-primary text-primary-foreground' : 'bg-card text-muted-foreground hover:border-primary/40 hover:text-foreground'}"
                  aria-pressed={shelf === item.id}
                  onclick={() => (shelf = item.id)}
                >{item.label}</button>
              {/each}
            </div>
            <ul role="list" aria-labelledby="apps-heading" class="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
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
                        {#if pending}<span class="shrink-0 text-xs font-medium text-prepared-foreground">{m.bundle_pending()}</span>
                        {:else if owned}<span class="shrink-0 text-xs font-medium text-primary">{m.onestep_installed()}</span>{/if}
                      </span>
                      <span class="line-clamp-2 text-muted-foreground">{item.app!.summary ?? item.d ?? ""}</span>
                    </span>
                  </button>
                </li>
              {/each}
            </ul>
            {#if shown.length > 18}
              <Button variant="outline" size="sm" onclick={() => (showAll = !showAll)}>
                {showAll ? m.software_show_fewer() : m.software_show_all_apps({ count: shown.length })}
              </Button>
            {/if}
          {:else if software.appStatus?.state === "failed"}
            <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2 text-xs">
              <TriangleAlertIcon class="size-4 shrink-0 text-warning" />
              <span class="flex-1">{stored(software.appStatus.error)} {m.software_searching_still_finds_every_package()}</span>
              <button class="focus-ring rounded text-primary hover:underline" onclick={() => software.loadApps(true)}>{m.activity_try_again()}</button>
            </p>
          {:else if software.catalog?.state === "failed"}
            <p class="text-muted-foreground">{m.software_the_app_list_needs_the_package()} {stored(software.catalog.error)}</p>
          {:else}
            <p class="flex items-center gap-2 text-muted-foreground">
              <LoaderIcon class="size-4 animate-spin" />
              {software.catalog?.state === "ready" ? m.software_downloading_app_details_from_flathub() : m.software_reading_every_package_in_your_nixpkgs()}
            </p>
          {/if}
        </section>
        <h2 class="heading">{m.bundle_features()}</h2>
      {/if}
      {#each categories as [name, title] (name)}
        {@const items = visible.filter((item) => item.category === name)}
        {#if items.length}
          <section class="space-y-2" aria-labelledby="category-{name}">
            <svelte:element this={appsOn ? "h3" : "h2"} id="category-{name}" class={appsOn ? "label" : "heading"}>{title}</svelte:element>
            <ul role="list" aria-labelledby="category-{name}" class="grid gap-2 sm:grid-cols-2">
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
                        <span class="font-semibold">{stored(item.title)}</span>
                        {#if pending}<span class="text-xs font-medium text-prepared-foreground">{m.bundle_pending()}</span>
                        {:else if on}<span class="text-xs font-medium text-primary">{m.setup_flakes_on()}</span>{/if}
                      </span>
                      <span class="block text-muted-foreground">{stored(item.summary)}</span>
                      <span class="mt-0.5 block text-[0.72rem] text-muted-foreground">{item.scope === "system" ? m.scope_system() : m.scope_home()}</span>
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
          label={m.overview_configuration()}
          bind:value={scope}
          options={[
            { value: "home" as Target, label: m.scope_home(), count: inventory?.home.packages.filter((p) => !p.external).length },
            { value: "system" as Target, label: m.scope_system(), count: inventory?.system.packages.filter((p) => !p.external).length },
          ]}
        />
        {#if changing.length}
          <button
            class="focus-ring rounded-md px-2 py-1 text-xs font-medium {changingOnly ? 'bg-accent text-accent-foreground' : 'text-primary hover:underline'}"
            aria-pressed={changingOnly}
            onclick={() => (changingOnly = !changingOnly)}
            title={m.software_compared_with_the_version_in_use()}
          >{m.software_package_changes_on_the_next_apply({ count: changing.length })}</button>
        {/if}
        <label class="ml-auto flex h-8 w-56 items-center gap-2 rounded-md border border-field bg-card px-2 text-muted-foreground focus-within:ring-2 focus-within:ring-ring/40">
          <SearchIcon class="size-3.5" />
          <input bind:this={filterInput} bind:value={filter} aria-label={m.software_filter_installed_packages()} aria-keyshortcuts="/" placeholder={m.software_filter()} class="w-full bg-transparent text-xs text-foreground outline-none placeholder:text-muted-foreground" />
        </label>
      </div>
      {#if !inventory}
        <EmptyState icon={software.loading ? LoaderIcon : PackageIcon} title={software.loading ? m.software_reading_s_configuration({ host: app.host }) : m.software_not_read_yet()} />
      {:else if installed.length}
        <div class="overflow-hidden rounded-lg border">
          <table class="w-full text-left text-xs">
            <thead class="bg-muted/50 text-muted-foreground">
              <tr>
                <th class="px-3 py-2 font-medium">{m.packages_package()}</th>
                <th class="px-3 py-2 font-medium">{m.packages_version()}</th>
                <th class="px-3 py-2 font-medium">{m.software_installed_by()}</th>
                <th class="w-24 px-3 py-2"><span class="sr-only">{m.software_actions()}</span></th>
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
                    {#if updated(item)}
                      <span title={m.software_the_version_in_use_has_your({ previous: String(item.previous), version: item.version ?? "?" })}>
                        <span class="text-muted-foreground">{item.previous} →</span> <span class="font-semibold text-primary">{item.version ?? "?"}</span>
                      </span>
                    {:else}
                      {item.version ?? ""}
                    {/if}
                  </td>
                  <td class="px-3 py-1.5" title={item.file}>
                    {#if item.external}<span class="text-muted-foreground">{origin(item.file)}</span>
                    {:else}{category(item.file)}<span class="block font-mono text-[0.68rem] text-muted-foreground">{item.file}</span>{/if}
                  </td>
                  <td class="px-3 py-1.5 text-right">
                    {#if removing}<span class="font-medium text-prepared-foreground">{m.software_will_be_removed()}</span>
                    {:else if item.attr}<Button variant="ghost" size="xs" aria-label={m.pending_remove({ name: item.name })} disabled={software.saving} onclick={() => removeInstalled(item)}>{m.package_remove()}</Button>
                    {:else if !item.external}<span class="text-muted-foreground" title={m.software_this_item_is_computed_edit_it()}>{m.software_computed()}</span>{/if}
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {:else}
        <p class="rounded-lg border border-dashed px-3 py-6 text-center text-muted-foreground">{filter || changingOnly ? m.software_no_installed_packages_match() : m.software_nothing_installed_here_yet()}</p>
      {/if}
      {#if moduleCount}
        <button class="focus-ring mt-3 rounded text-xs font-medium text-primary hover:underline" onclick={() => (showModules = !showModules)}>
          {showModules
            ? m.software_hide_package_added_by_settings_and({ count: moduleCount })
            : m.software_show_package_added_by_settings_and({ count: moduleCount })}
        </button>
      {/if}
    </section>
  {/if}
</div>

<PackageDialog bind:entry />
<ExperienceDialog bind:experience {start} />
<BundleDialog bind:bundle />
