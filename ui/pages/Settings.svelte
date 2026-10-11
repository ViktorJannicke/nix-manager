<script lang="ts">
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { Button } from "$lib/components/ui/button";
  import { app, type Detail, type Theme } from "$lib/app.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import T from "$lib/components/app/T.svelte";
  import { Switch } from "$lib/components/ui/switch";
  import { t } from "$lib/i18n";

  const hosts = $derived(Object.keys(app.config?.hosts ?? {}).sort());
</script>

<div class="max-w-3xl space-y-6">
  <header>
    <h1 class="title">{t("Settings")}</h1>
  </header>

  <section class="panel divide-y">
    <div class="flex items-center justify-between gap-4 p-4">
      <div>
        <h2 class="font-medium">{t("Appearance")}</h2>
        <p class="text-xs text-muted-foreground">{t("Follow the desktop or choose a theme.")}</p>
      </div>
      <Segmented
        label={t("Theme")}
        bind:value={() => app.theme, (theme) => app.setTheme(theme)}
        options={[
          { value: "system" as Theme, label: t("System") },
          { value: "light" as Theme, label: t("Light") },
          { value: "dark" as Theme, label: t("Dark") },
        ]}
      />
    </div>

    <div class="flex items-center justify-between gap-4 p-4">
      <div>
        <h2 class="font-medium">{t("Language")}</h2>
        <p class="text-xs text-muted-foreground">{t("Follow the desktop or choose a language.")}</p>
      </div>
      <!-- Each language is named in itself, so it can be found without reading the current one. -->
      <select
        class="focus-ring h-8 rounded-md border bg-card px-2 text-xs"
        aria-label={t("Language")}
        value={app.snapshot?.preferences.language ?? "system"}
        disabled={!app.connected}
        onchange={(event) => app.setLanguage(event.currentTarget.value as "system" | "en" | "de")}
      >
        <option value="system">{t("System")}</option>
        <option value="en" lang="en">English</option>
        <option value="de" lang="de">Deutsch</option>
      </select>
    </div>

    <div class="flex items-center justify-between gap-4 p-4">
      <div>
        <h2 class="font-medium">{t("How much to show")}</h2>
        <p class="text-xs text-muted-foreground">
          {#if app.detail === "simple"}
            {t("One button applies your changes, with a short summary first when they need a look.")}
          {:else}
            {t("Every step: prepare, then review the full comparison, then apply.")}
          {/if}
        </p>
      </div>
      <Segmented
        label={t("How much to show")}
        bind:value={() => app.detail, (detail) => app.setDetail(detail)}
        options={[
          { value: "simple" as Detail, label: t("Simple") },
          { value: "detailed" as Detail, label: t("Detailed") },
        ]}
      />
    </div>

    <label class="flex items-center justify-between gap-4 p-4">
      <span>
        <span class="block font-medium">{t("Prepare updates in the background")}</span>
        <span class="block text-xs text-muted-foreground">
          {t("When the tray finds updates, they are downloaded and built while the computer is plugged in and not busy. A notification says when they are ready. Nothing is applied until you choose.")}
        </span>
      </span>
      <Switch
        aria-label={t("Prepare updates in the background")}
        checked={app.snapshot?.preferences.prepareUpdates ?? false}
        disabled={!app.connected}
        onCheckedChange={(prepareUpdates) => app.savePreferences({ prepareUpdates })}
      />
    </label>

    <label class="flex items-center justify-between gap-4 p-4">
      <span>
        <span class="block font-medium">{t("Clean up automatically")}</span>
        <span class="block text-xs text-muted-foreground">
          {t("Once a week, while the computer is plugged in and not busy, older restore points of your apps are removed and files nothing uses are deleted. The newest five are kept. System restore points are only removed when you choose.")}
        </span>
      </span>
      <Switch
        aria-label={t("Clean up automatically")}
        checked={app.snapshot?.preferences.cleanUp ?? false}
        disabled={!app.connected}
        onCheckedChange={(cleanUp) => app.savePreferences({ cleanUp })}
      />
    </label>

    <label class="flex items-center justify-between gap-4 p-4">
      <span>
        <span class="block font-medium">{t("Keep a history of changes")}</span>
        <span class="block text-xs text-muted-foreground">
          {t("Every change you apply is saved with a short description, so the Restore points page shows what changed and when. It is kept with Git in the configuration folder.")}
        </span>
      </span>
      <Switch
        aria-label={t("Keep a history of changes")}
        checked={app.snapshot?.preferences.history ?? false}
        disabled={!app.connected}
        onCheckedChange={(history) => app.savePreferences({ history })}
      />
    </label>

    <label class="flex items-center justify-between gap-4 p-4">
      <span>
        <span class="block font-medium">{t("Back up after each change")}</span>
        <span class="block text-xs text-muted-foreground">
          {#if app.system?.repository.upstream}
            {t("The history is sent to {upstream} after each change you apply.", { upstream: app.system.repository.upstream })}
          {:else}
            {t("The configuration folder has no remote copy to back up to yet.")}
          {/if}
        </span>
      </span>
      <Switch
        aria-label={t("Back up after each change")}
        checked={(app.snapshot?.preferences.backup ?? false) && !!app.system?.repository.upstream}
        disabled={!app.connected || !app.system?.repository.upstream || !app.snapshot?.preferences.history}
        onCheckedChange={(backup) => app.savePreferences({ backup })}
      />
    </label>

    <label class="flex items-center justify-between gap-4 p-4">
      <span>
        <span class="block font-medium">{t("Show app details from Flathub")}</span>
        <span class="block text-xs text-muted-foreground">
          {t("Names, descriptions, categories and icons of apps come from Flathub's public catalogue, downloaded about once a week. The apps themselves are still installed from your nixpkgs.")}
        </span>
      </span>
      <Switch
        aria-label={t("Show app details from Flathub")}
        checked={app.snapshot?.preferences.appDetails ?? true}
        disabled={!app.connected}
        onCheckedChange={(appDetails) => app.savePreferences({ appDetails })}
      />
    </label>

    <div class="space-y-3 p-4">
      <div class="flex items-center justify-between gap-4">
        <div>
          <h2 class="font-medium">{t("Target host")}</h2>
          <p class="text-xs text-muted-foreground">
            <T text={"Detected as {host}."}>{#snippet children()}<span class="font-medium text-foreground">{app.config?.hostKey}</span>{/snippet}</T>
          </p>
        </div>
        <div class="flex items-center gap-2">
          <select
            class="focus-ring h-8 rounded-md border bg-card px-2 text-xs"
            aria-label={t("Target host")}
            value={app.host}
            onchange={(event) => app.setHost(event.currentTarget.value)}
          >
            {#each hosts as host (host)}
              <option value={host}>{host === app.config?.hostKey ? t("{host} (this computer)", { host }) : host}</option>
            {/each}
          </select>
          {#if app.foreignHost}<Button variant="ghost" size="sm" onclick={() => app.setHost(undefined)}>{t("Reset")}</Button>{/if}
        </div>
      </div>
      {#if app.foreignHost}
        <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2 text-xs">
          <TriangleAlertIcon class="size-4 shrink-0 text-warning" />
          {t("Changes are now built and applied with the {host} configuration. Applying another computer's configuration here can break this system; use this only to test builds.", { host: app.host })}
        </p>
      {/if}
    </div>

    <dl class="grid gap-x-6 gap-y-3 p-4 sm:grid-cols-[10rem_1fr]">
      <dt class="text-muted-foreground">{t("Configuration")}</dt>
      <dd class="font-mono text-xs break-all">{app.config?.flake}</dd>
      <dt class="text-muted-foreground">{t("Home profile")}</dt>
      <dd class="font-mono text-xs break-all">{app.config?.homeProfile}</dd>
      {#if app.config?.hosts[app.host]}
        {@const host = app.config.hosts[app.host]}
        <dt class="text-muted-foreground">{t("Home configuration")}</dt>
        <dd class="font-mono text-xs">{host.homeConfiguration}</dd>
        <dt class="text-muted-foreground">{t("NixOS configuration")}</dt>
        <dd class="font-mono text-xs">{host.nixosConfiguration} ({host.system})</dd>
      {/if}
    </dl>
  </section>

  <section class="panel p-4">
    <h2 class="font-medium">Nix Manager</h2>
    <p class="mt-1 text-xs text-muted-foreground">
      {t("Changes are prepared without special rights and shown for review. Only applying a reviewed system change, going back to an earlier system version and removing system restore points ask for your password.")}
    </p>
  </section>
</div>
