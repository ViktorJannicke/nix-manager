<script lang="ts">
  import { onMount } from "svelte";
  import LayoutIcon from "@lucide/svelte/icons/layout-dashboard";
  import CloudDownloadIcon from "@lucide/svelte/icons/cloud-download";
  import GitCompareIcon from "@lucide/svelte/icons/git-compare";
  import HistoryIcon from "@lucide/svelte/icons/history";
  import ListIcon from "@lucide/svelte/icons/list";
  import SettingsIcon from "@lucide/svelte/icons/settings";
  import PackageIcon from "@lucide/svelte/icons/package";
  import MonitorIcon from "@lucide/svelte/icons/monitor";
  import SnowflakeIcon from "@lucide/svelte/icons/snowflake";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { Button } from "$lib/components/ui/button";
  import { app, type Page } from "$lib/app.svelte";
  import { hasToken } from "$lib/api";
  import { t } from "$lib/i18n";
  import { actionLabels } from "../src/domain";
  import Toasts from "$lib/components/app/Toasts.svelte";
  import T from "$lib/components/app/T.svelte";
  import ConfirmDialog from "$lib/components/app/ConfirmDialog.svelte";
  import OneStepDialog from "$lib/components/app/OneStepDialog.svelte";
  import BuildProgress from "$lib/components/app/BuildProgress.svelte";
  import Overview from "./pages/Overview.svelte";
  import Updates from "./pages/Updates.svelte";
  import Changes from "./pages/Changes.svelte";
  import Generations from "./pages/Generations.svelte";
  import Activity from "./pages/Activity.svelte";
  import Settings from "./pages/Settings.svelte";
  import Software from "./pages/Software.svelte";
  import Computer from "./pages/Computer.svelte";
  import Setup from "./pages/Setup.svelte";

  /** Started before there is a configuration: only the setup assistant runs. */
  const setup = new URLSearchParams(location.search).has("setup") || new URLSearchParams(location.search).has("problem");
  onMount(() => {
    if (setup) app.applyTheme(app.theme);
    else app.start();
  });

  const nav = $derived<{ id: Page; label: string; icon: typeof LayoutIcon; count?: number }[]>([
    { id: "overview", label: t("Overview"), icon: LayoutIcon },
    { id: "software", label: t("Software"), icon: PackageIcon, count: app.snapshot?.draft.changes.length || undefined },
    { id: "computer", label: t("Computer"), icon: MonitorIcon },
    { id: "changes", label: t("Changes"), icon: GitCompareIcon, count: app.reviews.length || undefined },
    { id: "updates", label: t("Updates"), icon: CloudDownloadIcon, count: app.inputUpdates.length + app.packageUpdates.length || undefined },
    { id: "generations", label: t("Restore points"), icon: HistoryIcon },
    { id: "activity", label: t("Activity"), icon: ListIcon },
    { id: "settings", label: t("Settings"), icon: SettingsIcon },
  ]);
  const running = $derived(app.active);
  const runningStep = $derived(running?.steps?.find((step) => step.status === "running"));
</script>

<svelte:head><title>{running ? `${t(actionLabels[running.request.action])}… ` : ""}{setup ? "" : `${nav.find((item) => item.id === app.page)?.label} – `}Nix Manager</title></svelte:head>

{#if !hasToken()}
  <main class="grid min-h-screen place-items-center p-8 text-center">
    <div class="max-w-sm space-y-2">
      <h1 class="heading">{t("Open Nix Manager from the app menu or tray")}</h1>
      <p class="text-muted-foreground"><T text={"This address needs the private session link Nix Manager creates when it starts. Run {command} to open a new window."}>{#snippet children()}<code>nix-manager ui</code>{/snippet}</T></p>
    </div>
  </main>
{:else if setup}
  <Setup />
{:else}
  <a href="#main" class="focus-ring fixed top-2 left-2 z-50 -translate-y-16 rounded-lg border bg-card px-3 py-2 font-medium shadow-lg focus:translate-y-0">{t("Skip to content")}</a>
  <!-- Announces each step of a running operation; finishing and failing are announced by the toasts. -->
  <p class="sr-only" role="status">{running ? `${t(actionLabels[running.request.action])}: ${t(runningStep?.label ?? running.step)}` : ""}</p>
  <div class="flex h-screen overflow-hidden">
    <nav class="flex w-52 shrink-0 flex-col bg-sidebar px-3 py-4" aria-label={t("Sections")}>
      <div class="mb-6 flex items-center gap-2 px-2">
        <SnowflakeIcon class="size-5 text-primary" />
        <span class="font-bold tracking-tight">Nix Manager</span>
      </div>
      <ul class="space-y-0.5">
        {#each nav as item (item.id)}
          <li>
            <button
              class="focus-ring flex h-9 w-full items-center gap-3 rounded-lg px-2.5 text-left transition-colors
                {app.page === item.id ? 'bg-card font-semibold text-foreground' : 'text-muted-foreground hover:bg-card/60 hover:text-foreground'}"
              aria-current={app.page === item.id ? "page" : undefined}
              onclick={() => app.navigate(item.id)}
            >
              <item.icon class="size-4 {app.page === item.id ? 'text-primary' : ''}" />
              <span class="flex-1">{item.label}</span>
              {#if item.id === "activity" && running}
                <LoaderIcon class="size-3.5 animate-spin text-primary" /><span class="sr-only">{t(", running")}</span>
              {:else if item.count}
                <span class="numeric text-[0.8rem] {item.id === 'changes' || item.id === 'software' ? 'font-semibold text-prepared-foreground' : ''}">{item.count}</span>
              {/if}
            </button>
          </li>
        {/each}
      </ul>
      <div class="mt-auto space-y-2 px-2 text-[0.8rem]">
        {#if app.foreignHost}
          <button class="focus-ring flex w-full items-start gap-1.5 rounded text-left text-warning" onclick={() => app.navigate("settings")}>
            <TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0" /> {t("Targeting {host}", { host: app.host })}
          </button>
        {/if}
        <p class="flex items-center gap-1.5 text-warning" role="status">
          {#if app.snapshot && !app.connected}<LoaderIcon class="size-3.5 animate-spin" /> {t("Reconnecting")}{/if}
        </p>
      </div>
    </nav>

    <div class="flex min-w-0 flex-1 flex-col">
      {#if running && app.page !== "overview" && app.page !== "activity"}
        <div class="flex items-center gap-4 border-b bg-card px-6 py-2">
          <span class="font-semibold">{t(actionLabels[running.request.action])}</span>
          <span class="min-w-0 flex-1 truncate text-muted-foreground">{t(runningStep?.label ?? running.step)}</span>
          {#if runningStep}<div class="w-56"><BuildProgress compact progress={app.stepProgress(running, runningStep)} /></div>{/if}
          <Button variant="ghost" size="sm" onclick={() => { app.selectOperation(running.id); app.navigate("activity"); }}>{t("Show")}</Button>
        </div>
      {/if}
      <main id="main" tabindex="-1" class="outline-none scroll-thin min-h-0 flex-1 {app.page === 'activity' ? 'overflow-hidden' : 'overflow-auto'}">
        <div class="mx-auto h-full max-w-5xl px-8 py-7">
          {#if !app.snapshot}
            <div class="grid h-full place-items-center text-muted-foreground">
              <p class="flex items-center gap-2" role="status"><LoaderIcon class="size-4 animate-spin" /> {t("Reading this computer's state")}</p>
            </div>
          {:else if app.page === "overview"}<Overview />
          {:else if app.page === "software"}<Software />
          {:else if app.page === "computer"}<Computer />
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
{/if}
