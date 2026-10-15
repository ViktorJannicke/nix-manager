<script lang="ts">
  import PlusIcon from "@lucide/svelte/icons/plus";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import type { Generation } from "../../../generated/types.js";
  import { app } from "$lib/app.svelte";
  import { m } from "$lib/i18n";
  import { day, relative, summarize } from "$lib/format";

  let { profile, title }: { profile: "home" | "system"; title: string } = $props();

  const all = $derived(app.system?.generations[profile] ?? []);
  const generations = $derived([...all].sort((a, b) => a.number - b.number).slice(-5));
  const review = $derived(app.reviews.find((r) => r.host === app.host && r[profile]));
  const building = $derived(
    app.active?.request.action === "review-build" && (app.active.request.scope === "all" || app.active.request.scope === profile),
  );
  const output = $derived(review?.[profile] ?? undefined);
  const changes = $derived(output ? summarize(output.changes) : undefined);
  const describe = (generation: Generation) =>
    generation.current ? m.rail_in_use_since({ when: relative(generation.createdAt, app.now) }) : day(generation.createdAt);
</script>

<div class="grid grid-cols-[8.5rem_1fr] items-center gap-4">
  <!-- A long count, such as German "Wiederherstellungspunkte", wraps at its soft hyphen. -->
  <div class="min-w-0">
    <p class="font-semibold">{title}</p>
    <p class="label hyphens-manual break-words">{m.rail_count({ count: all.length })}</p>
  </div>
  <!-- Tailwind removes the list style, which makes WebKitGTK drop the list role (G-19): name it as a list. -->
  <ol role="list" class="relative flex min-w-0 items-stretch gap-2" aria-label={m.rail_label({ title })}>
    <!-- The line the restore points sit on. -->
    <span class="pointer-events-none absolute inset-x-0 top-1/2 h-px bg-border" aria-hidden="true"></span>
    {#each generations as generation (generation.number)}
      <li class="relative">
        <!-- Each button is named by its number and date, so the restore points can be told apart. -->
        <button
          class="focus-ring flex h-full min-w-16 flex-col justify-center rounded-lg px-3 py-2 text-left transition-colors
            {generation.current ? 'bg-primary text-primary-foreground' : 'bg-background text-muted-foreground hover:text-foreground'}"
          title={describe(generation)}
          aria-label={m.rail_point({ number: String(generation.number), when: describe(generation) })}
          onclick={() => app.navigate("generations")}
        >
          <span class="numeric text-[1.35rem] leading-none font-bold tracking-tight">{generation.number}</span>
          <span class="mt-1 text-[0.72rem] whitespace-nowrap {generation.current ? 'opacity-85' : ''}">
            {generation.current ? m.restore_in_use() : day(generation.createdAt).replace(/,? \d{4}$/, "")}
          </span>
        </button>
        {#if generation.booted && !generation.current}
          <span class="absolute -top-2 left-2 rounded bg-warning px-1 text-[0.65rem] font-semibold text-white">{m.rail_running()}</span>
        {/if}
      </li>
    {:else}
      <li class="relative rounded-lg bg-background px-3 py-2 text-muted-foreground">{m.rail_none()}</li>
    {/each}
    <li class="relative min-w-0 flex-1">
      {#if building}
        <button class="prepared focus-ring flex h-full w-full items-center gap-3 px-4 py-2 text-left" onclick={() => app.navigate("activity")}>
          <LoaderIcon class="size-4 shrink-0 animate-spin text-prepared-foreground" />
          <span class="min-w-0">
            <span class="block font-semibold text-prepared-foreground">{m.rail_building_next()}</span>
            <span class="block truncate text-[0.78rem] text-muted-foreground">{m.rail_not_active_until_applied()}</span>
          </span>
        </button>
      {:else if review && changes}
        <button
          class="prepared focus-ring flex h-full w-full items-center gap-3 px-4 py-2 text-left hover:bg-accent/70"
          onclick={() => {
            app.selectedReview = review.id;
            app.navigate("changes");
          }}
        >
          <span class="min-w-0">
            <span class="block font-semibold text-prepared-foreground">{m.rail_next_ready()}</span>
            <span class="block truncate text-[0.78rem] text-muted-foreground">
              {output?.compared ? m.rail_changes({ count: changes.changed, added: changes.added, removed: changes.removed }) : m.rail_first_build()}
            </span>
          </span>
        </button>
      {:else}
        <button
          class="focus-ring flex h-full w-full items-center gap-2 rounded-xl border-2 border-dashed border-border bg-card px-4 py-2 text-left text-muted-foreground hover:border-prepared/60 hover:text-foreground disabled:pointer-events-none disabled:opacity-50"
          disabled={app.busy || !app.connected}
          onclick={() => app.prepare(profile)}
        >
          <PlusIcon class="size-4 shrink-0" />
          <span class="truncate">{profile === "home" ? m.rail_prepare_home() : m.rail_prepare_system()}</span>
        </button>
      {/if}
    </li>
  </ol>
</div>
