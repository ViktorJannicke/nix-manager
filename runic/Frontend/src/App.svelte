<svelte:options runes={true} />

<script lang="ts">
  import { onMount } from "svelte";
  import { useView } from "@runic-artifex/svelte/views";
  import LayoutIcon from "@lucide/svelte/icons/layout-dashboard";
  import CloudDownloadIcon from "@lucide/svelte/icons/cloud-download";
  import GitCompareIcon from "@lucide/svelte/icons/git-compare";
  import PackageIcon from "@lucide/svelte/icons/package";
  import MonitorIcon from "@lucide/svelte/icons/monitor";
  import HistoryIcon from "@lucide/svelte/icons/history";
  import ListIcon from "@lucide/svelte/icons/list";
  import SettingsIcon from "@lucide/svelte/icons/settings";
  import SnowflakeIcon from "@lucide/svelte/icons/snowflake";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { connectDashboard } from "./generated/dashboard.js";
  import { Button } from "$lib/components/ui/button";
  import { app, type Page } from "$lib/app.svelte";
  import { actionLabel, m, setLocale, stored } from "$lib/i18n";
  import Toasts from "$lib/components/app/Toasts.svelte";
  import ConfirmDialog from "$lib/components/app/ConfirmDialog.svelte";
  import BuildProgress from "$lib/components/app/BuildProgress.svelte";
  import OneStepDialog from "$lib/components/app/OneStepDialog.svelte";
  import Overview from "./pages/Overview.svelte";
  import Updates from "./pages/Updates.svelte";
  import Changes from "./pages/Changes.svelte";
  import Upcoming from "./pages/Upcoming.svelte";
  import Generations from "./pages/Generations.svelte";
  import Activity from "./pages/Activity.svelte";
  import Settings from "./pages/Settings.svelte";

  const dashboard = useView(() => ({ connect: connectDashboard }));
  const operations = useView(() => dashboard.state?.operations);
  const reviews = useView(() => dashboard.state?.reviews);
  const system = useView(() => dashboard.state?.system);
  const software = useView(() => dashboard.state?.software);
  app.dashboardView = dashboard;
  app.operationsView = operations;
  app.reviewsView = reviews;
  app.systemView = system;
  app.softwareView = software;

  onMount(() => app.start());

  // The ViewModel says which language to speak: the chosen one, or the desktop's.
  const locale = $derived(dashboard.state?.locale ?? "en");
  $effect.pre(() => setLocale(locale));

  // The chosen theme, or the desktop's through the page's colour scheme.
  $effect(() => {
    const theme = dashboard.state?.theme ?? "system";
    const media = matchMedia("(prefers-color-scheme: dark)");
    const apply = () =>
      document.documentElement.classList.toggle("dark", theme === "dark" || (theme === "system" && media.matches));
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  });

  $effect(() => app.settle(app.history));

  // The configuration's history is read again whenever its last commit changes.
  $effect(() => {
    void app.system?.repository.revision;
    const client = reviews.client;
    if (client && app.system) void client.refreshHistory().catch(() => {});
  });

  const nav = $derived.by(() => {
    void locale;
    return [
      { id: "overview" as Page, label: m.nav_overview(), icon: LayoutIcon, count: undefined, prepared: false },
      { id: "software" as Page, label: m.nav_software(), icon: PackageIcon, count: app.pendingChanges.length || undefined, prepared: true },
      { id: "computer" as Page, label: m.nav_computer(), icon: MonitorIcon, count: undefined, prepared: false },
      { id: "changes" as Page, label: m.nav_changes(), icon: GitCompareIcon, count: app.reviews.length || undefined, prepared: true },
      { id: "updates" as Page, label: m.nav_updates(), icon: CloudDownloadIcon, count: app.inputUpdates.length + app.packageUpdates.length || undefined, prepared: false },
      { id: "generations" as Page, label: m.nav_restore_points(), icon: HistoryIcon, count: undefined, prepared: false },
      { id: "activity" as Page, label: m.nav_activity(), icon: ListIcon, count: undefined, prepared: false },
      { id: "settings" as Page, label: m.nav_settings(), icon: SettingsIcon, count: undefined, prepared: false },
    ];
  });
  const running = $derived(app.active);
  const runningStep = $derived(running?.steps?.find((step) => step.status === "running"));
  const title = $derived.by(() => {
    void locale;
    const page = nav.find((item) => item.id === app.page)?.label;
    return `${running ? `${actionLabel(running.request.action)}… ` : ""}${page} – Nix Manager`;
  });
</script>

