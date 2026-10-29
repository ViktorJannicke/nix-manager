<svelte:options runes={true} />

<!--
  The setup assistant in its own Window, before there is a checkout:
  what is on this computer, the folder to create and each step, or only why Nix Manager could not start. The
  backend's SetupViewModel holds the state; its text for people is shown with `text`.
-->
<script lang="ts">
  import { useView } from "@runic-artifex/svelte";
  import { LocalizedInline } from "@runic-artifex/translations-svelte/inline";
  import { useMessageLocale } from "@runic-artifex/translations-svelte";
  import { configureLocaleResolver, getLocale } from "virtual:runic-translations/dashboard/runtime";
  import SnowflakeIcon from "@lucide/svelte/icons/snowflake";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import CheckIcon from "@lucide/svelte/icons/check";
  import XIcon from "@lucide/svelte/icons/x";
  import CircleIcon from "@lucide/svelte/icons/circle";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import { connectSetup } from "./generated/setup.js";
  import { Button } from "$lib/components/ui/button";
  import { inline, m, message, text } from "$lib/i18n";

  const setup = useView(() => ({ connect: connectSetup }));

  // There are no preferences before setup: the desktop's language and colour scheme.
  useMessageLocale(configureLocaleResolver, () => setup.state?.locale ?? "en");
  $effect(() => {
    document.documentElement.lang = getLocale();
  });
  $effect(() => {
    const media = matchMedia("(prefers-color-scheme: dark)");
    const apply = () => document.documentElement.classList.toggle("dark", media.matches);
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  });

  const current = $derived(setup.state?.state);
  const info = $derived(current?.info ?? undefined);
  const started = $derived(Boolean(current?.target));
  const home = $derived(info?.home ?? "");
  const short = (path: string) => (home && path.startsWith(`${home}/`) ? `~${path.slice(home.length)}` : path);

  let target = $state("");
  let sending = $state(false);
  let failure = $state("");
  let progress = $state<HTMLElement>();
  let moveFocus = false;

  // The suggested folder, until the user types another.
  $effect(() => {
    if (!target && setup.state?.target) target = short(setup.state.target);
  });

  // The form gives way to the steps once creation starts; focus follows, so it is not lost with the button.
  $effect(() => {
    if (started && progress && moveFocus) {
      moveFocus = false;
      progress.focus();
    }
  });

  // Served without a window, the page moves on to the dashboard's address by itself.
  $effect(() => {
    if (current?.next) location.replace(current.next);
  });

  const stepState = (status: string) =>
    ({ running: m.setup_step_running(), done: m.setup_step_done(), failed: m.setup_step_failed(), waiting: m.setup_step_waiting() })[status] ?? status;

  async function create(event: SubmitEvent) {
    event.preventDefault();
    const client = setup.client;
    if (!client) return;
    sending = true;
    moveFocus = true;
    try {
      await client.setTarget(target.trim());
      await client.create();
      failure = "";
    } catch (error) {
      moveFocus = false;
      failure = message(error);
    } finally {
      sending = false;
    }
  }
</script>

<svelte:head><title>{current?.problem ? m.setup_could_not_start() : m.setup_title()} – Nix Manager</title></svelte:head>

