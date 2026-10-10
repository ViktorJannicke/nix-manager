<script lang="ts">
  import ExternalLinkIcon from "@lucide/svelte/icons/external-link";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import Segmented from "./Segmented.svelte";
  import { app } from "$lib/app.svelte";
  import { origin, software } from "$lib/software.svelte";
  import type { CatalogEntry, Target } from "../../../../src/software";

  let { entry = $bindable() }: { entry?: CatalogEntry } = $props();

  let scope = $state<Target>("home");
  let file = $state("");
  const attr = $derived(entry ? `pkgs.${entry.a}` : "");
  const files = $derived(software.packageFiles(scope));
  const installed = $derived(entry && software.installed(entry));
  const pending = $derived(attr ? software.pendingPackage(attr) : undefined);
  $effect(() => {
    if (!files.some((item) => item.file === file)) file = files[0]?.file ?? "";
  });
  const category = (path: string) => path.split("/").at(-1)!.replace(/\.nix$/, "");

  async function add() {
    if (!entry || !file) return;
    software.remember(scope, file);
    await software.stage([{ kind: "add-package", scope, file, attr }]);
    app.toast({ tone: "success", title: `${entry.n ?? entry.a} will be installed`, body: "Prepare your pending edits to build it." });
    entry = undefined;
  }
  async function remove() {
    if (!entry || !installed?.attr) return;
    await software.stage([{ kind: "remove-package", scope: installed.scope, file: installed.file, attr: installed.attr }]);
    app.toast({ tone: "info", title: `${entry.n ?? entry.a} will be removed`, body: "Prepare your pending edits to build without it." });
    entry = undefined;
  }
</script>

<Dialog.Root open={Boolean(entry)} onOpenChange={(open) => { if (!open) entry = undefined; }}>
  <Dialog.Content class="sm:max-w-lg">
    {#if entry}
      <Dialog.Header>
        <Dialog.Title>{entry.n ?? entry.a}</Dialog.Title>
        <Dialog.Description>{entry.d ?? "No description."}</Dialog.Description>
      </Dialog.Header>
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
        <dt class="text-muted-foreground">Version</dt><dd>{entry.v ?? "unknown"}</dd>
        <dt class="text-muted-foreground">Attribute</dt><dd class="font-mono break-all">{attr}</dd>
        {#if entry.p}<dt class="text-muted-foreground">Command</dt><dd class="font-mono">{entry.p}</dd>{/if}
        {#if entry.l.length}<dt class="text-muted-foreground">License</dt><dd>{entry.l.join(", ")}{entry.f ? "" : " (not free)"}</dd>{/if}
        {#if entry.h}
          <dt class="text-muted-foreground">Website</dt>
          <dd><a class="inline-flex items-center gap-1 break-all text-primary hover:underline" href={entry.h} target="_blank" rel="noreferrer">{entry.h.replace(/^https?:\/\//, "")}<ExternalLinkIcon class="size-3 shrink-0" /></a></dd>
        {/if}
      </dl>
      {#if entry.b}
        <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2 text-xs"><TriangleAlertIcon class="mt-0.5 size-3.5 shrink-0 text-warning" />Marked as broken in nixpkgs; building it will probably fail.</p>
      {/if}

      {#if pending}
        <p class="rounded-lg bg-accent px-3 py-2 text-xs">
          {pending.kind === "add-package" ? "Waiting to be installed" : "Waiting to be removed"} in your pending edits ({pending.file}).
        </p>
      {:else if installed}
        <div class="rounded-lg bg-muted px-3 py-2 text-xs">
          <p class="font-medium">Installed{installed.scope === "system" ? " for every user" : ""}</p>
          <p class="text-muted-foreground">
            {installed.attr ? `Listed in ${installed.file}` : `Added by ${origin(installed.file)}`}
          </p>
        </div>
      {:else}
        <div class="space-y-3">
          <div class="flex items-center justify-between gap-3">
            <span class="font-medium">Install for</span>
            <Segmented label="Install for" bind:value={scope} options={[{ value: "home" as Target, label: "Just you" }, { value: "system" as Target, label: "Every user" }]} />
          </div>
          {#if files.length}
            <label class="block space-y-1">
              <span class="font-medium">Add it to</span>
              <select class="focus-ring h-9 w-full rounded-md border bg-card px-2" bind:value={file}>
                {#each files as item (item.file)}
                  <option value={item.file}>{category(item.file)}: {item.file}{item.active ? "" : " (not used by this host)"}</option>
                {/each}
              </select>
              <span class="block text-xs text-muted-foreground">The package is added to this file's package list, next to the others.</span>
            </label>
          {:else}
            <p class="text-xs text-muted-foreground">No file of the {scope} configuration has a package list to add to. Add a <code>{scope === "home" ? "home.packages" : "environment.systemPackages"}</code> list to a module first.</p>
          {/if}
        </div>
      {/if}

      <Dialog.Footer>
        <Button variant="ghost" onclick={() => (entry = undefined)}>Close</Button>
        {#if !pending && installed?.attr}
          <Button variant="destructive" disabled={software.saving} onclick={remove}>Remove</Button>
        {:else if !pending && !installed}
          <Button disabled={!file || software.saving} onclick={add}>Add to pending edits</Button>
        {/if}
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
