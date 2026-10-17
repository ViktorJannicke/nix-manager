<script lang="ts">
  import SearchIcon from "@lucide/svelte/icons/search";
  import type { PackageChange } from "../../../generated/types.js";
  import { m } from "$lib/i18n";
  import { signedBytes, summarize, versions } from "$lib/format";
  import Segmented from "./Segmented.svelte";

  let { changes, label }: { changes: readonly PackageChange[]; label: string } = $props();
  type Kind = "all" | "added" | "removed" | "changed" | "rebuilt";
  let kind = $state<Kind>("all");
  let query = $state("");
  let limit = $state(150);

  const counts = $derived(summarize(changes));
  const visible = $derived(
    changes.filter(
      (change) =>
        (kind === "all" ? change.kind !== "rebuilt" : change.kind === kind) &&
        change.name.toLowerCase().includes(query.trim().toLowerCase()),
    ),
  );
  const kindStyle: Record<string, string> = {
    added: "text-success",
    removed: "text-destructive",
    changed: "text-primary",
    rebuilt: "text-muted-foreground",
  };
  const kindLabel = (kind: string) =>
    kind === "added" ? m.packages_new() : kind === "removed" ? m.packages_removed() : kind === "changed" ? m.packages_updated() : m.packages_rebuilt();
</script>

<div class="space-y-3">
  <div class="flex flex-wrap items-center gap-2">
    <Segmented
      label={m.packages_change_type()}
      bind:value={kind}
      options={[
        { value: "all", label: m.packages_all(), count: changes.length - counts.rebuilt },
        { value: "changed", label: m.packages_updated(), count: counts.changed },
        { value: "added", label: m.packages_new(), count: counts.added },
        { value: "removed", label: m.packages_removed(), count: counts.removed },
        { value: "rebuilt", label: m.packages_rebuilt(), count: counts.rebuilt },
      ]}
    />
    <label class="ml-auto flex h-8 w-56 items-center gap-2 rounded-md border border-field bg-card px-2 text-muted-foreground focus-within:ring-2 focus-within:ring-ring/40">
      <SearchIcon class="size-3.5" />
      <input bind:value={query} aria-label={m.packages_search()} placeholder={m.packages_search()} class="w-full bg-transparent text-xs text-foreground outline-none placeholder:text-muted-foreground" />
    </label>
  </div>
  <!-- One announcement when the filter or the search changes what is listed. -->
  <p class="sr-only" role="status">{m.packages_shown({ count: visible.length })}</p>
  {#if visible.length}
    <div class="overflow-hidden rounded-lg border">
      <table class="w-full text-left text-xs" aria-label={label}>
        <thead class="bg-muted/50 text-muted-foreground">
          <tr>
            <th class="px-3 py-2 font-medium">{m.packages_package()}</th>
            <th class="w-20 px-3 py-2 font-medium">{m.packages_change()}</th>
            <th class="px-3 py-2 font-medium">{m.packages_version()}</th>
            <th class="w-24 px-3 py-2 text-right font-medium">{m.packages_size()}</th>
          </tr>
        </thead>
        <tbody class="divide-y">
          {#each visible.slice(0, limit) as change (change.name)}
            <tr class="hover:bg-muted/30">
              <td class="px-3 py-1.5 font-medium break-all">{change.name}</td>
              <td class="px-3 py-1.5 {kindStyle[change.kind]}">{kindLabel(change.kind)}</td>
              <td class="px-3 py-1.5 font-mono text-[0.7rem]">
                {#if change.kind === "added"}{versions(change.after)}
                {:else if change.kind === "removed"}<span class="text-muted-foreground line-through">{versions(change.before)}</span>
                {:else if change.kind === "rebuilt"}<span class="text-muted-foreground">{m.packages_same_version()}</span>
                {:else}<span class="text-muted-foreground">{versions(change.before)}</span> → {versions(change.after)}{/if}
              </td>
              <td class="px-3 py-1.5 text-right tabular-nums {change.sizeDelta > 0n ? 'text-warning' : change.sizeDelta < 0n ? 'text-success' : 'text-muted-foreground'}">
                {change.sizeDelta ? signedBytes(change.sizeDelta) : ""}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
    {#if visible.length > limit}
      <button class="focus-ring rounded text-xs font-medium text-primary hover:underline" onclick={() => (limit += 300)}>
        {m.packages_show_more({ count: Math.min(300, visible.length - limit), remaining: visible.length - limit })}
      </button>
    {/if}
  {:else}
    <p class="rounded-lg border border-dashed px-3 py-6 text-center text-muted-foreground">
      {changes.length ? m.packages_no_match() : m.packages_none()}
    </p>
  {/if}
</div>
