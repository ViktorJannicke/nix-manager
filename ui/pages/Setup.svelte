<script lang="ts">
  import { onMount } from "svelte";
  import SnowflakeIcon from "@lucide/svelte/icons/snowflake";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import CheckIcon from "@lucide/svelte/icons/check";
  import XIcon from "@lucide/svelte/icons/x";
  import CircleIcon from "@lucide/svelte/icons/circle";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { Button } from "$lib/components/ui/button";
  import { api } from "$lib/api";
  import { getLocale, switchLocale, t, tn } from "$lib/i18n";
  import T from "$lib/components/app/T.svelte";
  import type { SetupState } from "../../src/setup-server";

  let current = $state<SetupState>();
  let target = $state("");
  let failure = $state("");
  let sending = $state(false);
  const info = $derived(current?.info);
  const started = $derived(Boolean(current?.target));
  const home = $derived(info?.home ?? "");
  const stepState = (status: string) =>
    ({ running: t("running"), done: t("done"), failed: t("failed"), waiting: t("waiting") })[status] ?? status;
  const short = (path: string) => (home && path.startsWith(`${home}/`) ? `~${path.slice(home.length)}` : path);

  async function poll() {
    try {
      current = await api<SetupState>("setup");
      if (current.locale !== getLocale()) return switchLocale(current.locale);
      failure = "";
      if (!target && current.info) target = short(current.info.suggested);
      if (current.next) location.replace(current.next);
    } catch (error) {
      failure = error instanceof Error ? error.message : String(error);
    }
  }
  onMount(() => {
    void poll();
    const timer = setInterval(poll, 1_000);
    return () => clearInterval(timer);
  });

  async function create(event: SubmitEvent) {
    event.preventDefault();
    sending = true;
    try {
      current = await api<SetupState>("setup", { target });
      failure = "";
    } catch (error) {
      failure = error instanceof Error ? error.message : String(error);
    } finally {
      sending = false;
    }
  }
</script>

