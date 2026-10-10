<script lang="ts">
  import PlusIcon from "@lucide/svelte/icons/plus";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import type { BuildReview, Generation } from "../../../../src/contracts";
  import { app } from "$lib/app.svelte";
  import { day, plural, relative, summarize } from "$lib/format";

  let { profile, title }: { profile: "home" | "system"; title: string } = $props();

  const generations = $derived(
    [...(app.system?.generations[profile] ?? [])].sort((a, b) => a.number - b.number).slice(-5),
  );
  const review = $derived(app.reviews.find((r: BuildReview) => r.host === app.host && r[profile]));
  const building = $derived(
    app.active?.request.action === "review-build" &&
      (app.active.request.scope === "all" || app.active.request.scope === profile),
  );
  const changes = $derived(review?.[profile] ? summarize(review[profile].changes) : undefined);
  const describe = (generation: Generation) =>
    generation.current ? `Active since ${relative(generation.createdAt, app.now)}` : day(generation.createdAt);
</script>

<div class="grid grid-cols-[5.5rem_1fr] items-center gap-4">
  <div>
    <p class="font-semibold">{title}</p>
    <p class="label">{plural(app.system?.generations[profile].length ?? 0, "generation")}</p>
  </div>
  <ol class="relative flex min-w-0 items-stretch gap-2" aria-label="{title} generations">
    <!-- The line the generations sit on. -->
    <span class="pointer-events-none absolute inset-x-0 top-1/2 h-px bg-border" aria-hidden="true"></span>
    {#each generations as generation (generation.number)}
      <li class="relative">
        <button
          class="focus-ring flex h-full min-w-16 flex-col justify-center rounded-lg px-3 py-2 text-left transition-colors
            {generation.current
              ? 'bg-primary text-primary-foreground'
              : 'bg-background text-muted-foreground hover:text-foreground'}"
          title={describe(generation)}
          onclick={() => app.navigate("generations")}
        >
          <span class="numeric text-[1.35rem] leading-none font-bold tracking-tight">{generation.number}</span>
          <span class="mt-1 text-[0.72rem] whitespace-nowrap {generation.current ? 'opacity-85' : ''}">
            {generation.current ? "Active" : day(generation.createdAt).replace(/,? \d{4}$/, "")}
          </span>
        </button>
        {#if generation.booted && !generation.current}
          <span class="absolute -top-2 left-2 rounded bg-warning px-1 text-[0.65rem] font-semibold text-white">running</span>
        {/if}
      </li>
    {:else}
      <li class="relative rounded-lg bg-background px-3 py-2 text-muted-foreground">No generations yet</li>
    {/each}
    <li class="relative min-w-0 flex-1">
      {#if building}
        <button class="prepared focus-ring flex h-full w-full items-center gap-3 px-4 py-2 text-left" onclick={() => app.navigate("activity")}>
          <LoaderIcon class="size-4 shrink-0 animate-spin text-prepared-foreground" />
          <span class="min-w-0">
            <span class="block font-semibold text-prepared-foreground">Building next</span>
            <span class="block truncate text-[0.78rem] text-muted-foreground">Not active until you apply it</span>
          </span>
        </button>
      {:else if review && changes}
        <button
          class="prepared focus-ring flex h-full w-full items-center gap-3 px-4 py-2 text-left hover:bg-accent/70"
          onclick={() => { app.selectedReview = review.id; app.navigate("changes"); }}
        >
          <span class="min-w-0">
            <span class="block font-semibold text-prepared-foreground">Next, ready to apply</span>
            <span class="block truncate text-[0.78rem] text-muted-foreground">
              {review[profile]?.compared
                ? `${plural(changes.changed, "update")}, ${changes.added} new, ${changes.removed} removed`
                : "First build of this profile"}
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
          <span class="truncate">Prepare the next {profile} generation</span>
        </button>
      {/if}
    </li>
  </ol>
</div>
