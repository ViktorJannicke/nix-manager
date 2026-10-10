<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import { app } from "$lib/app.svelte";
  import { local, NEW_FILE, showValue, software } from "$lib/software.svelte";
  import NewModuleFields from "./NewModuleFields.svelte";
  import type { Change } from "../../../../src/draft";
  import type { Experience } from "../../../../src/curated";

  let { experience = $bindable() }: { experience?: Experience } = $props();

  type Field =
    | { kind: "boolean" }
    | { kind: "text"; nullable: boolean }
    | { kind: "number"; min?: number; max?: number }
    | { kind: "choice"; choices: string[] }
    | { kind: "lines" }
    | { kind: "other" };

  /** Form control for an option type as `optionAttrSetToDocList` describes it. */
  function field(type: string | null | undefined): Field {
    if (!type) return { kind: "other" };
    if (type === "boolean" || type === "null or boolean") return { kind: "boolean" };
    if (type.startsWith("one of ")) return { kind: "choice", choices: [...type.matchAll(/"([^"]*)"/g)].map((match) => match[1]) };
    if (type === "list of string") return { kind: "lines" };
    if (/integer/.test(type)) {
      const range = type.match(/between (-?\d+) and (-?\d+)/);
      return { kind: "number", min: range ? Number(range[1]) : /unsigned|positive/.test(type) ? 0 : undefined, max: range ? Number(range[2]) : undefined };
    }
    if (/^(null or )?(non-empty )?string/.test(type) || type.startsWith("strings concatenated")) return { kind: "text", nullable: type.startsWith("null or") };
    return { kind: "other" };
  }

  /** The default as data, when its Nix text is a plain literal. */
  function parseDefault(text: string | null | undefined): unknown {
    if (!text) return undefined;
    if (text === "true" || text === "false") return text === "true";
    if (text === "null") return null;
    if (/^-?\d+$/.test(text)) return Number(text);
    if (/^"([^"\\]|\\.)*"$/.test(text)) return JSON.parse(text);
    if (/^\[\s*\]$/.test(text)) return [];
    return undefined;
  }

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
    target = software.definedIn(experience.scope, options[0]) ?? modules[0]?.file ?? NEW_FILE;
    newFile = software.suggestFile(experience.scope, experience.id, modules.find((module) => module.active)?.file);
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
  // New settings go to the chosen file; settings that are already defined are edited where they are.
  const needsTarget = $derived(changed.some((option) => !software.definedIn(scope, option)));
  const creating = $derived(needsTarget && target === NEW_FILE);
  const ready = $derived(!needsTarget || (creating ? !software.newFileProblem(scope, newFile) && Boolean(importer) : Boolean(target)));

  async function save() {
    if (!experience) return;
    const label = experience.title;
    const file = creating ? newFile : target;
    const where = (option: string) => software.definedIn(scope, option) ?? file;
    const edits: Change[] = creating ? [{ kind: "create-module", scope, file, importer, label }] : [];
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
      // A new file keeps everything about the feature together.
      const packages = creating ? file : software.packageFiles(scope)[0]?.file;
      for (const attr of experience.packages ?? [])
        if (packages) edits.push({ kind: "add-package", scope, file: packages, attr: `pkgs.${attr}`, label });
    }
    if (needsTarget && !creating) software.remember(scope, target);
    await software.stage(edits);
    app.toast({ tone: "success", title: `${experience.title}: ${edits.length === 1 ? "edit" : `${edits.length} edits`} pending`, body: "Prepare your pending edits to build them." });
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
          {@const kind = field(doc?.t)}
          {@const files = (software.inventory?.[scope].options[setting.option]?.files ?? []).filter(local)}
          {@const pending = software.value(scope, setting.option).pending}
          <div class="flex flex-wrap items-center gap-x-4 gap-y-2 py-3 {on ? '' : 'opacity-60'}">
            <div class="min-w-48 flex-1">
              <p class="font-medium">{setting.label}{#if pending}<span class="ml-2 text-xs font-normal text-prepared-foreground">pending</span>{/if}</p>
              {#if setting.help}<p class="text-xs text-muted-foreground">{setting.help}</p>{/if}
              <p class="font-mono text-[0.7rem] break-all text-muted-foreground">{setting.option}{files.length ? `, set in ${files.join(", ")}` : ""}</p>
            </div>
            {#if !doc}
              <span class="text-xs text-muted-foreground">Reading…</span>
            {:else if kind.kind === "boolean"}
              <Switch checked={Boolean(shown(setting.option))} onCheckedChange={(checked) => (values[setting.option] = checked)} aria-label={setting.label} />
            {:else if kind.kind === "choice"}
              <select class="focus-ring h-8 rounded-md border bg-card px-2 text-xs" aria-label={setting.label} value={String(shown(setting.option) ?? "")} onchange={(event) => (values[setting.option] = event.currentTarget.value)}>
                {#each kind.choices as choice (choice)}<option value={choice}>{choice}</option>{/each}
              </select>
            {:else if kind.kind === "number"}
              <input type="number" class="focus-ring h-8 w-28 rounded-md border bg-card px-2 text-xs" aria-label={setting.label} min={kind.min} max={kind.max} value={shown(setting.option) ?? ""}
                onchange={(event) => { const number = event.currentTarget.valueAsNumber; if (Number.isInteger(number)) values[setting.option] = number; }} />
            {:else if kind.kind === "text"}
              <input class="focus-ring h-8 w-56 rounded-md border bg-card px-2 text-xs" aria-label={setting.label} value={String(shown(setting.option) ?? "")} placeholder={kind.nullable ? "Not set" : ""}
                onchange={(event) => { const text = event.currentTarget.value; values[setting.option] = kind.nullable && !text ? null : text; }} />
            {:else if kind.kind === "lines"}
              <textarea class="focus-ring h-20 w-56 rounded-md border bg-card px-2 py-1 font-mono text-xs" aria-label={setting.label} placeholder="One per line"
                value={((shown(setting.option) as string[] | undefined) ?? []).join("\n")}
                onchange={(event) => (values[setting.option] = event.currentTarget.value.split("\n").map((line) => line.trim()).filter(Boolean))}></textarea>
            {:else}
              <span class="max-w-56 truncate text-xs text-muted-foreground" title={doc.t ?? ""}>{showValue(shown(setting.option))}</span>
            {/if}
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
      {#if !on && initial.on}
        <p class="text-xs text-muted-foreground">Turning it off writes <code>false</code> into {software.definedIn(scope, Object.keys(experience.enable)[0]) ?? (creating ? newFile : target)}.</p>
      {/if}

      <Dialog.Footer>
        <Button variant="ghost" onclick={() => (experience = undefined)}>Cancel</Button>
        <Button disabled={!changed.length || !ready || software.saving} onclick={save}>
          {changed.length ? "Add to pending edits" : "No changes"}
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