<svelte:head><title>{title}</title></svelte:head>

{#key locale}
  <a href="#main" class="focus-ring fixed top-2 left-2 z-50 -translate-y-16 rounded-lg border bg-card px-3 py-2 font-medium shadow-lg focus:translate-y-0">{m.nav_skip_to_content()}</a>
  <!-- Announces each step of a running operation; finishing and failing are announced by the toasts. -->
  <p class="sr-only" role="status">{running ? `${actionLabel(running.request.action)}: ${stored(runningStep?.label ?? running.step)}` : ""}</p>
  <div class="flex h-screen overflow-hidden">
    <nav class="flex w-60 shrink-0 flex-col bg-sidebar px-3 py-4" aria-label={m.nav_sections()}>
      <div class="mb-6 flex items-center gap-2 px-2">
        <SnowflakeIcon class="size-5 text-primary" />
        <span class="font-bold tracking-tight">Nix Manager</span>
      </div>
      <ul role="list" class="space-y-0.5">
        {#each nav as item (item.id)}
          <li>
            <button
              class="focus-ring flex h-9 w-full items-center gap-3 rounded-lg px-2.5 text-left transition-colors
                {app.page === item.id ? 'bg-card font-semibold text-foreground' : 'text-muted-foreground hover:bg-card/60 hover:text-foreground'}"
              aria-current={app.page === item.id ? "page" : undefined}
              onclick={() => app.navigate(item.id)}
            >
              <item.icon class="size-4 shrink-0 {app.page === item.id ? 'text-primary' : ''}" />
              <span class="flex-1">{item.label}</span>
              {#if item.id === "activity" && running}
                <LoaderIcon class="size-3.5 shrink-0 animate-spin text-primary" /><span class="sr-only">{m.nav_running()}</span>
              {:else if item.count}
                <span class="numeric shrink-0 text-[0.8rem] {item.prepared ? 'font-semibold text-prepared-foreground' : ''}">{item.count}</span>
              {/if}
            </button>
          </li>
        {/each}
      </ul>
      <div class="mt-auto space-y-2 px-2 text-[0.8rem]">
        {#if app.foreignHost}
          <button class="focus-ring flex w-full items-start gap-1.5 rounded text-left text-warning" onclick={() => app.navigate("settings")}>
            <TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0" /> {m.nav_targeting({ host: app.host })}
          </button>
        {/if}
        <p class="flex items-center gap-1.5 text-warning" role="status">
          {#if dashboard.state && (dashboard.error || operations.error)}<LoaderIcon class="size-3.5 animate-spin" /> {m.nav_reconnecting()}{/if}
        </p>
      </div>
    </nav>

    <div class="flex min-w-0 flex-1 flex-col">
      {#if running && app.page !== "overview" && app.page !== "activity"}
        <div class="flex items-center gap-4 border-b bg-card px-6 py-2">
          <span class="font-semibold">{actionLabel(running.request.action)}</span>
          <span class="min-w-0 flex-1 truncate text-muted-foreground">{stored(runningStep?.label ?? running.step)}</span>
          {#if runningStep}<div class="w-56"><BuildProgress compact progress={runningStep.progress} /></div>{/if}
          <Button variant="ghost" size="sm" onclick={() => app.show(running.id)}>{m.nav_show()}</Button>
        </div>
      {/if}
      <main id="main" tabindex="-1" class="outline-none scroll-thin min-h-0 flex-1 {app.page === 'activity' ? 'overflow-hidden' : 'overflow-auto'}">
        <div class="mx-auto h-full max-w-5xl px-8 py-7">
          {#if !app.dashboard || !app.operations}
            <div class="grid h-full place-items-center text-muted-foreground">
              {#if dashboard.error && !dashboard.state}
                <p role="alert">{m.nav_not_connected()}</p>
              {:else}
                <p class="flex items-center gap-2" role="status"><LoaderIcon class="size-4 animate-spin" /> {m.nav_reading_state()}</p>
              {/if}
            </div>
          {:else if app.page === "overview"}<Overview />
          {:else if app.page === "software"}<Upcoming title={m.nav_software()} />
          {:else if app.page === "computer"}<Upcoming title={m.nav_computer()} />
          {:else if app.page === "updates"}<Updates />
          {:else if app.page === "changes"}<Changes />
          {:else if app.page === "generations"}<Generations />
          {:else if app.page === "activity"}<Activity />
          {:else}<Settings />{/if}
        </div>
      </main>
    </div>
  </div>
  <ConfirmDialog />
  <OneStepDialog />
  <Toasts />
{/key}
