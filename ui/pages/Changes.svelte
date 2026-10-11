<script lang="ts">
  import GitCompareIcon from "@lucide/svelte/icons/git-compare";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import ExternalLinkIcon from "@lucide/svelte/icons/external-link";
  import TrashIcon from "@lucide/svelte/icons/trash-2";
  import RotateCwIcon from "@lucide/svelte/icons/rotate-cw";
  import { Button } from "$lib/components/ui/button";
  import { app } from "$lib/app.svelte";
  import type { BuildReview, ReviewOutput } from "../../src/contracts";
  import { bytes, dateTime, day, editedPackages, plural, relative, reviewSummary, shortRev, summarize } from "$lib/format";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import TechName from "$lib/components/app/TechName.svelte";
  import PackageChanges from "$lib/components/app/PackageChanges.svelte";
  import ReviewTitle from "$lib/components/app/ReviewTitle.svelte";
  import DiffView from "$lib/components/app/DiffView.svelte";
  import { describeChange, showValue } from "$lib/software.svelte";

  const review = $derived(app.reviews.find((r) => r.id === app.selectedReview) ?? app.reviews[0]);
  let tab = $state<"home" | "system">("home");
  $effect(() => {
    if (review && !review[tab]) tab = review.home ? "home" : "system";
  });
  const output = $derived(review?.[tab]);
  const repository = $derived(app.system?.repository);
  const checkoutChanged = $derived(
    review && repository && review.source !== "generation" && review.fingerprint !== repository.fingerprint,
  );
  const lockChanged = $derived(
    review?.inputs && repository && review.inputs.baseLockHash !== repository.lockHash,
  );
  const otherHost = $derived(review && review.host !== app.snapshot?.config.hostKey);
  const kernelChange = $derived(
    review?.system?.kernel && review.system.kernel.before !== review.system.kernel.after,
  );
  const preparing = $derived(app.active?.request.action === "review-build");
  // Simple mode keeps sizes, file diffs, sources and the full package list one click away.
  let expanded = $state(false);
  const detailed = $derived(app.detail === "detailed" || expanded);
  const draftMoved = $derived(review?.edits && app.snapshot && review.edits.hash !== app.snapshot.draft.hash);

  function apply(review: BuildReview, activation: "switch" | "boot") {
    const points: string[] = [];
    if (review.edits)
      points.push(
        `Writes your edits to ${review.edits.files.length === 1 ? review.edits.files[0].path : `${review.edits.files.length} files`} in your configuration.`,
      );
    const created = review.edits?.files.filter((file) => file.created) ?? [];
    if (created.length)
      points.push(`Creates ${created.map((file) => file.path).join(", ")} and adds ${created.length === 1 ? "it" : "them"} to Git, so Nix can read ${created.length === 1 ? "it" : "them"}.`);
    if (review.inputs) points.push("Saves the reviewed source versions (flake.lock) in your configuration.");
    const restored = review.undo?.point.files ?? [];
    const deleted = restored.filter((file) => file.created).map((file) => file.path);
    const put = restored.filter((file) => !file.created).map((file) => file.path);
    if (put.length) points.push(`Puts back ${put.join(", ")} as ${put.length === 1 ? "it was" : "they were"} before that change.`);
    if (deleted.length) points.push(`Deletes ${deleted.join(", ")}, which that change created.`);
    if (review.home && activation === "switch") points.push("Switches your user's apps and settings to the reviewed version.");
    if (review.home && review.system && review.scope !== "system" && activation === "boot")
      points.push("Your apps are updated after the restart, once the computer runs the new system.");
    if (review.system)
      points.push(
        activation === "boot"
          ? "Makes the reviewed system the default boot entry. The running system is unchanged until you restart."
          : "Switches the running system to the reviewed version. You'll be asked for your password.",
      );
    if (kernelChange && activation === "switch") points.push("The kernel changes; restart afterwards to use it.");
    points.push("The current version stays as a restore point you can go back to.");
    app.confirm({
      title: activation === "boot" ? "Apply on next boot?" : "Apply these changes now?",
      description: `Prepared ${relative(review.createdAt, app.now)} for ${review.host}.`,
      request: app.request("review-apply", {
        host: review.host,
        scope: review.scope,
        reviewId: review.id,
        activation,
      }),
      confirmLabel: activation === "boot" ? "Apply on next boot" : "Apply now",
      points,
    });
  }

  const sizeChange = (output: ReviewOutput) =>
    output.closureSize !== undefined && output.currentClosureSize !== undefined
      ? output.closureSize - output.currentClosureSize
      : undefined;
