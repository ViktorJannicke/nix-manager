<script lang="ts">
  import ExternalLinkIcon from "@lucide/svelte/icons/external-link";
  import LockOpenIcon from "@lucide/svelte/icons/lock-open";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import Segmented from "./Segmented.svelte";
  import { app } from "$lib/app.svelte";
  import { m, stored } from "$lib/i18n";
  import { origin } from "$lib/changes";
  import { NEW_FILE, software, type Change, type Target } from "$lib/software.svelte";
  import NewModuleFields from "./NewModuleFields.svelte";
  import type { AppEntry } from "../../../generated/types.js";
  import { appCategoryLabel } from "$lib/apps";
  import AppIcon from "./AppIcon.svelte";

  let { entry = $bindable() }: { entry?: AppEntry } = $props();
  const uid = $props.id();

  let scope = $state<Target>("home");
  let file = $state("");
  let newFile = $state("");
  let importer = $state("");
  const attr = $derived(entry ? `pkgs.${entry.a}` : "");
  const files = $derived(software.packageFiles(scope));
  const installed = $derived(entry && software.installed(entry));
  const pending = $derived(attr ? software.pendingPackage(attr) : undefined);
  // Each package starts with the best list, also when the dialog opened before the inventory arrived.
  $effect(() => {
    if (entry) file = files[0]?.file ?? NEW_FILE;
  });
  $effect(() => {
    if (entry) newFile = software.suggestFile(scope, entry.a.split(".").at(-1)!, files.find((item) => item.active)?.file);
  });
  // A non-free package is refused unless the configuration allows it, so the
  // permission is added in the same pending edit.
  const refused = $derived(Boolean(entry && !installed && !pending && software.refusesUnfree(scope, entry)));
  const allowing = $derived(refused ? software.allowUnfree(scope) : undefined);
  const creating = $derived(file === NEW_FILE);
  const ready = $derived(creating ? !software.newFileProblem(scope, newFile) && Boolean(importer) : Boolean(file));
  const category = (path: string) => path.split("/").at(-1)!.replace(/\.nix$/, "");

  /** The edits that install the package. */
  const adding = $derived.by((): Change[] => {
    const allow = allowing ?? [];
    return creating
      ? [...allow, { kind: "create-module", scope, file: newFile, importer }, { kind: "add-package", scope, file: newFile, attr }]
      : [...allow, { kind: "add-package", scope, file, attr }];
  });
  const removing = $derived<Change[]>(
    installed?.attr ? [{ kind: "remove-package", scope: installed.scope, file: installed.file, attr: installed.attr }] : [],
  );
  const name = $derived(entry ? (entry.n ?? entry.a) : "");

  async function add() {
    if (!entry || !ready || (refused && !allowing)) return;
    const allow = allowing ?? [];
    if (!creating) software.remember(scope, file);
    await software.stage(adding);
    app.toast({
      tone: "success",
      title: m.package_will_be_installed({ name: entry.n ?? entry.a }),
      body: allow.length
        ? m.package_non_free_software_will_be_allowed()
        : m.package_prepare_your_pending_edits_to_build(),
    });
    entry = undefined;
  }
  function allowFirst() {
    entry = undefined;
    app.applyFix({ kind: "allow-unfree", packages: [], scope }, scope);
  }
  function installNow() {
    if (!entry || !ready || (refused && !allowing)) return;
    if (!creating) software.remember(scope, file);
    void software.installNow(adding, m.package_installing({ name }), m.package_is_installed({ name }));
    entry = undefined;
  }
  function removeNow() {
    if (!entry || !removing.length) return;
    void software.installNow(removing, m.package_removing({ name }), m.package_is_removed({ name }));
    entry = undefined;
  }
  async function remove() {
    if (!entry || !removing.length) return;
    await software.stage(removing);
    app.toast({ tone: "info", title: m.package_will_be_removed({ name: entry.n ?? entry.a }), body: m.package_prepare_your_pending_edits_to_build_2() });
    entry = undefined;
  }
</script>