<main class="scroll-thin h-screen overflow-auto">
  <div class="mx-auto max-w-2xl space-y-8 px-8 py-12">
    {#if current?.problem}
      <header class="space-y-2">
        <p class="flex items-center gap-2 font-bold tracking-tight"><SnowflakeIcon class="size-5 text-primary" aria-hidden="true" /> Nix Manager</p>
        <h1 class="title">{m.setup_could_not_start()}</h1>
      </header>
      <p class="rounded-lg bg-destructive/10 px-4 py-3" role="alert">{text(current.problem.message)}</p>
      <p class="text-muted-foreground">
        <LocalizedInline nodes={inline.render(m.setup_problem_hint({ flake: current.problem.flake, command: "nix-manager ui --flake <folder>" }), { slots: {} })} />
      </p>
    {:else}
      <header class="space-y-2">
        <p class="flex items-center gap-2 font-bold tracking-tight"><SnowflakeIcon class="size-5 text-primary" aria-hidden="true" /> Nix Manager</p>
        <h1 class="title">{m.setup_title()}</h1>
        <p class="text-muted-foreground">{m.setup_intro()}</p>
      </header>

      {#if !info}
        <p class="flex items-center gap-2 text-muted-foreground" role="status">
          {#if current?.error}{text(current.error)}{:else if setup.error && !setup.state}{m.setup_not_connected()}{:else}<LoaderIcon class="size-4 animate-spin" aria-hidden="true" /> {m.setup_looking()}{/if}
        </p>
      {:else}
        <section class="space-y-2" aria-labelledby="setup-found">
          <h2 id="setup-found" class="heading">{m.setup_what_is_here()}</h2>
          <dl class="panel grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 px-4 py-3">
            <dt class="text-muted-foreground">{m.setup_computer()}</dt>
            <dd>{info.hostName}, NixOS {info.release}{info.unstable ? " (unstable)" : ""}</dd>
            <dt class="text-muted-foreground">{m.setup_you()}</dt>
            <dd>{info.user}</dd>
            <dt class="text-muted-foreground">{m.setup_configuration()}</dt>
            <dd>{info.files.length ? m.setup_files({ count: info.files.length }) : m.setup_none_found()}</dd>
            <dt class="text-muted-foreground">{m.setup_flakes()}</dt>
            <dd>{info.flakes ? m.setup_flakes_on() : m.setup_flakes_off()}</dd>
          </dl>
          {#if info.unreadable.length}
            <p class="flex gap-2 text-[0.9rem]"><TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" /> {m.setup_unreadable({ count: info.unreadable.length, files: info.unreadable.join(", ") })}</p>
          {/if}
          {#if info.channelImports.length}
            <p class="flex gap-2 text-[0.9rem]"><TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" /> {m.setup_channel_imports({ imports: info.channelImports.join(", ") })}</p>
          {/if}
        </section>

        {#if info.blocked}
          <p class="flex gap-2 rounded-lg bg-warning/10 px-4 py-3"><TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />{text(info.blocked)}</p>
        {:else}
          <form class="space-y-3" onsubmit={create} aria-labelledby="setup-folder">
            <h2 id="setup-folder" class="heading">{m.setup_where()}</h2>
            <div class="space-y-1">
              <label class="block space-y-1">
                <span class="font-medium">{m.setup_folder()}</span>
                <input class="focus-ring h-9 w-full rounded-md border bg-card px-3 font-mono text-[0.9rem]" bind:value={target} disabled={started && !current?.error} autocomplete="off" spellcheck="false" aria-describedby="setup-folder-help" aria-invalid={setup.state?.requestError ? true : undefined} />
              </label>
              <span id="setup-folder-help" class="block text-[0.85rem] text-muted-foreground">{m.setup_folder_help()}</span>
            </div>
            {#if setup.state?.requestError}<p class="text-destructive" role="alert">{text(setup.state.requestError)}</p>{/if}
            {#if !started || current?.error}
              <Button type="submit" disabled={sending || !target.trim() || current?.running}>{current?.error ? m.setup_try_again() : m.setup_create()}</Button>
            {/if}
          </form>
        {/if}

        {#if started}
          <section class="space-y-2" aria-labelledby="setup-progress">
            <h2 id="setup-progress" class="heading outline-none" tabindex="-1" bind:this={progress}>{m.setup_setting_up()}</h2>
            <ol class="space-y-2" aria-live="polite">
              {#each current!.steps as step (step.id)}
                <li class="flex items-center gap-3 {step.status === 'waiting' ? 'text-muted-foreground' : ''}">
                  {#if step.status === "running"}<LoaderIcon class="size-4 shrink-0 animate-spin text-primary" aria-hidden="true" />
                  {:else if step.status === "done"}<CheckIcon class="size-4 shrink-0 text-success" aria-hidden="true" />
                  {:else if step.status === "failed"}<XIcon class="size-4 shrink-0 text-destructive" aria-hidden="true" />
                  {:else}<CircleIcon class="size-4 shrink-0 opacity-40" aria-hidden="true" />{/if}
                  <span>{text(step.label)}<span class="sr-only">: {stepState(step.status)}</span></span>
                </li>
              {/each}
            </ol>
            {#if current?.error}
              <p class="rounded-lg bg-destructive/10 px-4 py-3 text-[0.9rem]" role="alert">{text(current.error)} {m.setup_removed()}</p>
            {/if}
            <p class="flex items-center gap-2" role="status">
              {#if !current?.error && (current?.next || !current?.running)}<LoaderIcon class="size-4 animate-spin" aria-hidden="true" /> {m.setup_opening()}{/if}
            </p>
          </section>
        {/if}

        <section class="space-y-2 border-t pt-6" aria-labelledby="setup-how">
          <h2 id="setup-how" class="heading">{m.setup_how()}</h2>
          <p class="text-muted-foreground">{m.setup_how_text()}</p>
        </section>
      {/if}
    {/if}

    {#if failure}<p class="text-destructive" role="alert">{failure}</p>{/if}
  </div>
</main>