</script>

<div class="space-y-6">
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div class="min-w-0">
      <h1 class="title">Changes</h1>
      <p class="mt-1 text-muted-foreground">Prepared builds waiting for your decision. Nothing here is active yet.</p>
    </div>
    {#if app.reviews.length > 1}
      <select
        class="focus-ring h-8 rounded-md border bg-card px-2 text-xs"
        aria-label="Prepared changes"
        value={review?.id}
        onchange={(event) => (app.selectedReview = event.currentTarget.value)}
      >
        {#each app.reviews as item (item.id)}
          <option value={item.id}>{item.host}, {item.scope}, prepared {relative(item.createdAt, app.now)}</option>
        {/each}
      </select>
    {/if}
  </header>

  {#if !review}
    <section class="panel">
      <EmptyState icon={GitCompareIcon} title={preparing ? "Preparing changes…" : "No prepared changes"}>
        {#if preparing}
          The build is running; its changes will appear here when it finishes.
        {:else}
          Prepare changes to build your configuration and compare it with what's running.
        {/if}
        {#snippet actions()}
          {#if preparing}
            <Button variant="outline" onclick={() => app.navigate("activity")}>Show progress</Button>
          {:else}
            <Button disabled={app.busy || !app.connected} onclick={() => app.prepare("home")}>Prepare your user</Button>
            <Button variant="outline" disabled={app.busy || !app.connected} onclick={() => app.prepare("all")}>Prepare your user and system</Button>
          {/if}
        {/snippet}
      </EmptyState>
    </section>
  {:else}
    <section class="prepared p-5">
      <div class="flex flex-wrap items-start gap-4">
        <div class="min-w-0 flex-1">
          <p class="label">Prepared {relative(review.createdAt, app.now)} for {review.host}, not active yet</p>
          <h2 class="heading mt-1"><ReviewTitle {review} /></h2>
          <p class="mt-1 max-w-prose">{reviewSummary(review)}</p>
        </div>
        <div class="flex flex-wrap gap-2">
          <Button variant="ghost" disabled={app.busy} onclick={() => app.discard(review)}><TrashIcon /> Discard</Button>
          {#if review.system}
            <Button variant="outline" disabled={app.busy || Boolean(lockChanged)} onclick={() => apply(review, "boot")}>Apply on next boot</Button>
          {/if}
          <Button disabled={app.busy || Boolean(lockChanged)} onclick={() => apply(review, "switch")}>Apply now</Button>
        </div>
      </div>

      {#if lockChanged || checkoutChanged || otherHost || kernelChange || draftMoved}
        <div class="mt-4 space-y-2">
          {#if lockChanged}
            <p class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-destructive">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0" />
              The source versions changed since these updates were prepared. Discard and prepare them again.
            </p>
          {:else if checkoutChanged}
            <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" />
              Your configuration changed since this was prepared. Applying uses exactly what was built here; prepare again to include newer edits.
            </p>
          {/if}
          {#if draftMoved}
            <p class="flex gap-2 rounded-lg bg-muted px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
              Your pending edits changed since this was prepared. Applying writes the edits shown below; prepare again to include the others.
            </p>
          {/if}
          {#if otherHost}
            <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" />
              Built for {review.host}, but this computer is {app.snapshot?.config.hostKey}.
            </p>
          {/if}
          {#if kernelChange}
            <p class="flex gap-2 rounded-lg bg-muted px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
              New kernel {review.system?.kernel?.after}; a restart is needed to use it.
            </p>
          {/if}
        </div>
      {/if}

      {#if review.afterBoot}
        <p class="mt-4 flex gap-2 rounded-lg bg-card px-3 py-2">
          <RotateCwIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
          The system update is used after the next restart. Your apps are updated then too, or apply now to update them right away.
        </p>
      {/if}

      {#if review.undo}
        <div class="mt-4 rounded-lg bg-card px-3 py-2">
          <p class="font-medium">Goes back to how it was before {dateTime(review.undo.appliedAt)}, undoing:</p>
          <ul class="mt-1 list-disc pl-5 text-muted-foreground">
            {#each review.undo.point.changes as change, index (index)}<li>{change}</li>{/each}
          </ul>
          {#if review.undo.point.files.length}
            <p class="mt-1 text-xs text-muted-foreground">
              Applying also restores {review.undo.point.files.map((file) => file.path).join(", ")}.
            </p>
          {/if}
        </div>
      {/if}

      {#if detailed}
        <dl class="mt-5 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {#each (["home", "system"] as const).filter((key) => review[key]) as key (key)}
            {@const item = review[key]!}
            {@const counts = summarize(item.changes)}
            {@const delta = sizeChange(item)}
            <div class="rounded-lg bg-card p-3">
              <dt class="label">{key === "home" ? "Your user's apps" : "System apps"}</dt>
              <dd class="mt-1 text-lg font-semibold">{item.compared ? plural(counts.changed + counts.added + counts.removed, "change") : "New"}</dd>
              <dd class="text-xs text-muted-foreground">
                {#if item.compared}{counts.changed} updated, {counts.added} new, {counts.removed} removed{:else}Nothing active to compare with{/if}
              </dd>
            </div>
            <div class="rounded-lg bg-card p-3">
              <dt class="label">{key === "home" ? "Your user's disk use" : "System disk use"}<TechName name="closure" /></dt>
              <dd class="mt-1 text-lg font-semibold">{bytes(item.closureSize)}</dd>
              <dd class="text-xs {delta && delta > 0 ? 'text-warning' : 'text-muted-foreground'}">
                {delta === undefined ? "—" : `${bytes(delta, true)} compared to now`}
              </dd>
            </div>
          {/each}
          {#if review.system?.version?.after && review.system.version.after !== review.system.version.before}
            <div class="rounded-lg bg-card p-3">
              <dt class="label">NixOS</dt>
              <dd class="mt-1 font-semibold">{review.system.version.after}</dd>
              <dd class="text-xs text-muted-foreground">from {review.system.version.before ?? "unknown"}</dd>
            </div>
          {/if}
        </dl>
      {/if}
    </section>

    {#if review.edits}
      <section class="panel overflow-hidden" aria-labelledby="edits-title">
        <div class="border-b px-4 py-3">
          <h2 id="edits-title" class="font-semibold">Your edits</h2>
          <p class="text-xs text-muted-foreground">Checked by evaluating {review.host}'s configuration with and without them. They are written to your configuration when you apply.</p>
        </div>
        <div class="grid gap-4 p-4 {detailed ? 'lg:grid-cols-[minmax(0,1fr)_minmax(0,1.3fr)]' : ''}">
          <div class="space-y-3">
            {#each review.edits.effects as effect (effect.scope)}
              {@const packages = editedPackages(effect)}
              <div>
                <p class="label">{effect.scope === "home" ? "Your user" : "System"}</p>
                <ul class="mt-1 space-y-1">
                  {#each packages.added as item (item.out + item.file)}
                    <li><span class="font-medium text-success">Adds</span> {item.name} <span class="font-mono text-[0.72rem] text-muted-foreground">{item.version ?? ""}</span></li>
                  {/each}
                  {#each packages.removed as item (item.out + item.file)}
                    <li><span class="font-medium text-destructive">Removes</span> {item.name} <span class="font-mono text-[0.72rem] text-muted-foreground">{item.version ?? ""}</span></li>
                  {/each}
                  {#each packages.updated as item (item.out + item.file)}
                    <li class="text-muted-foreground">Updates {item.name} <span class="font-mono text-[0.72rem]">{item.version ?? ""}</span></li>
                  {/each}
                  {#each effect.options as option (option.option)}
                    <li class="break-words">
                      <span class="font-mono text-[0.75rem]">{option.option}</span>:
                      <span class="text-muted-foreground">{showValue(option.before)}</span> → {showValue(option.after)}
                    </li>
                  {/each}
                </ul>
              </div>
            {/each}
            <details class="text-xs">
              <summary class="focus-ring cursor-pointer rounded text-muted-foreground hover:text-foreground">{plural(review.edits.changes.length, "edit")} in this review</summary>
              <ul class="mt-1 space-y-0.5">
                {#each review.edits.changes as change, index (index)}<li>{describeChange(change).title}</li>{/each}
              </ul>
            </details>
          </div>
          {#if detailed}
            <div class="space-y-2">
              {#each review.edits.files as file (file.path)}<DiffView {file} />{/each}
            </div>
          {/if}
        </div>
      </section>
    {/if}

    {#if app.detail === "simple"}
      <Button variant="outline" aria-expanded={expanded} onclick={() => (expanded = !expanded)}>
        {expanded ? "Hide the details" : "Show all details"}
      </Button>
    {/if}

    {#if detailed && review.inputs?.changes.length}
      <section class="panel overflow-hidden">
        <h2 class="border-b px-4 py-3 font-semibold">Software sources<TechName name="flake inputs" /></h2>
        <ul class="divide-y">
          {#each review.inputs.changes as change (change.name)}
            <li class="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-2.5">
              <div class="min-w-40 flex-1">
                <p class="font-medium">{change.name}</p>
                {#if change.after?.source ?? change.before?.source}<p class="text-xs text-muted-foreground">{change.after?.source ?? change.before?.source}</p>{/if}
              </div>
              <p class="text-xs text-muted-foreground">
                {day(change.before?.lastModified)} <span class="font-mono">{shortRev(change.before?.rev)}</span>
                → <span class="text-foreground">{day(change.after?.lastModified)}</span>
                {#if change.after?.url}
                  <a class="inline-flex items-center gap-0.5 font-mono text-primary hover:underline" href={change.after.url} target="_blank" rel="noreferrer" aria-label="{change.name} at {shortRev(change.after.rev)}, opens in a new window">{shortRev(change.after.rev)}<ExternalLinkIcon class="size-3" /></a>
                {:else}<span class="font-mono">{shortRev(change.after?.rev)}</span>{/if}
              </p>
            </li>
          {/each}
        </ul>
      </section>
    {/if}

    {#if detailed}
      <section class="panel p-4">
        <div class="mb-3 flex items-center justify-between gap-3">
          <h2 class="heading">{review.edits ? "Everything that differs from what's running" : "Packages"}</h2>
          {#if review.home && review.system}
            <Segmented
              label="Configuration"
              bind:value={tab}
              options={[
                { value: "home", label: "Your user", count: review.home.changes.length },
                { value: "system", label: "System", count: review.system.changes.length },
              ]}
            />
          {/if}
        </div>
        {#if output}
          {#if output.compared}
            {#key `${review.id}-${tab}`}<PackageChanges changes={output.changes} />{/key}
          {:else}
            <p class="text-muted-foreground">There's no active {tab} configuration to compare with, so everything in this build is new.</p>
          {/if}
        {/if}
      </section>
    {/if}
  {/if}
</div>