<Dialog.Root open={Boolean(entry)} onOpenChange={(open) => { if (!open) entry = undefined; }}>
  <Dialog.Content class="sm:max-w-lg">
    {#if entry}
      <Dialog.Header>
        <div class="flex items-center gap-3">
          {#if entry.app}<AppIcon app={entry.app} name={entry.app.name} class="size-12" />{/if}
          <div class="min-w-0">
            <Dialog.Title>{entry.app?.name ?? entry.n ?? entry.a}</Dialog.Title>
            <Dialog.Description>{entry.app?.summary ?? entry.d ?? m.package_no_description()}</Dialog.Description>
          </div>
        </div>
      </Dialog.Header>
      {#if entry.app && entry.d && entry.d !== entry.app.summary}<p class="text-muted-foreground">{entry.d.replace(/\.?$/, ".")}</p>{/if}
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
        <dt class="text-muted-foreground">{m.packages_version()}</dt><dd>{entry.v ?? m.format_unknown()}</dd>
        {#if entry.app?.categories.length}<dt class="text-muted-foreground">{m.package_category()}</dt><dd>{entry.app.categories.map(appCategoryLabel).join(", ")}</dd>{/if}
        <dt class="text-muted-foreground">{m.package_attribute()}</dt><dd class="font-mono break-all">{attr}</dd>
        {#if entry.p}<dt class="text-muted-foreground">{m.package_command()}</dt><dd class="font-mono">{entry.p}</dd>{/if}
        {#if entry.l.length}<dt class="text-muted-foreground">{m.package_license()}</dt><dd>{entry.f ? entry.l.join(", ") : m.package_not_free({ licenses: entry.l.join(", ") })}</dd>{/if}
        {#if entry.h}
          <dt class="text-muted-foreground">{m.package_website()}</dt>
          <dd><a class="inline-flex items-center gap-1 break-all text-primary hover:underline" href={entry.h} target="_blank" rel="noreferrer">{entry.h.replace(/^https?:\/\//, "")}<ExternalLinkIcon class="size-3 shrink-0" /></a></dd>
        {/if}
      </dl>
      {#if entry.b}
        <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2 text-xs"><TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0 text-warning" />{m.package_marked_as_broken_in_nixpkgs_building()}</p>
      {/if}

      {#if pending}
        <p class="rounded-lg bg-accent px-3 py-2 text-xs">
          {pending.kind === "add-package"
            ? m.package_waiting_to_be_installed_in_your({ file: pending.file })
            : m.package_waiting_to_be_removed_in_your({ file: pending.file })}
        </p>
      {:else if installed}
        <div class="rounded-lg bg-muted px-3 py-2 text-xs">
          <p class="font-medium">{installed.scope === "system" ? m.package_installed_for_every_user() : m.onestep_installed()}</p>
          <p class="text-muted-foreground">
            {installed.attr ? m.package_listed_in({ file: installed.file }) : m.package_added_by({ origin: origin(installed.file) })}
          </p>
        </div>
      {:else}
        <div class="space-y-3">
          <div class="flex items-center justify-between gap-3">
            <span class="font-medium">{m.package_install_for()}</span>
            <Segmented label={m.package_install_for()} bind:value={scope} options={[{ value: "home" as Target, label: m.package_just_you() }, { value: "system" as Target, label: m.package_every_user() }]} />
          </div>
          <div class="space-y-1">
            <label class="block space-y-1">
              <span class="font-medium">{m.package_add_it_to()}</span>
              <select class="focus-ring h-9 w-full rounded-md border bg-card px-2" bind:value={file} aria-describedby={creating ? undefined : `${uid}-file-help`}>
                {#each files as item (item.file)}
                  <option value={item.file}>{item.active ? `${category(item.file)}: ${item.file}` : m.package_not_used_by_this_host({ category: category(item.file), file: item.file })}</option>
                {/each}
                <option value={NEW_FILE}>{m.package_new_file()}</option>
              </select>
            </label>
            {#if !creating}
              <span id="{uid}-file-help" class="block text-xs text-muted-foreground">{m.package_the_package_is_added_to_this()}</span>
            {/if}
          </div>
          {#if creating}
            <NewModuleFields {scope} bind:file={newFile} bind:importer />
          {/if}
          {#if refused}
            <div class="flex gap-2 rounded-lg bg-muted px-3 py-2 text-xs">
              <LockOpenIcon class="mt-0.5 size-3.5 shrink-0 text-muted-foreground" />
              <div class="space-y-1">
                <p class="font-medium">{m.package_is_not_free_software({ name: entry.n ?? entry.a })}</p>
                {#if allowing}
                  <p class="text-muted-foreground">
                    {scope === "home"
                      ? m.package_your_user_only_installs_software_with({ file: allowing.at(-1)!.file })
                      : m.package_this_computer_only_installs_software_with({ file: allowing.at(-1)!.file })}
                  </p>
                {:else}
                  <p class="text-muted-foreground">
                    {scope === "home"
                      ? m.package_your_user_only_installs_software_with_2()
                      : m.package_this_computer_only_installs_software_with_2()}
                  </p>
                  <Button size="sm" variant="outline" class="mt-1" onclick={allowFirst}>{m.package_allow_non_free_software()}</Button>
                {/if}
              </div>
            </div>
          {/if}
        </div>
      {/if}

      <Dialog.Footer>
        <Button variant="ghost" onclick={() => (entry = undefined)}>{m.common_close()}</Button>
        {#if !pending && installed?.attr}
          {#if software.canInstallNow(removing)}
            <Button variant="outline" disabled={software.saving} onclick={remove}>{m.package_add_to_pending_edits()}</Button>
            <Button variant="destructive" disabled={software.saving} onclick={removeNow}>{m.package_remove_now()}</Button>
          {:else}
            <Button variant="destructive" disabled={software.saving} onclick={remove}>{m.package_remove()}</Button>
          {/if}
        {:else if !pending && !installed}
          {#if software.canInstallNow(adding)}
            <Button variant="outline" disabled={!ready || (refused && !allowing) || software.saving} onclick={add}>{m.package_add_to_pending_edits()}</Button>
            <Button disabled={!ready || (refused && !allowing) || software.saving} onclick={installNow}>{m.package_install_now()}</Button>
          {:else}
            <Button disabled={!ready || (refused && !allowing) || software.saving} onclick={add}>{m.package_add_to_pending_edits()}</Button>
          {/if}
        {/if}
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