<main class="scroll-thin h-screen overflow-auto">
  <div class="mx-auto max-w-2xl space-y-8 px-8 py-12">
    {#if current?.problem}
      <header class="space-y-2">
        <p class="flex items-center gap-2 font-bold tracking-tight"><SnowflakeIcon class="size-5 text-primary" aria-hidden="true" /> Nix Manager</p>
        <h1 class="title">{t("Nix Manager could not start")}</h1>
      </header>
      <p class="rounded-lg bg-destructive/10 px-4 py-3" role="alert">{t(current.problem.message)}</p>
      <p class="text-muted-foreground">
        <T text={"It reads the configuration in {flake}. Fix the problem there and open Nix Manager again, or run {command} in a terminal to choose another configuration."}>
          {#snippet children(name)}{#if name === "flake"}<code class="font-mono">{current?.problem?.flake}</code>{:else}<code class="font-mono">nix-manager ui --flake &lt;folder&gt;</code>{/if}{/snippet}
        </T>
      </p>
    {:else}
    <header class="space-y-2">
      <p class="flex items-center gap-2 font-bold tracking-tight"><SnowflakeIcon class="size-5 text-primary" aria-hidden="true" /> Nix Manager</p>
      <h1 class="title">{t("Set up this computer")}</h1>
      <p class="text-muted-foreground">
        {t("Nix Manager keeps this computer's configuration in a folder you own, so it can change it for you and keep every version. This copies your configuration from /etc/nixos into that folder once; nothing on the running system changes.")}
      </p>
    </header>

    {#if !info}
      <p class="flex items-center gap-2 text-muted-foreground" role="status">
        {#if current?.error}{t(current.error)}{:else}<LoaderIcon class="size-4 animate-spin" aria-hidden="true" /> {t("Looking at this computer…")}{/if}
      </p>
    {:else}
      <section class="space-y-2" aria-labelledby="setup-found">
        <h2 id="setup-found" class="heading">{t("What is here")}</h2>
        <dl class="panel grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 px-4 py-3">
          <dt class="text-muted-foreground">{t("Computer")}</dt>
          <dd>{info.hostName}, NixOS {info.release}{info.unstable ? " (unstable)" : ""}</dd>
          <dt class="text-muted-foreground">{t("You")}</dt>
          <dd>{info.user}</dd>
          <dt class="text-muted-foreground">{t("Configuration")}</dt>
          <dd>{info.files.length ? tn("{count} file in /etc/nixos", "{count} files in /etc/nixos", info.files.length) : t("None found")}</dd>
          <dt class="text-muted-foreground">{t("Flakes")}</dt>
          <dd>{info.flakes ? t("On") : t("Off for now; the new configuration turns them on")}</dd>
        </dl>
        {#if info.unreadable.length}
          <p class="flex gap-2 text-[0.9rem]"><TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" /> {tn(
            "Only an administrator can read {files}, so it stays behind. If the configuration needs it, copy it into the new folder afterwards.",
            "Only an administrator can read {files}, so they stay behind. If the configuration needs them, copy them into the new folder afterwards.",
            info.unreadable.length,
            { files: info.unreadable.join(", ") },
          )}</p>
        {/if}
        {#if info.channelImports.length}
          <p class="flex gap-2 text-[0.9rem]"><TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" /> {t("The configuration imports {imports} from channels. A flake cannot see channels, so the check below will fail until those imports become flake inputs.", { imports: info.channelImports.join(", ") })}</p>
        {/if}
      </section>

      {#if info.blocked}
        <p class="flex gap-2 rounded-lg bg-warning/10 px-4 py-3"><TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />{t(info.blocked)}</p>
      {:else}
        <form class="space-y-3" onsubmit={create} aria-labelledby="setup-folder">
          <h2 id="setup-folder" class="heading">{t("Where your configuration lives")}</h2>
          <div class="space-y-1">
            <label class="block space-y-1">
              <span class="font-medium">{t("Folder")}</span>
              <input class="focus-ring h-9 w-full rounded-md border bg-card px-3 font-mono text-[0.9rem]" bind:value={target} disabled={started && !current?.error} autocomplete="off" spellcheck="false" aria-describedby="setup-folder-help" />
            </label>
            <span id="setup-folder-help" class="block text-[0.85rem] text-muted-foreground">{t("A new, empty folder in your home folder. It becomes a Git repository, so every change you make is kept.")}</span>
          </div>
          {#if !started || current?.error}
            <Button type="submit" disabled={sending || !target.trim() || current?.running}>{current?.error ? t("Try again") : t("Create my configuration")}</Button>
          {/if}
        </form>
      {/if}

      {#if started}
        <section class="space-y-2" aria-labelledby="setup-progress">
          <h2 id="setup-progress" class="heading">{t("Setting up")}</h2>
          <ol class="space-y-2" aria-live="polite">
            {#each current!.steps as step (step.id)}
              <li class="flex items-center gap-3 {step.status === 'waiting' ? 'text-muted-foreground' : ''}">
                {#if step.status === "running"}<LoaderIcon class="size-4 shrink-0 animate-spin text-primary" aria-hidden="true" />
                {:else if step.status === "done"}<CheckIcon class="size-4 shrink-0 text-success" aria-hidden="true" />
                {:else if step.status === "failed"}<XIcon class="size-4 shrink-0 text-destructive" aria-hidden="true" />
                {:else}<CircleIcon class="size-4 shrink-0 opacity-40" aria-hidden="true" />{/if}
                <span>{t(step.label)}<span class="sr-only">: {stepState(step.status)}</span></span>
              </li>
            {/each}
          </ol>
          {#if current?.error}
            <p class="rounded-lg bg-destructive/10 px-4 py-3 text-[0.9rem]" role="alert">{t(current.error)} {t("The folder was removed again, so you can try once more.")}</p>
          {/if}
          <p class="flex items-center gap-2" role="status">
            {#if !current?.error && (current?.next || !current?.running)}<LoaderIcon class="size-4 animate-spin" aria-hidden="true" /> {t("Opening Nix Manager…")}{/if}
          </p>
        </section>
      {/if}

      <section class="space-y-2 border-t pt-6" aria-labelledby="setup-how">
        <h2 id="setup-how" class="heading">{t("How changes work")}</h2>
        <p class="text-muted-foreground">
          {t("Everything you change, from apps to settings, is written into those files and waits as a pending edit. Prepare builds the new version in the background while you keep working, and shows what will change. Apply switches the computer to it, and every version you applied stays as a restore point you can go back to.")}
        </p>
      </section>
    {/if}

    {/if}

    {#if failure}<p class="text-destructive" role="alert">{t(failure)}</p>{/if}
  </div>
</main>
