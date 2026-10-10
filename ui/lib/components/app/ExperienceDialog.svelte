<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import LockOpenIcon from "@lucide/svelte/icons/lock-open";
  import { parseDefault } from "$lib/fields";
  import { app } from "$lib/app.svelte";
  import { local, NEW_FILE, software } from "$lib/software.svelte";
  import NewModuleFields from "./NewModuleFields.svelte";
  import OptionField from "./OptionField.svelte";
  import type { Change } from "../../../../src/draft";
  import type { Experience } from "../../../../src/curated";

  /** `start` opens the experience switched on, as the fix for a failure. */
  let { experience = $bindable(), start = false }: { experience?: Experience; start?: boolean } = $props();

  let on = $state(false);
  let values = $state<Record<string, unknown>>({});
  let initial = $state.raw<{ on: boolean; values: Record<string, unknown> }>({ on: false, values: {} });
  let target = $state("");
  let newFile = $state("");
  let importer = $state("");
  let opened = "";

  const scope = $derived(experience?.scope ?? "home");
  const options = $derived(experience ? [...Object.keys(experience.enable), ...experience.settings.map((setting) => setting.option)] : []);
  const modules = $derived(experience ? software.moduleFiles(experience.scope, options[0]) : []);

  $effect(() => {
    if (!experience || opened === experience.id) return;
    opened = experience.id;
    void software.describe(experience.scope, options);
    on = software.enabled(experience);
    values = Object.fromEntries(
      experience.settings.map((setting) => [setting.option, software.value(experience!.scope, setting.option).value]),
    );
    initial = { on, values: $state.snapshot(values) as Record<string, unknown> };
    if (start) on = true;
    target = software.editableIn(experience.scope, options[0]) ?? software.featureFile(experience.scope, options[0], experience.id);
    newFile = software.suggestFile(experience.scope, experience.id, modules.find((module) => module.active !== false)?.file);
  });
  $effect(() => {
    if (!experience) opened = "";
  });

  const shown = (option: string) => {
    const value = values[option];
    return value === undefined ? parseDefault(software.doc(scope, option)?.df) : value;
  };
  const changed = $derived(
    experience
      ? [
          ...(on !== initial.on ? Object.keys(experience.enable) : []),
          ...experience.settings.map((setting) => setting.option).filter((option) => JSON.stringify(values[option]) !== JSON.stringify(initial.values[option])),
        ]
      : [],
  );
  // New settings go to the chosen file; settings that are already defined in an editable module are edited there.
  const needsTarget = $derived(changed.some((option) => !software.editableIn(scope, option)));
  const creating = $derived(needsTarget && target === NEW_FILE);
  const ready = $derived(!needsTarget || (creating ? !software.newFileProblem(scope, newFile) && Boolean(importer) : Boolean(target)));
  // Turning on a feature that installs non-free software also allows it, like adding a non-free app.
  const refused = $derived(Boolean(experience && on && !initial.on && experience.unfree?.some((name) => software.refusesUnfreeName(scope, name))));
  const allowing = $derived(refused ? software.allowUnfree(scope) : undefined);

  async function save() {
    if (!experience) return;
    const label = experience.title;
    const file = creating ? newFile : target;
    const where = (option: string) => software.editableIn(scope, option) ?? file;
    const edits: Change[] = [...(allowing ?? []), ...(creating ? [{ kind: "create-module", scope, file, importer, label } as const] : [])];
    if (on !== initial.on)
      for (const [option, value] of Object.entries(experience.enable))
        edits.push(
          on
            ? { kind: "set-option", scope, file: where(option), option, value, label }
            : typeof value === "boolean"
              ? { kind: "set-option", scope, file: where(option), option, value: false, label }
              : { kind: "unset-option", scope, file: where(option), option, label },
        );
    for (const setting of experience.settings) {
      const value = values[setting.option];
      if (JSON.stringify(value) === JSON.stringify(initial.values[setting.option])) continue;
      edits.push({ kind: "set-option", scope, file: where(setting.option), option: setting.option, value: value as never, label });
    }
    if (on && !initial.on) {
      for (const [option, value] of Object.entries(experience.alsoSet ?? {}))
        edits.push({ kind: "set-option", scope, file: where(option), option, value: value as never, label });
      // A new file keeps everything about the feature together.
      const packages = creating ? file : software.packageFiles(scope)[0]?.file;
      for (const attr of experience.packages ?? [])
        if (packages) edits.push({ kind: "add-package", scope, file: packages, attr: `pkgs.${attr}`, label });
    }
    if (needsTarget && !creating) software.remember(scope, target);
    await software.stage(edits);
    app.toast({ tone: "success", title: `${experience.title}: ${edits.length === 1 ? "edit" : `${edits.length} edits`} pending`, body: `${allowing ? "Non-free software will be allowed too. " : ""}Prepare your pending edits to build them.` });
    experience = undefined;
  }
