<script lang="ts">
  import SearchIcon from "@lucide/svelte/icons/search";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import PackageIcon from "@lucide/svelte/icons/package";
  import RefreshIcon from "@lucide/svelte/icons/refresh-cw";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { origin, software } from "$lib/software.svelte";
  import { plural, relative } from "$lib/format";
  import { experiences, type Experience } from "../../src/curated";
  import type { CatalogEntry, InstalledPackage, Target } from "../../src/software";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import PendingEdits from "$lib/components/app/PendingEdits.svelte";
  import PackageDialog from "$lib/components/app/PackageDialog.svelte";
  import ExperienceDialog from "$lib/components/app/ExperienceDialog.svelte";
  import ExperienceIcon from "$lib/components/app/ExperienceIcon.svelte";
  import { arrows, typing } from "$lib/keyboard";

  let view = $state<"discover" | "installed">("discover");
  let query = $state("");
  let scope = $state<Target>("home");
  let filter = $state("");
  let showModules = $state(false);
  let changingOnly = $state(false);
  let entry = $state<CatalogEntry>();
  let experience = $state<Experience>();
  let start = $state(false);
  let searchInput = $state<HTMLInputElement>();
  let filterInput = $state<HTMLInputElement>();
  let content = $state<HTMLElement>();

  // "/" finds: the search on Discover, the filter on Installed.
  function shortcut(event: KeyboardEvent) {
    if (event.key !== "/" || typing(event) || entry || experience) return;
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
  const categories = ["Games", "Development", "Internet", "Media", "Productivity", "System"] as const;
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

<div class="space-y-6" bind:this={content}>
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div class="min-w-0">
      <h1 class="title">Software</h1>
      <p class="mt-1 text-muted-foreground">Install packages and turn on features for {app.host}. Your edits wait here until you prepare them.</p>
    </div>
    <Segmented
      label="View"
      bind:value={view}
      options={[
        { value: "discover", label: "Discover" },
        { value: "installed", label: "Installed", count: inventory ? inventory.home.packages.filter((p) => !p.external).length + inventory.system.packages.filter((p) => !p.external).length : undefined },
      ]}
    />
  </header>

  <PendingEdits />

  {#if software.error}
    <p class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-destructive">
      <TriangleAlertIcon class="mt-0.5 size-4 shrink-0" />
      <span class="flex-1">Could not read {app.host}'s configuration. {software.error}</span>
      <Button variant="ghost" size="sm" onclick={() => software.load(true)}>Try again</Button>
    </p>
  {/if}

  {#if view === "discover"}
    <label class="flex h-12 items-center gap-3 rounded-xl border bg-card px-4 shadow-xs focus-within:ring-3 focus-within:ring-ring/30">
      <SearchIcon class="size-5 text-muted-foreground" />
      <input
        bind:this={searchInput}
        bind:value={query}
        onkeydown={searchKey}
        aria-label="Search packages"
        aria-keyshortcuts="/"
        placeholder="Search over a hundred thousand packages"
        class="peer w-full bg-transparent text-base outline-none placeholder:text-muted-foreground"
      />
      {#if !query}<kbd class="rounded border bg-muted px-1.5 font-mono text-xs text-muted-foreground peer-focus:hidden" title="Press / to search">/</kbd>{/if}
      {#if software.catalog?.state === "building"}<LoaderIcon class="size-4 animate-spin text-muted-foreground" aria-label="Building the package index" />{/if}
    </label>

    {#if query.trim()}
      <div class="-mt-3 flex flex-wrap gap-4 px-1 text-xs">
        <label class="flex items-center gap-1.5"><input type="checkbox" class="focus-ring accent-primary" checked={software.free} onchange={(event) => software.setFilter("free", event.currentTarget.checked)} /> Free software only</label>
        <label class="flex items-center gap-1.5"><input type="checkbox" class="focus-ring accent-primary" checked={software.working} onchange={(event) => software.setFilter("working", event.currentTarget.checked)} /> Hide packages marked as broken</label>
      </div>
      {#if software.catalog?.state === "ready"}
        {#if software.results.length}
          <ul class="panel divide-y overflow-hidden" use:arrows={{ before: () => searchInput }}>
            {#each software.results as result (result.a)}
              {@const owned = software.installed(result)}
              {@const pending = software.pendingPackage(`pkgs.${result.a}`)}
              <li>
                <button data-nav class="focus-ring flex w-full items-start gap-3 px-4 py-3 text-left hover:bg-muted/40 focus-visible:bg-muted/40" onclick={() => (entry = result)}>
                  <div class="min-w-0 flex-1">
                    <p class="flex flex-wrap items-baseline gap-x-2">
                      <span class="font-semibold">{result.n ?? result.a}</span>
                      <span class="font-mono text-[0.72rem] text-muted-foreground">{result.v ?? ""}</span>
                      {#if result.a !== result.n}<span class="font-mono text-[0.72rem] text-muted-foreground">{result.a}</span>{/if}
                    </p>
                    <p class="line-clamp-2 text-muted-foreground">{result.d ?? "No description."}</p>
                  </div>
                  <span class="shrink-0 pt-0.5 text-xs font-medium">
                    {#if pending}<span class="text-prepared-foreground">{pending.kind === "add-package" ? "Will be installed" : "Will be removed"}</span>
                    {:else if owned}<span class="text-primary">Installed</span>
                    {:else if result.b}<span class="text-warning">Broken</span>
                    {:else if !result.f}<span class="text-muted-foreground">Not free</span>{/if}
                  </span>
                </button>
              </li>
            {/each}
          </ul>
        {:else if software.searched === query}
          <section class="panel"><EmptyState icon={SearchIcon} title="No packages match “{query}”">Try the program's name, or a word from what it does.</EmptyState></section>
        {/if}
        <p class="text-xs text-muted-foreground">
          Index of {software.catalog.count?.toLocaleString()} packages from your locked nixpkgs and overlays, built {relative(software.catalog.builtAt, app.now)}.
          <button class="focus-ring rounded text-primary hover:underline" onclick={() => software.buildCatalog(true)}>Rebuild</button>
        </p>
      {:else if software.catalog?.state === "failed"}
        <section class="panel">
          <EmptyState icon={TriangleAlertIcon} title="The package index could not be built">
            {software.catalog.error}
            {#snippet actions()}<Button variant="outline" onclick={() => software.buildCatalog(true)}><RefreshIcon /> Try again</Button>{/snippet}
          </EmptyState>
        </section>
      {:else}
        <section class="panel">
          <EmptyState icon={LoaderIcon} title="Building the package index">
            Nix Manager reads every package in your locked nixpkgs once, which takes about half a minute. It is built again after nixpkgs updates.
          </EmptyState>
        </section>
      {/if}
    {:else if !inventory}
      <section class="panel">
        <EmptyState icon={LoaderIcon} title="Reading {app.host}'s configuration">
          Evaluating what the home and system configurations contain. This takes a few seconds.
        </EmptyState>
      </section>
    {:else}
      <div class="space-y-6" use:arrows={{ before: () => searchInput }}>
      {#each categories as name (name)}
        {@const items = visible.filter((item) => item.category === name)}
        {#if items.length}
          <section class="space-y-2" aria-labelledby="category-{name}">
            <h2 id="category-{name}" class="heading">{name}</h2>
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
                        <span class="font-semibold">{item.title}</span>
                        {#if pending}<span class="text-xs font-medium text-prepared-foreground">Pending</span>
                        {:else if on}<span class="text-xs font-medium text-primary">On</span>{/if}
                      </span>
                      <span class="block text-muted-foreground">{item.summary}</span>
                      <span class="mt-0.5 block text-[0.72rem] text-muted-foreground">{item.scope === "system" ? "System" : "Your user"}</span>
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
          label="Configuration"
          bind:value={scope}
          options={[
            { value: "home" as Target, label: "Your user", count: inventory?.home.packages.filter((p) => !p.external).length },
            { value: "system" as Target, label: "System", count: inventory?.system.packages.filter((p) => !p.external).length },
          ]}
        />
        {#if changing.length}
          <button
            class="focus-ring rounded-md px-2 py-1 text-xs font-medium {changingOnly ? 'bg-accent text-accent-foreground' : 'text-primary hover:underline'}"
            aria-pressed={changingOnly}
            onclick={() => (changingOnly = !changingOnly)}
            title="Compared with the version in use now. Apply your configuration to get them."
          >{plural(changing.length, "package")} {changing.length === 1 ? "changes" : "change"} on the next apply</button>
        {/if}
        <label class="ml-auto flex h-8 w-56 items-center gap-2 rounded-md border bg-card px-2 text-muted-foreground focus-within:ring-2 focus-within:ring-ring/40">
          <SearchIcon class="size-3.5" />
          <input bind:this={filterInput} bind:value={filter} aria-label="Filter installed packages" aria-keyshortcuts="/" placeholder="Filter (/)" class="w-full bg-transparent text-xs text-foreground outline-none placeholder:text-muted-foreground" />
        </label>
      </div>
      {#if !inventory}
        <EmptyState icon={software.loading ? LoaderIcon : PackageIcon} title={software.loading ? `Reading ${app.host}'s configuration` : "Not read yet"} />
      {:else if installed.length}
        <div class="overflow-hidden rounded-lg border">
          <table class="w-full text-left text-xs">
            <thead class="bg-muted/50 text-muted-foreground">
              <tr>
                <th class="px-3 py-2 font-medium">Package</th>
                <th class="px-3 py-2 font-medium">Version</th>
                <th class="px-3 py-2 font-medium">Installed by</th>
                <th class="w-24 px-3 py-2"><span class="sr-only">Actions</span></th>
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
                      <span title="The version in use has {item.previous}; your configuration builds {item.version}.">
                        <span class="text-muted-foreground">{item.previous} →</span> <span class="font-semibold text-primary">{item.version ?? "?"}</span>
                      </span>
                    {:else}
                      {item.version ?? ""}
                      {#if item.previous === null}<span class="ml-1 font-sans text-[0.68rem] font-medium text-primary" title="Not in the version in use now.">new</span>{/if}
                    {/if}
                  </td>
                  <td class="px-3 py-1.5" title={item.file}>
                    {#if item.external}<span class="text-muted-foreground">{origin(item.file)}</span>
                    {:else}{category(item.file)}<span class="block font-mono text-[0.68rem] text-muted-foreground">{item.file}</span>{/if}
                  </td>
                  <td class="px-3 py-1.5 text-right">
                    {#if removing}<span class="font-medium text-prepared-foreground">Will be removed</span>
                    {:else if item.attr}<Button variant="ghost" size="xs" disabled={software.saving} onclick={() => removeInstalled(item)}>Remove</Button>
                    {:else if !item.external}<span class="text-muted-foreground" title="This item is computed; edit it in the file.">Computed</span>{/if}
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {:else}
        <p class="rounded-lg border border-dashed px-3 py-6 text-center text-muted-foreground">{filter || changingOnly ? "No installed packages match." : "Nothing installed here yet."}</p>
      {/if}
      {#if moduleCount}
        <button class="focus-ring mt-3 rounded text-xs font-medium text-primary hover:underline" onclick={() => (showModules = !showModules)}>
          {showModules ? "Hide" : "Show"} {plural(moduleCount, "package")} added by settings and modules
        </button>
      {/if}
    </section>
  {/if}
</div>

<PackageDialog bind:entry />
<ExperienceDialog bind:experience {start} />
