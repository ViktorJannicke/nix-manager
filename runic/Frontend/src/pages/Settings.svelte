<script lang="ts">
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import InfoIcon from "@lucide/svelte/icons/info";
  import { Button } from "$lib/components/ui/button";
  import { app, type Theme } from "$lib/app.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import { m } from "$lib/i18n";

  const theme = $derived((app.dashboard?.theme ?? "system") as Theme);
  const language = $derived(app.dashboard?.language ?? "system");
  const hosts = $derived(app.config?.hosts ?? []);
  const host = $derived(hosts.find((item) => item.name === app.host));
</script>

<div class="max-w-3xl space-y-6">
  <header>
    <h1 class="title">{m.nav_settings()}</h1>
  </header>

  <section class="panel divide-y">
    <div class="flex items-center justify-between gap-4 p-4">
      <div>
        <h2 class="font-medium">{m.settings_appearance()}</h2>
        <p class="text-xs text-muted-foreground">{m.settings_appearance_body()}</p>
      </div>
      <Segmented
        label={m.settings_theme()}
        bind:value={() => theme, (next) => app.setTheme(next)}
        options={[
          { value: "system" as Theme, label: m.scope_system() },
          { value: "light" as Theme, label: m.settings_light() },
          { value: "dark" as Theme, label: m.settings_dark() },
        ]}
      />
    </div>

    <div class="flex items-center justify-between gap-4 p-4">
      <div>
        <h2 id="language-heading" class="font-medium">{m.settings_language()}</h2>
        <p class="text-xs text-muted-foreground">{m.settings_language_body()}</p>
      </div>
      <!-- Each language is named in itself, so it can be found without reading the current one. -->
      <select
        class="focus-ring h-8 rounded-md border bg-card px-2 text-xs"
        aria-labelledby="language-heading"
        value={language}
        disabled={!app.connected}
        onchange={(event) => app.setLanguage(event.currentTarget.value as "system" | "en" | "de")}
      >
        <option value="system">{m.scope_system()}</option>
        <option value="en" lang="en">English</option>
        <option value="de" lang="de">Deutsch</option>
      </select>
    </div>
  </section>

  {#if hosts.length > 1}
    <section class="panel space-y-3 p-4" aria-labelledby="host-heading">
      <div class="flex items-center justify-between gap-4">
        <div>
          <h2 id="host-heading" class="font-medium">{m.settings_target_host()}</h2>
          <p class="text-xs text-muted-foreground">{m.settings_detected_as({ host: app.config?.hostKey ?? "" })}</p>
        </div>
        <div class="flex items-center gap-2">
          <select
            class="focus-ring h-8 rounded-md border bg-card px-2 text-xs"
            aria-labelledby="host-heading"
            value={app.host}
            onchange={(event) => app.setHost(event.currentTarget.value)}
          >
            {#each hosts as item (item.name)}
              <option value={item.name}>{item.name === app.config?.hostKey ? m.settings_this_computer({ host: item.name }) : item.name}</option>
            {/each}
          </select>
          {#if app.foreignHost}<Button variant="ghost" size="sm" onclick={() => app.setHost(undefined)}>{m.settings_reset()}</Button>{/if}
        </div>
      </div>
      {#if app.foreignHost}
        <p class="flex gap-2 rounded-lg bg-warning/12 px-3 py-2 text-xs">
          <TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0 text-warning" />
          {m.settings_foreign_host_warning({ host: app.host })}
        </p>
      {/if}
    </section>
  {/if}

  <section class="panel p-4" aria-labelledby="details-heading">
    <h2 id="details-heading" class="mb-3 font-medium">Nix Manager</h2>
    <dl class="grid grid-cols-[10rem_1fr] gap-x-4 gap-y-1.5 text-xs">
      <dt class="text-muted-foreground">{m.overview_configuration()}</dt>
      <dd class="font-mono break-all">{app.config?.flake}</dd>
      <dt class="text-muted-foreground">{m.settings_home_profile()}</dt>
      <dd class="font-mono break-all">{app.config?.homeProfile}</dd>
      {#if host}
        <dt class="text-muted-foreground">{m.settings_home_configuration()}</dt>
        <dd class="font-mono break-all">{host.homeConfiguration}</dd>
        <dt class="text-muted-foreground">{m.settings_nixos_configuration()}</dt>
        <dd class="font-mono break-all">{host.nixosConfiguration} ({host.system})</dd>
      {/if}
    </dl>
    <p class="mt-4 text-xs text-muted-foreground">{m.settings_privileges()}</p>
  </section>

  <div class="flex gap-2 rounded-lg bg-muted/60 p-3 text-[0.83rem] text-muted-foreground">
    <InfoIcon class="mt-0.5 size-4 shrink-0" />
    <p>{m.placeholder_settings()}</p>
  </div>
</div>
