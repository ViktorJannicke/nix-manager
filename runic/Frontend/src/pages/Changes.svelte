<script lang="ts">
  import GitCompareIcon from "@lucide/svelte/icons/git-compare";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import ExternalLinkIcon from "@lucide/svelte/icons/external-link";
  import TrashIcon from "@lucide/svelte/icons/trash-2";
  import RotateCwIcon from "@lucide/svelte/icons/rotate-cw";
  import { Button } from "$lib/components/ui/button";
  import type { BuildReview, ReviewOutput } from "../generated/types.js";
  import { app } from "$lib/app.svelte";
  import { m, stored } from "$lib/i18n";
  import { describeChange, showValue } from "$lib/changes";
  import { bytes, dateTime, day, editedPackages, relative, reviewSummary, scopeText, shortRev, signedBytes, summarize } from "$lib/format";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import Segmented from "$lib/components/app/Segmented.svelte";
  import TechName from "$lib/components/app/TechName.svelte";
  import PackageChanges from "$lib/components/app/PackageChanges.svelte";
  import ReviewTitle from "$lib/components/app/ReviewTitle.svelte";
  import DiffView from "$lib/components/app/DiffView.svelte";

  const review = $derived(app.reviews.find((r) => r.id === app.selectedReview) ?? app.reviews[0]);
  let tab = $state<"home" | "system">("home");
  $effect(() => {
    if (review && !review[tab]) tab = review.home ? "home" : "system";
  });
  const output = $derived(review?.[tab] ?? undefined);
  const repository = $derived(app.system?.repository);
  const checkoutChanged = $derived(Boolean(review && repository && review.source !== "generation" && review.fingerprint !== repository.fingerprint));
  const lockChanged = $derived(Boolean(review?.inputs && repository && review.inputs.baseLockHash !== repository.lockHash));
  const otherHost = $derived(Boolean(review && review.host !== app.config?.hostKey));
  const kernelChange = $derived(Boolean(review?.system?.kernel && review.system.kernel.before !== review.system.kernel.after));
  const preparing = $derived(app.active?.request.action === "review-build");
  // Simple mode keeps sizes, file diffs, sources and the full package list one click away.
  let expanded = $state(false);
  const detailed = $derived(app.detail === "detailed" || expanded);
  const draftMoved = $derived(Boolean(review?.edits && app.draft && review.edits.hash !== app.draft.hash));

  function apply(review: BuildReview, activation: "switch" | "boot") {
    const points: string[] = [];
    if (review.edits)
      points.push(
        review.edits.files.length === 1
          ? m.changes_writes_file({ path: review.edits.files[0].path })
          : m.changes_writes_files({ count: review.edits.files.length }),
      );
    const created = review.edits?.files.filter((file) => file.created) ?? [];
    if (created.length) points.push(m.changes_creates_files({ count: created.length, files: created.map((file) => file.path) }));
    if (review.inputs) points.push(m.changes_saves_lock());
    const restored = review.undo?.point.files ?? [];
    const deleted = restored.filter((file) => file.created).map((file) => file.path);
    const put = restored.filter((file) => !file.created).map((file) => file.path);
    if (put.length) points.push(m.changes_puts_back({ count: put.length, files: put }));
    if (deleted.length) points.push(m.changes_deletes({ files: deleted }));
    if (review.home && activation === "switch") points.push(m.changes_switches_home());
    if (review.home && review.system && review.scope !== "system" && activation === "boot") points.push(m.changes_home_after_restart());
    if (review.system) points.push(activation === "boot" ? m.changes_boot_default() : m.changes_switches_system());
    if (kernelChange && activation === "switch") points.push(m.changes_kernel_restart());
    points.push(m.changes_restore_point_kept());
    void app.confirm({
      title: activation === "boot" ? m.changes_apply_on_boot_title() : m.changes_apply_now_title(),
      description: m.changes_prepared_for({ time: relative(review.createdAt, app.now), host: review.host }),
      request: app.request("review-apply", { host: review.host, scope: review.scope, reviewId: review.id, activation }),
      confirmLabel: activation === "boot" ? m.changes_apply_on_boot() : m.changes_apply_now(),
      points,
    });
  }

  const sizeChange = (output: ReviewOutput) =>
    output.closureSize !== null && output.currentClosureSize !== null ? Number(output.closureSize) - Number(output.currentClosureSize) : undefined;
</script>