</script>

<Dialog.Root open={Boolean(experience)} onOpenChange={(open) => { if (!open) experience = undefined; }}>
  <Dialog.Content class="scroll-thin max-h-[90vh] overflow-y-auto sm:max-w-xl">
    {#if experience}
      <Dialog.Header>
        <Dialog.Title>{experience.title}</Dialog.Title>
        <Dialog.Description>{experience.summary}</Dialog.Description>
      </Dialog.Header>

      <label class="flex items-center justify-between gap-4 rounded-lg bg-muted px-3 py-2.5">
        <span>
          <span class="block font-medium">{on ? "Turned on" : "Turned off"}</span>
          <span class="block text-xs text-muted-foreground">
            {scope === "system" ? "System setting for every user of this computer." : "Setting for your user."}
            {#if software.definedIn(scope, Object.keys(experience.enable)[0])}Set in {software.definedIn(scope, Object.keys(experience.enable)[0])}.{/if}
          </span>
        </span>
        <Switch bind:checked={on} aria-label="Turn {experience.title} on" />
      </label>

      <div class="divide-y">
        {#each experience.settings as setting (setting.option)}
          {@const doc = software.doc(scope, setting.option)}
          {@const files = (software.inventory?.[scope].options[setting.option]?.files ?? []).filter(local)}
          {@const pending = software.value(scope, setting.option).pending}
          <div class="flex flex-wrap items-center gap-x-4 gap-y-2 py-3 {on ? '' : 'opacity-60'}">
            <div class="min-w-48 flex-1">
              <p class="font-medium">{setting.label}{#if pending}<span class="ml-2 text-xs font-normal text-prepared-foreground">pending</span>{/if}</p>
              {#if setting.help}<p class="text-xs text-muted-foreground">{setting.help}</p>{/if}
              <p class="font-mono text-[0.7rem] break-all text-muted-foreground">{setting.option}{files.length ? `, set in ${files.join(", ")}` : ""}</p>
            </div>
            <OptionField label={setting.label} {doc} value={shown(setting.option)} onchange={(value) => (values[setting.option] = value)} />
          </div>
        {/each}
      </div>

      {#if needsTarget}
        <label class="block space-y-1">
          <span class="font-medium">Write new settings to</span>
          <select class="focus-ring h-9 w-full rounded-md border bg-card px-2" bind:value={target}>
            <optgroup label="Used by {app.host}">
              {#each modules.filter((module) => module.active !== false) as module (module.file)}<option value={module.file}>{module.file}</option>{/each}
            </optgroup>
            {#if modules.some((module) => module.active === false)}
              <optgroup label="Not used by {app.host}">
                {#each modules.filter((module) => module.active === false) as module (module.file)}<option value={module.file}>{module.file}</option>{/each}
              </optgroup>
            {/if}
            <option value={NEW_FILE}>New file…</option>
          </select>
          {#if creating}
            <NewModuleFields {scope} bind:file={newFile} bind:importer />
          {:else if modules.find((module) => module.file === target)?.active === false}
            <span class="block text-xs text-warning">{app.host} does not import this file, so settings written here have no effect on it. Prepare will refuse them.</span>
          {:else}
            <span class="block text-xs text-muted-foreground">Prepare checks that every setting takes effect before anything is built.</span>
          {/if}
        </label>
      {/if}
      {#if refused}
        <div class="flex gap-2 rounded-lg bg-muted px-3 py-2 text-xs">
          <LockOpenIcon class="mt-0.5 size-3.5 shrink-0 text-muted-foreground" />
          <div class="space-y-1">
            <p class="font-medium">{experience.title} is not free software</p>
            {#if allowing}
              <p class="text-muted-foreground">This computer only installs software with a free license. Turning it on also allows non-free software, in {allowing.at(-1)!.file}.</p>
            {:else}
              <p class="text-muted-foreground">This computer only installs software with a free license. Allow non-free software first, then turn it on.</p>
            {/if}
          </div>
        </div>
      {/if}
      {#if !on && initial.on}
        <p class="text-xs text-muted-foreground">Turning it off writes <code>false</code> into {software.editableIn(scope, Object.keys(experience.enable)[0]) ?? (creating ? newFile : target)}.</p>
      {/if}

      <Dialog.Footer>
        <Button variant="ghost" onclick={() => (experience = undefined)}>Cancel</Button>
        <Button disabled={!changed.length || !ready || (refused && !allowing) || software.saving} onclick={save}>
          {changed.length ? "Add to pending edits" : "No changes"}
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
