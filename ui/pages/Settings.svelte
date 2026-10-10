<script lang="ts">
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { Button } from "$lib/components/ui/button";
  import { app, type Theme } from "$lib/app.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";

  const hosts = $derived(Object.keys(app.config?.hosts ?? {}).sort());
  let theme = $state(app.theme);
  $effect(() => {
    if (theme !== app.theme) app.applyTheme(theme);
  });
</script>

<div class="max-w-3xl space-y-6">
  <header>
    <h1 class="title">Settings</h1>
  </header>

  <section class="panel divide-y">
    <div class="flex items-center justify-between gap-4 p-4">
      <div>
        <p class="font-medium">Appearance</p>
        <p class="text-xs text-muted-foreground">Follow the desktop or choose a theme.</p>
      </div>
      <Segmented
        label="Theme"
        bind:value={theme}
        options={[
          { value: "system" as Theme, label: "System" },
          { value: "light" as Theme, label: "Light" },
          { value: "dark" as Theme, label: "Dark" },
        ]}
      />
    </div>

    <div class="space-y-3 p-4">
      <div class="flex items-center justify-between gap-4">
        <div>
          <p class="font-medium">Target host</p>
          <p class="text-xs text-muted-foreground">Detected as <span class="font-medium text-foreground">{app.config?.hostKey}</span>.</p>
        </div>
        <div class="flex items-center gap-2">
          <select
            class="focus-ring h-8 rounded-md border bg-card px-2 text-xs"
            aria-label="Target host"
            value={app.host}
            onchange={(event) => app.setHost(event.currentTarget.value)}
          >
            {#each hosts as host (host)}
              <option value={host}>{host}{host === app.config?.hostKey ? " (this computer)" : ""}</option>
            {/each}
          </select>
          {#if app.foreignHost}<Button variant="ghost" size="sm" onclick={() => app.setHost(undefined)}>Reset</Button>{/if}
        </div>
      </div>
      {#if app.foreignHost}
        <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2 text-xs">
          <TriangleAlertIcon class="size-4 shrink-0 text-warning" />
          Builds and activations now use the {app.host} configuration. Activating another computer's configuration here can break this system; use this only for testing builds.
        </p>
      {/if}
    </div>

    <dl class="grid gap-x-6 gap-y-3 p-4 sm:grid-cols-[10rem_1fr]">
      <dt class="text-muted-foreground">Configuration</dt>
      <dd class="font-mono text-xs break-all">{app.config?.flake}</dd>
      <dt class="text-muted-foreground">Home profile</dt>
      <dd class="font-mono text-xs break-all">{app.config?.homeProfile}</dd>
      {#if app.config?.hosts[app.host]}
        {@const host = app.config.hosts[app.host]}
        <dt class="text-muted-foreground">Home configuration</dt>
        <dd class="font-mono text-xs">{host.homeConfiguration}</dd>
        <dt class="text-muted-foreground">NixOS configuration</dt>
        <dd class="font-mono text-xs">{host.nixosConfiguration} ({host.system})</dd>
      {/if}
    </dl>
  </section>

  <section class="panel p-4">
    <p class="font-medium">Nix Manager</p>
    <p class="mt-1 text-xs text-muted-foreground">
      Builds are prepared without privileges and shown for review. Only activating a reviewed system build,
      rolling back, and cleaning system generations ask for your password.
    </p>
  </section>
</div>
