<script lang="ts">
  import GitCompareIcon from "@lucide/svelte/icons/git-compare";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import ExternalLinkIcon from "@lucide/svelte/icons/external-link";
  import TrashIcon from "@lucide/svelte/icons/trash-2";
  import RotateCwIcon from "@lucide/svelte/icons/rotate-cw";
  import { Button } from "$lib/components/ui/button";
  import { t, tn } from "$lib/i18n";
  import T from "$lib/components/app/T.svelte";
  import { app } from "$lib/app.svelte";
  import type { BuildReview, ReviewOutput } from "../../src/contracts";
  import { bytes, dateTime, day, editedPackages, relative, reviewSummary, scopeText, shortRev, summarize } from "$lib/format";
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
        review.edits.files.length === 1
          ? t("Writes your edits to {path} in your configuration.", { path: review.edits.files[0].path })
          : t("Writes your edits to {count} files in your configuration.", { count: review.edits.files.length }),
      );
    const created = review.edits?.files.filter((file) => file.created) ?? [];
    if (created.length)
      points.push(
        tn("Creates {files} and adds it to Git, so Nix can read it.", "Creates {files} and adds them to Git, so Nix can read them.", created.length, {
          files: created.map((file) => file.path).join(", "),
        }),
      );
    if (review.inputs) points.push(t("Saves the reviewed source versions (flake.lock) in your configuration."));
    const restored = review.undo?.point.files ?? [];
    const deleted = restored.filter((file) => file.created).map((file) => file.path);
    const put = restored.filter((file) => !file.created).map((file) => file.path);
    if (put.length)
      points.push(tn("Puts back {files} as it was before that change.", "Puts back {files} as they were before that change.", put.length, { files: put.join(", ") }));
    if (deleted.length) points.push(t("Deletes {files}, which that change created.", { files: deleted.join(", ") }));
    if (review.home && activation === "switch") points.push(t("Switches your user's apps and settings to the reviewed version."));
    if (review.home && review.system && review.scope !== "system" && activation === "boot")
      points.push(t("Your apps are updated after the restart, once the computer runs the new system."));
    if (review.system)
      points.push(
        activation === "boot"
          ? t("Makes the reviewed system the default boot entry. The running system is unchanged until you restart.")
          : t("Switches the running system to the reviewed version. You'll be asked for your password."),
      );
    if (kernelChange && activation === "switch") points.push(t("The kernel changes; restart afterwards to use it."));
    points.push(t("The current version stays as a restore point you can go back to."));
    app.confirm({
      title: activation === "boot" ? t("Apply on next boot?") : t("Apply these changes now?"),
      description: t("Prepared {time} for {host}.", { time: relative(review.createdAt, app.now), host: review.host }),
      request: app.request("review-apply", {
        host: review.host,
        scope: review.scope,
        reviewId: review.id,
        activation,
      }),
      confirmLabel: activation === "boot" ? t("Apply on next boot") : t("Apply now"),
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
      <h1 class="title">{t("Changes")}</h1>
      <p class="mt-1 text-muted-foreground">{t("Prepared builds waiting for your decision. Nothing here is active yet.")}</p>
    </div>
    {#if app.reviews.length > 1}
      <select
        class="focus-ring h-8 rounded-md border bg-card px-2 text-xs"
        aria-label={t("Prepared changes")}
        value={review?.id}
        onchange={(event) => (app.selectedReview = event.currentTarget.value)}
      >
        {#each app.reviews as item (item.id)}
          <option value={item.id}>{t("{host}, {scope}, prepared {time}", { host: item.host, scope: scopeText(item.scope), time: relative(item.createdAt, app.now) })}</option>
        {/each}
      </select>
    {/if}
  </header>

  {#if !review}
    <section class="panel">
      <EmptyState icon={GitCompareIcon} title={preparing ? t("Preparing changes…") : t("No prepared changes")}>
        {#if preparing}
          {t("The build is running; its changes will appear here when it finishes.")}
        {:else}
          {t("Prepare changes to build your configuration and compare it with what's running.")}
        {/if}
        {#snippet actions()}
          {#if preparing}
            <Button variant="outline" onclick={() => app.navigate("activity")}>{t("Show progress")}</Button>
          {:else}
            <Button disabled={app.busy || !app.connected} onclick={() => app.prepare("home")}>{t("Prepare your user")}</Button>
            <Button variant="outline" disabled={app.busy || !app.connected} onclick={() => app.prepare("all")}>{t("Prepare your user and system")}</Button>
          {/if}
        {/snippet}
      </EmptyState>
    </section>
  {:else}
    <section class="prepared p-5">
      <div class="flex flex-wrap items-start gap-4">
        <div class="min-w-0 flex-1">
          <p class="label">{t("Prepared {time} for {host}, not active yet", { time: relative(review.createdAt, app.now), host: review.host })}</p>
          <h2 class="heading mt-1"><ReviewTitle {review} /></h2>
          <p class="mt-1 max-w-prose">{reviewSummary(review)}</p>
        </div>
        <div class="flex flex-wrap gap-2">
          <Button variant="ghost" disabled={app.busy} onclick={() => app.discard(review)}><TrashIcon /> {t("Discard")}</Button>
          {#if review.system}
            <Button variant="outline" disabled={app.busy || Boolean(lockChanged)} onclick={() => apply(review, "boot")}>{t("Apply on next boot")}</Button>
          {/if}
          <Button disabled={app.busy || Boolean(lockChanged)} onclick={() => apply(review, "switch")}>{t("Apply now")}</Button>
        </div>
      </div>

      {#if lockChanged || checkoutChanged || otherHost || kernelChange || draftMoved}
        <div class="mt-4 space-y-2">
          {#if lockChanged}
            <p class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-destructive">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0" />
              {t("The source versions changed since these updates were prepared. Discard and prepare them again.")}
            </p>
          {:else if checkoutChanged}
            <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" />
              {t("Your configuration changed since this was prepared. Applying uses exactly what was built here; prepare again to include newer edits.")}
            </p>
          {/if}
          {#if draftMoved}
            <p class="flex gap-2 rounded-lg bg-muted px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
              {t("Your pending edits changed since this was prepared. Applying writes the edits shown below; prepare again to include the others.")}
            </p>
          {/if}
          {#if otherHost}
            <p class="flex gap-2 rounded-lg bg-warning/10 px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-warning" />
              {t("Built for {host}, but this computer is {current}.", { host: review.host, current: app.snapshot?.config.hostKey ?? "" })}
            </p>
          {/if}
          {#if kernelChange}
            <p class="flex gap-2 rounded-lg bg-muted px-3 py-2">
              <TriangleAlertIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
              {t("New kernel {version}; a restart is needed to use it.", { version: review.system?.kernel?.after ?? "" })}
            </p>
          {/if}
        </div>
      {/if}

      {#if review.afterBoot}
        <p class="mt-4 flex gap-2 rounded-lg bg-card px-3 py-2">
          <RotateCwIcon class="mt-0.5 size-4 shrink-0 text-muted-foreground" />
          {t("The system update is used after the next restart. Your apps are updated then too, or apply now to update them right away.")}
        </p>
      {/if}

      {#if review.undo}
        <div class="mt-4 rounded-lg bg-card px-3 py-2">
          <p class="font-medium">{t("Goes back to how it was before {date}, undoing:", { date: dateTime(review.undo.appliedAt) })}</p>
          <ul class="mt-1 list-disc pl-5 text-muted-foreground">
            {#each review.undo.point.changes as change, index (index)}<li>{change}</li>{/each}
          </ul>
          {#if review.undo.point.files.length}
            <p class="mt-1 text-xs text-muted-foreground">
              {t("Applying also restores {files}.", { files: review.undo.point.files.map((file) => file.path).join(", ") })}
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
              <dt class="label">{key === "home" ? t("Your user's apps") : t("System apps")}</dt>
              <dd class="mt-1 text-lg font-semibold">{item.compared ? tn("{count} change", "{count} changes", counts.changed + counts.added + counts.removed) : t("New")}</dd>
              <dd class="text-xs text-muted-foreground">
                {#if item.compared}{t("{changed} updated, {added} new, {removed} removed", { changed: counts.changed, added: counts.added, removed: counts.removed })}{:else}{t("Nothing active to compare with")}{/if}
              </dd>
            </div>
            <div class="rounded-lg bg-card p-3">
              <dt class="label">{key === "home" ? t("Your user's disk use") : t("System disk use")}<TechName name="closure" /></dt>
              <dd class="mt-1 text-lg font-semibold">{bytes(item.closureSize)}</dd>
              <dd class="text-xs {delta && delta > 0 ? 'text-warning' : 'text-muted-foreground'}">
                {delta === undefined ? "—" : t("{size} compared to now", { size: bytes(delta, true) })}
              </dd>
            </div>
          {/each}
          {#if review.system?.version?.after && review.system.version.after !== review.system.version.before}
            <div class="rounded-lg bg-card p-3">
              <dt class="label">NixOS</dt>
              <dd class="mt-1 font-semibold">{review.system.version.after}</dd>
              <dd class="text-xs text-muted-foreground">{t("from {version}", { version: review.system.version.before ?? t("unknown") })}</dd>
            </div>
          {/if}
        </dl>
      {/if}
    </section>

    {#if review.edits}
      <section class="panel overflow-hidden" aria-labelledby="edits-title">
        <div class="border-b px-4 py-3">
          <h2 id="edits-title" class="font-semibold">{t("Your edits")}</h2>
          <p class="text-xs text-muted-foreground">{t("Checked by evaluating {host}'s configuration with and without them. They are written to your configuration when you apply.", { host: review.host })}</p>
        </div>
        <div class="grid gap-4 p-4 {detailed ? 'lg:grid-cols-[minmax(0,1fr)_minmax(0,1.3fr)]' : ''}">
          <div class="space-y-3">
            {#each review.edits.effects as effect (effect.scope)}
              {@const packages = editedPackages(effect)}
              <div>
                <p class="label">{effect.scope === "home" ? t("Your user") : t("System")}</p>
                <ul class="mt-1 space-y-1">
                  {#each packages.added as item (item.out + item.file)}
                    <li><span class="font-medium text-success">{t("Adds")}</span> {item.name} <span class="font-mono text-[0.72rem] text-muted-foreground">{item.version ?? ""}</span></li>
                  {/each}
                  {#each packages.removed as item (item.out + item.file)}
                    <li><span class="font-medium text-destructive">{t("Removes")}</span> {item.name} <span class="font-mono text-[0.72rem] text-muted-foreground">{item.version ?? ""}</span></li>
                  {/each}
                  {#each packages.updated as item (item.out + item.file)}
                    <li class="text-muted-foreground">{t("Updates {name}", { name: item.name })} <span class="font-mono text-[0.72rem]">{item.version ?? ""}</span></li>
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
              <summary class="focus-ring cursor-pointer rounded text-muted-foreground hover:text-foreground">{tn("{count} edit in this review", "{count} edits in this review", review.edits.changes.length)}</summary>
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
        {expanded ? t("Hide the details") : t("Show all details")}
      </Button>
    {/if}

    {#if detailed && review.inputs?.changes.length}
      <section class="panel overflow-hidden">
        <h2 class="border-b px-4 py-3 font-semibold">{t("Software sources")}<TechName name="flake inputs" /></h2>
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
                  <a class="inline-flex items-center gap-0.5 font-mono text-primary hover:underline" href={change.after.url} target="_blank" rel="noreferrer" aria-label={t("{name} at {rev}, opens in a new window", { name: change.name, rev: shortRev(change.after.rev) })}>{shortRev(change.after.rev)}<ExternalLinkIcon class="size-3" /></a>
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
          <h2 class="heading">{review.edits ? t("Everything that differs from what's running") : t("Packages")}</h2>
          {#if review.home && review.system}
            <Segmented
              label={t("Configuration")}
              bind:value={tab}
              options={[
                { value: "home", label: t("Your user"), count: review.home.changes.length },
                { value: "system", label: t("System"), count: review.system.changes.length },
              ]}
            />
          {/if}
        </div>
        {#if output}
          {#if output.compared}
            {#key `${review.id}-${tab}`}<PackageChanges changes={output.changes} />{/key}
          {:else}
            <p class="text-muted-foreground">
              {tab === "home"
                ? t("There's no active home configuration to compare with, so everything in this build is new.")
                : t("There's no active system configuration to compare with, so everything in this build is new.")}
            </p>
          {/if}
        {/if}
      </section>
    {/if}
  {/if}
</div>
