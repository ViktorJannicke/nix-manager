<script lang="ts">
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import { software } from "$lib/software.svelte";
  import { list, m, stored } from "$lib/i18n";
  import { experiences } from "$lib/curated";
  import type { AppEntry } from "../../../generated/types.js";
  import type { Bundle } from "$lib/bundles";
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
    void software.lookup(bundle.apps).then((found) => {
      if (found && bundle?.id === id) entries = found;
    });
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
    const title = stored(bundle.title);
    await software.stage(plan.changes);
    app.toast({
      tone: "success",
      title: m.bundle_item_pending({ count: count, title }),
      body: unfree.length
        ? m.feature_non_free_software_will_be_allowed()
        : m.feature_prepare_your_pending_edits_to_build(),
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
        <input type="checkbox" class="focus-ring size-4 accent-primary" checked={!left[id]} onchange={(event) => (left[id] = !event.currentTarget.checked)} aria-label={m.bundle_include({ name })} />
      {/if}
    </label>
  </li>
{/snippet}

<Dialog.Root open={Boolean(bundle)} onOpenChange={(open) => { if (!open) bundle = undefined; }}>
  <Dialog.Content class="scroll-thin max-h-[90vh] overflow-x-hidden overflow-y-auto sm:max-w-lg">
    {#if bundle}
      <Dialog.Header>
        <Dialog.Title>{stored(bundle.title)}</Dialog.Title>
        <Dialog.Description>{stored(bundle.summary)} {m.bundle_untick_what_you_do_not_want()}</Dialog.Description>
      </Dialog.Header>

      {#if !ready}
        <p class="flex items-center gap-2 text-muted-foreground" role="status">
          <LoaderIcon class="size-4 animate-spin" aria-hidden="true" />
          {software.catalog?.state === "failed" ? m.bundle_the_package_index_could_not_be({ error: stored(software.catalog.error) }) : m.bundle_reading_the_package_index()}
        </p>
      {:else}
        {#if features.length}
          <section class="min-w-0 space-y-1.5" aria-labelledby="bundle-features">
            <h3 id="bundle-features" class="label">{m.bundle_features()}</h3>
            <ul role="list" class="panel divide-y overflow-hidden">
              {#each features as item (item.id)}
                {#snippet icon()}
                  <span class="grid size-8 shrink-0 place-items-center rounded-lg bg-muted text-muted-foreground"><ExperienceIcon name={item.icon} class="size-4" /></span>
                {/snippet}
                {@render row(item.id, stored(item.title), stored(item.summary), software.enabled(item) ? m.setup_flakes_on() : undefined, icon)}
              {/each}
            </ul>
          </section>
        {/if}
        {#if apps.length}
          <section class="min-w-0 space-y-1.5" aria-labelledby="bundle-apps">
            <h3 id="bundle-apps" class="label">{m.bundle_apps_installed_for_you()}</h3>
            <ul role="list" class="panel divide-y overflow-hidden">
              {#each apps as entry (entry.a)}
                {@const name = entry.app?.name ?? entry.n ?? entry.a}
                {#snippet icon()}<AppIcon app={entry.app} {name} class="size-8" />{/snippet}
                {@render row(
                  entry.a,
                  name,
                  entry.app?.summary ?? entry.d ?? "",
                  software.installed(entry) ? m.onestep_installed() : software.pendingPackage(`pkgs.${entry.a}`) ? m.bundle_pending() : undefined,
                  icon,
                )}
              {/each}
            </ul>
          </section>
        {/if}
        {#if unfree.length}
          <p class="text-xs text-muted-foreground">
            {m.bundle_is_not_free_software_allowing_non({ count: unfree.length, names: list(unfree) })}
          </p>
        {/if}
        {#if plan.skipped.length}
          <p class="rounded-lg bg-warning/10 px-3 py-2 text-xs">
            {m.bundle_needs_a_file_chosen_by_hand({ count: plan.skipped.length, names: list(plan.skipped) })}
          </p>
        {/if}
      {/if}

      <Dialog.Footer>
        <Button variant="ghost" onclick={() => (bundle = undefined)}>{m.common_close()}</Button>
        <Button disabled={!ready || !plan.changes.length || software.saving} onclick={add}>
          {count > 0 ? m.bundle_add_item_to_pending_edits({ count: count }) : m.bundle_everything_is_there()}
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