<div class="space-y-6">
  <header class="flex flex-wrap items-end justify-between gap-4">
    <div class="min-w-0">
      <h1 class="title">{m.nav_changes()}</h1>
      <p class="mt-1 text-muted-foreground">{m.changes_intro()}</p>
    </div>
    {#if app.reviews.length > 1}
      <select
        class="focus-ring h-8 rounded-md border bg-card px-2 text-xs"
        aria-label={m.changes_prepared_changes()}
        value={review?.id}
        onchange={(event) => (app.selectedReview = event.currentTarget.value)}
      >
        {#each app.reviews as item (item.id)}
          <option value={item.id}>{m.changes_option({ host: item.host, scope: scopeText(item.scope), time: relative(item.createdAt, app.now) })}</option>
        {/each}
      </select>
    {/if}
  </header>

  {#if !review}
    <section class="panel">
      <EmptyState icon={GitCompareIcon} title={preparing ? m.changes_preparing() : m.changes_none()}>
        {preparing ? m.changes_preparing_body() : m.changes_none_body()}
        {#snippet actions()}
          {#if preparing}
            <Button variant="outline" onclick={() => app.navigate("activity")}>{m.changes_show_progress()}</Button>
          {:else}
            <Button disabled={app.busy || !app.connected} onclick={() => app.prepare("home")}>{m.changes_prepare_home()}</Button>
            <Button variant="outline" disabled={app.busy || !app.connected} onclick={() => app.prepare("all")}>{m.changes_prepare_all()}</Button>
          {/if}
        {/snippet}
      </EmptyState>
    </section>
  {:else}
    <section class="prepared p-5" aria-labelledby="review-title">
      <div class="flex flex-wrap items-start gap-4">
        <div class="min-w-0 flex-1">
          <p class="label">{m.changes_prepared_not_active({ time: relative(review.createdAt, app.now), host: review.host })}</p>
          <h2 id="review-title" class="heading mt-1"><ReviewTitle {review} /></h2>
          <p class="mt-1 max-w-prose">{reviewSummary(review)}</p>
        </div>
        <div class="flex flex-wrap gap-2">
          <Button variant="ghost" disabled={app.busy} aria-describedby="review-title" onclick={() => app.discard(review)}><TrashIcon /> {m.changes_discard()}</Button>
          {#if review.system}
            <Button variant="outline" disabled={app.busy || !app.connected || lockChanged} aria-describedby="review-title" onclick={() => apply(review, "boot")}>{m.changes_apply_on_boot()}</Button>
          {/if}
          <Button disabled={app.busy || !app.connected || lockChanged} aria-describedby="review-title" onclick={() => apply(review, "switch")}>{m.changes_apply_now()}</Button>
        </div>
      </div>

      {#if lockChanged || checkoutChanged || otherHost || kernelChange || draftMoved}
        <div class="mt-4 space-y-2">
          {#if lockChanged}
            <p class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-destructive">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0" />
              {m.changes_lock_changed()}
            </p>
          {:else if checkoutChanged}
            <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" />
              {m.changes_checkout_changed()}
            </p>
          {/if}
          {#if draftMoved}
            <p class="flex gap-2 rounded-lg bg-muted px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
              {m.changes_draft_moved()}
            </p>
          {/if}
          {#if otherHost}
            <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" />
              {m.changes_other_host({ host: review.host, current: app.config?.hostKey ?? "" })}
            </p>
          {/if}
          {#if kernelChange}
            <p class="flex gap-2 rounded-lg bg-muted px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
              {m.changes_new_kernel({ version: review.system?.kernel?.after ?? "" })}
            </p>
          {/if}
        </div>
      {/if}

      {#if review.afterBoot}
        <p class="mt-4 flex gap-2 rounded-lg bg-card px-3 py-2">
          <RotateCwIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
          {m.changes_after_boot()}
        </p>
      {/if}

      {#if review.undo}
        <div class="mt-4 rounded-lg bg-card px-3 py-2">
          <p id="undo-heading" class="font-medium">{m.changes_undo_before({ date: dateTime(review.undo.appliedAt) })}</p>
          <ul role="list" aria-labelledby="undo-heading" class="mt-1 list-disc pl-5 text-muted-foreground">
            {#each review.undo.point.changes as change, index (index)}<li>{stored(change)}</li>{/each}
          </ul>
          {#if review.undo.point.files.length}
            <p class="mt-1 text-xs text-muted-foreground">{m.changes_undo_restores({ files: review.undo.point.files.map((file) => file.path) })}</p>
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
              <dt class="label">{key === "home" ? m.changes_home_apps() : m.changes_system_apps()}</dt>
              <dd class="mt-1 text-lg font-semibold">{item.compared ? m.changes_count({ count: counts.changed + counts.added + counts.removed }) : m.packages_new()}</dd>
              <dd class="text-xs text-muted-foreground">
                {item.compared ? m.changes_counts({ changed: counts.changed, added: counts.added, removed: counts.removed }) : m.changes_nothing_to_compare()}
              </dd>
            </div>
            <div class="rounded-lg bg-card p-3">
              <dt class="label">{key === "home" ? m.changes_home_disk() : m.changes_system_disk()}<TechName name="closure" /></dt>
              <dd class="mt-1 text-lg font-semibold">{item.closureSize === null ? "—" : bytes(item.closureSize)}</dd>
              <dd class="text-xs {delta && delta > 0 ? 'text-warning' : 'text-muted-foreground'}">
                {delta === undefined ? "—" : m.changes_compared_to_now({ size: signedBytes(delta) })}
              </dd>
            </div>
          {/each}
          {#if review.system?.version?.after && review.system.version.after !== review.system.version.before}
            <div class="rounded-lg bg-card p-3">
              <dt class="label">NixOS</dt>
              <dd class="mt-1 font-semibold">{review.system.version.after}</dd>
              <dd class="text-xs text-muted-foreground">{m.changes_from_version({ version: review.system.version.before ?? m.format_unknown() })}</dd>
            </div>
          {/if}
        </dl>
      {/if}
    </section>

    {#if review.edits}
      <section class="panel overflow-hidden" aria-labelledby="edits-title">
        <div class="border-b px-4 py-3">
          <h2 id="edits-title" class="font-semibold">{m.changes_your_edits()}</h2>
          <p class="text-xs text-muted-foreground">{m.changes_edits_checked({ host: review.host })}</p>
        </div>
        <div class="grid gap-4 p-4 {detailed ? 'lg:grid-cols-[minmax(0,1fr)_minmax(0,1.3fr)]' : ''}">
          <div class="space-y-3">
            {#each review.edits.effects as effect (effect.scope)}
              {@const packages = editedPackages(effect)}
              <div>
                <p id="effect-{effect.scope}" class="label">{effect.scope === "home" ? m.scope_home() : m.scope_system()}</p>
                <ul role="list" aria-labelledby="effect-{effect.scope}" class="mt-1 space-y-1">
                  {#each packages.added as item (item.out + item.file)}
                    <li><span class="font-medium text-success">{m.changes_adds()}</span> {item.name} <span class="font-mono text-[0.72rem] text-muted-foreground">{item.version ?? ""}</span></li>
                  {/each}
                  {#each packages.removed as item (item.out + item.file)}
                    <li><span class="font-medium text-destructive">{m.changes_removes()}</span> {item.name} <span class="font-mono text-[0.72rem] text-muted-foreground">{item.version ?? ""}</span></li>
                  {/each}
                  {#each packages.updated as item (item.out + item.file)}
                    <li class="text-muted-foreground">{m.changes_updates({ name: item.name })} <span class="font-mono text-[0.72rem]">{item.version ?? ""}</span></li>
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
              <summary class="focus-ring cursor-pointer rounded text-muted-foreground hover:text-foreground">{m.changes_edits_in_review({ count: review.edits.changes.length })}</summary>
              <ul role="list" class="mt-1 space-y-0.5">
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
        {expanded ? m.changes_hide_details() : m.changes_show_details()}
      </Button>
    {/if}

    {#if detailed && review.inputs?.changes.length}
      <section class="panel overflow-hidden" aria-labelledby="inputs-title">
        <h2 id="inputs-title" class="border-b px-4 py-3 font-semibold">{m.updates_sources()}<TechName name="flake inputs" /></h2>
        <ul role="list" aria-labelledby="inputs-title" class="divide-y">
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
                  <a class="inline-flex items-center gap-0.5 font-mono text-primary hover:underline" href={change.after.url} target="_blank" rel="noreferrer" aria-label={m.updates_revision_link({ name: change.name, rev: shortRev(change.after.rev) })}>{shortRev(change.after.rev)}<ExternalLinkIcon class="size-3" /></a>
                {:else}<span class="font-mono">{shortRev(change.after?.rev)}</span>{/if}
              </p>
            </li>
          {/each}
        </ul>
      </section>
    {/if}

    {#if detailed}
      <section class="panel p-4" aria-labelledby="packages-title">
        <div class="mb-3 flex items-center justify-between gap-3">
          <h2 id="packages-title" class="heading">{review.edits ? m.changes_everything_differs() : m.changes_packages()}</h2>
          {#if review.home && review.system}
            <Segmented
              label={m.overview_configuration()}
              bind:value={tab}
              options={[
                { value: "home", label: m.scope_home(), count: review.home.changes.length },
                { value: "system", label: m.scope_system(), count: review.system.changes.length },
              ]}
            />
          {/if}
        </div>
        {#if output}
          {#if output.compared}
            {#key `${review.id}-${tab}`}<PackageChanges changes={output.changes} label={tab === "home" ? m.changes_packages_home() : m.changes_packages_system()} />{/key}
          {:else}
            <p class="text-muted-foreground">{tab === "home" ? m.changes_no_home_to_compare() : m.changes_no_system_to_compare()}</p>
          {/if}
        {/if}
      </section>
    {/if}
  {/if}
</div>
