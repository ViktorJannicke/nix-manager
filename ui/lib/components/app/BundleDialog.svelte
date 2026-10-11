<script lang="ts">
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { api } from "$lib/api";
  import { software } from "$lib/software.svelte";
  import { plural } from "$lib/format";
  import { experiences } from "../../../../src/curated";
  import type { AppEntry } from "../../../../src/apps";
  import type { Bundle } from "../../../../src/bundles";
  import type { Snippet } from "svelte";
  import AppIcon from "./AppIcon.svelte";
  import ExperienceIcon from "./ExperienceIcon.svelte";

  let { bundle = $bindable() }: { bundle?: Bundle } = $props();

  let entries = $state.raw<Record<string, AppEntry>>({});
  /** Items left out by hand, by experience id or attribute. */
  let left = $state<Record<string, boolean>>({});
  let opened = "";

  const features = $derived(
    (bundle?.features ?? []).flatMap((id) => experiences.find((item) => item.id === id && software.available(item)) ?? []),
  );
  const apps = $derived((bundle?.apps ?? []).flatMap((attr) => entries[attr] ?? []));
  const ready = $derived(software.catalog?.state === "ready" && bundle !== undefined && Object.keys(entries).length > 0);

  $effect(() => {
    if (!bundle) {
      opened = "";
      return;
    }
    if (software.catalog?.state === "missing") void software.buildCatalog();
    if (software.catalog?.state !== "ready" || opened === bundle.id) return;
    opened = bundle.id;
    left = {};
    entries = {};
    const id = bundle.id;
    api<Record<string, AppEntry>>("software/lookup", { attrs: bundle.apps })
      .then((found) => bundle?.id === id && (entries = found))
      .catch((error) => app.fail(error, "Could not read the apps"));
  });

  const plan = $derived(
    software.bundleChanges(
      features.filter((item) => !left[item.id]),
      apps.filter((entry) => !left[entry.a]),
    ),
  );
  const unfree = $derived(apps.filter((entry) => !left[entry.a] && !entry.f && !software.installed(entry)).map((entry) => entry.app?.name ?? entry.n ?? entry.a));
  const count = $derived(
    features.filter((item) => !left[item.id] && !software.enabled(item)).length +
      apps.filter((entry) => !left[entry.a] && !software.installed(entry) && !software.pendingPackage(`pkgs.${entry.a}`)).length -
      plan.skipped.length,
  );

  async function add() {
    if (!bundle || !plan.changes.length) return;
    const title = bundle.title;
    await software.stage(plan.changes);
    app.toast({
      tone: "success",
      title: `${title}: ${plural(count, "item")} pending`,
      body: `${unfree.length ? "Non-free software will be allowed too. " : ""}Prepare your pending edits to build them.`,
    });
    bundle = undefined;
  }
</script>

{#snippet row(id: string, name: string, summary: string, done: string | undefined, icon: Snippet)}
  <li>
    <label class="flex items-center gap-3 px-3 py-2.5 {done ? '' : 'cursor-pointer hover:bg-muted/40'}">
      {@render icon()}
      <span class="min-w-0 flex-1">
        <span class="block font-medium">{name}</span>
        <span class="block truncate text-xs text-muted-foreground">{summary}</span>
      </span>
      {#if done}
        <span class="text-xs font-medium text-primary">{done}</span>
      {:else}
        <input type="checkbox" class="focus-ring size-4 accent-primary" checked={!left[id]} onchange={(event) => (left[id] = !event.currentTarget.checked)} aria-label="Include {name}" />
      {/if}
    </label>
  </li>
{/snippet}

<Dialog.Root open={Boolean(bundle)} onOpenChange={(open) => { if (!open) bundle = undefined; }}>
  <Dialog.Content class="scroll-thin max-h-[90vh] overflow-x-hidden overflow-y-auto sm:max-w-lg">
    {#if bundle}
      <Dialog.Header>
        <Dialog.Title>{bundle.title}</Dialog.Title>
        <Dialog.Description>{bundle.summary} Untick what you do not want; everything else becomes one pending edit.</Dialog.Description>
      </Dialog.Header>

      {#if !ready}
        <p class="flex items-center gap-2 text-muted-foreground">
          <LoaderIcon class="size-4 animate-spin" />
          {software.catalog?.state === "failed" ? `The package index could not be built. ${software.catalog.error}` : "Reading the package index…"}
        </p>
      {:else}
        {#if features.length}
          <section class="min-w-0 space-y-1.5" aria-labelledby="bundle-features">
            <h3 id="bundle-features" class="label">Features</h3>
            <ul class="panel divide-y overflow-hidden">
              {#each features as item (item.id)}
                {#snippet icon()}
                  <span class="grid size-8 shrink-0 place-items-center rounded-lg bg-muted text-muted-foreground"><ExperienceIcon name={item.icon} class="size-4" /></span>
                {/snippet}
                {@render row(item.id, item.title, item.summary, software.enabled(item) ? "On" : undefined, icon)}
              {/each}
            </ul>
          </section>
        {/if}
        {#if apps.length}
          <section class="min-w-0 space-y-1.5" aria-labelledby="bundle-apps">
            <h3 id="bundle-apps" class="label">Apps, installed for you</h3>
            <ul class="panel divide-y overflow-hidden">
              {#each apps as entry (entry.a)}
                {@const name = entry.app?.name ?? entry.n ?? entry.a}
                {#snippet icon()}<AppIcon app={entry.app} {name} class="size-8" />{/snippet}
                {@render row(
                  entry.a,
                  name,
                  entry.app?.summary ?? entry.d ?? "",
                  software.installed(entry) ? "Installed" : software.pendingPackage(`pkgs.${entry.a}`) ? "Pending" : undefined,
                  icon,
                )}
              {/each}
            </ul>
          </section>
        {/if}
        {#if unfree.length}
          <p class="text-xs text-muted-foreground">{unfree.join(", ")} {unfree.length === 1 ? "is" : "are"} not free software; allowing non-free software is part of the edit.</p>
        {/if}
        {#if plan.skipped.length}
          <p class="rounded-lg bg-warning/10 px-3 py-2 text-xs">
            {plan.skipped.join(", ")} need{plan.skipped.length === 1 ? "s" : ""} a file chosen by hand. Add {plan.skipped.length === 1 ? "it" : "them"} from Discover on {plan.skipped.length === 1 ? "its" : "their"} own.
          </p>
        {/if}
      {/if}

      <Dialog.Footer>
        <Button variant="ghost" onclick={() => (bundle = undefined)}>Close</Button>
        <Button disabled={!ready || !plan.changes.length || software.saving} onclick={add}>
          {count > 0 ? `Add ${plural(count, "item")} to pending edits` : "Everything is there"}
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
