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
  import { actionLabels } from "../src/domain";
  import Toasts from "$lib/components/app/Toasts.svelte";
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

  onMount(() => app.start());

  const nav = $derived<{ id: Page; label: string; icon: typeof LayoutIcon; count?: number }[]>([
    { id: "overview", label: "Overview", icon: LayoutIcon },
    { id: "software", label: "Software", icon: PackageIcon, count: app.snapshot?.draft.changes.length || undefined },
    { id: "computer", label: "Computer", icon: MonitorIcon },
    { id: "changes", label: "Changes", icon: GitCompareIcon, count: app.reviews.length || undefined },
    { id: "updates", label: "Updates", icon: CloudDownloadIcon, count: app.inputUpdates.length + app.packageUpdates.length || undefined },
    { id: "generations", label: "Restore points", icon: HistoryIcon },
    { id: "activity", label: "Activity", icon: ListIcon },
    { id: "settings", label: "Settings", icon: SettingsIcon },
  ]);
  const running = $derived(app.active);
  const runningStep = $derived(running?.steps?.find((step) => step.status === "running"));
</script>

<svelte:head><title>{running ? `${actionLabels[running.request.action]}… ` : ""}Nix Manager</title></svelte:head>

{#if !hasToken()}
  <main class="grid min-h-screen place-items-center p-8 text-center">
    <div class="max-w-sm space-y-2">
      <h1 class="heading">Open Nix Manager from the app menu or tray</h1>
      <p class="text-muted-foreground">This address needs the private session link Nix Manager creates when it starts. Run <code>nix-manager ui</code> to open a new window.</p>
    </div>
  </main>
{:else}
  <div class="flex h-screen overflow-hidden">
    <nav class="flex w-52 shrink-0 flex-col bg-sidebar px-3 py-4" aria-label="Sections">
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
                <LoaderIcon class="size-3.5 animate-spin text-primary" aria-label="Running" />
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
            <TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0" /> Targeting {app.host}
          </button>
        {/if}
        {#if app.snapshot && !app.connected}
          <p class="flex items-center gap-1.5 text-warning"><LoaderIcon class="size-3.5 animate-spin" /> Reconnecting</p>
        {/if}
      </div>
    </nav>

    <div class="flex min-w-0 flex-1 flex-col">
      {#if running && app.page !== "overview" && app.page !== "activity"}
        <div class="flex items-center gap-4 border-b bg-card px-6 py-2">
          <span class="font-semibold">{actionLabels[running.request.action]}</span>
          <span class="min-w-0 flex-1 truncate text-muted-foreground">{runningStep?.label ?? running.step}</span>
          {#if runningStep}<div class="w-56"><BuildProgress compact progress={app.stepProgress(running, runningStep)} /></div>{/if}
          <Button variant="ghost" size="sm" onclick={() => { app.selectOperation(running.id); app.navigate("activity"); }}>Show</Button>
        </div>
      {/if}
      <main id="main" class="scroll-thin min-h-0 flex-1 {app.page === 'activity' ? 'overflow-hidden' : 'overflow-auto'}">
        <div class="mx-auto h-full max-w-5xl px-8 py-7">
          {#if !app.snapshot}
            <div class="grid h-full place-items-center text-muted-foreground">
              <p class="flex items-center gap-2"><LoaderIcon class="size-4 animate-spin" /> Reading this computer's state</p>
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
