<script lang="ts">
  import SearchIcon from "@lucide/svelte/icons/search";
  import type { PackageChange } from "../../../../src/contracts";
  import { t } from "$lib/i18n";
  import { bytes, summarize, versions } from "$lib/format";
  import Segmented from "./Segmented.svelte";

  let { changes }: { changes: PackageChange[] } = $props();
  type Kind = "all" | PackageChange["kind"];
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
  const kindStyle: Record<PackageChange["kind"], string> = {
    added: "text-success",
    removed: "text-destructive",
    changed: "text-primary",
    rebuilt: "text-muted-foreground",
  };
  const kindLabel = { added: t("New"), removed: t("Removed"), changed: t("Updated"), rebuilt: t("Rebuilt") };
</script>

<div class="space-y-3">
  <div class="flex flex-wrap items-center gap-2">
    <Segmented
      label={t("Change type")}
      bind:value={kind}
      options={[
        { value: "all", label: t("All"), count: changes.length - counts.rebuilt },
        { value: "changed", label: t("Updated"), count: counts.changed },
        { value: "added", label: t("New"), count: counts.added },
        { value: "removed", label: t("Removed"), count: counts.removed },
        { value: "rebuilt", label: t("Rebuilt"), count: counts.rebuilt },
      ]}
    />
    <label class="ml-auto flex h-8 w-56 items-center gap-2 rounded-md border border-field bg-card px-2 text-muted-foreground focus-within:ring-2 focus-within:ring-ring/40">
      <SearchIcon class="size-3.5" />
      <input bind:value={query} aria-label={t("Search packages")} placeholder={t("Search packages")} class="w-full bg-transparent text-xs text-foreground outline-none placeholder:text-muted-foreground" />
    </label>
  </div>
  {#if visible.length}
    <div class="overflow-hidden rounded-lg border">
      <table class="w-full text-left text-xs">
        <thead class="bg-muted/50 text-muted-foreground">
          <tr>
            <th class="px-3 py-2 font-medium">{t("Package")}</th>
            <th class="w-20 px-3 py-2 font-medium">{t("Change")}</th>
            <th class="px-3 py-2 font-medium">{t("Version")}</th>
            <th class="w-24 px-3 py-2 text-right font-medium">{t("Size")}</th>
          </tr>
        </thead>
        <tbody class="divide-y">
          {#each visible.slice(0, limit) as change (change.name)}
            <tr class="hover:bg-muted/30">
              <td class="px-3 py-1.5 font-medium break-all">{change.name}</td>
              <td class="px-3 py-1.5 {kindStyle[change.kind]}">{kindLabel[change.kind]}</td>
              <td class="px-3 py-1.5 font-mono text-[0.7rem]">
                {#if change.kind === "added"}{versions(change.after)}
                {:else if change.kind === "removed"}<span class="text-muted-foreground line-through">{versions(change.before)}</span>
                {:else if change.kind === "rebuilt"}<span class="text-muted-foreground">{t("same version")}</span>
                {:else}<span class="text-muted-foreground">{versions(change.before)}</span> → {versions(change.after)}{/if}
              </td>
              <td class="px-3 py-1.5 text-right tabular-nums {change.sizeDelta > 0 ? 'text-warning' : change.sizeDelta < 0 ? 'text-success' : 'text-muted-foreground'}">
                {change.sizeDelta ? bytes(change.sizeDelta, true) : ""}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
    {#if visible.length > limit}
      <button class="focus-ring rounded text-xs font-medium text-primary hover:underline" onclick={() => (limit += 300)}>
        {t("Show {count} more of {remaining} remaining", { count: Math.min(300, visible.length - limit), remaining: visible.length - limit })}
      </button>
    {/if}
  {:else}
    <p class="rounded-lg border border-dashed px-3 py-6 text-center text-muted-foreground">
      {changes.length ? t("No packages match.") : t("No package changes.")}
    </p>
  {/if}
</div>
