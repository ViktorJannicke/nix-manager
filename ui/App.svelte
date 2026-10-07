<script lang="ts">
  import { onMount, tick } from "svelte";
  import type { Attachment } from "svelte/attachments";
  import Activity from "@lucide/svelte/icons/activity";
  import ArchiveRestore from "@lucide/svelte/icons/archive-restore";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import Check from "@lucide/svelte/icons/check";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import CircleAlert from "@lucide/svelte/icons/circle-alert";
  import CircleCheck from "@lucide/svelte/icons/circle-check";
  import CircleDashed from "@lucide/svelte/icons/circle-dashed";
  import CircleX from "@lucide/svelte/icons/circle-x";
  import Clipboard from "@lucide/svelte/icons/clipboard";
  import CloudDownload from "@lucide/svelte/icons/cloud-download";
  import Code2 from "@lucide/svelte/icons/code-2";
  import Download from "@lucide/svelte/icons/download";
  import HardDrive from "@lucide/svelte/icons/hard-drive";
  import Home from "@lucide/svelte/icons/house";
  import Laptop from "@lucide/svelte/icons/laptop";
  import LoaderCircle from "@lucide/svelte/icons/loader-circle";
  import Moon from "@lucide/svelte/icons/moon";
  import PackageCheck from "@lucide/svelte/icons/package-check";
  import Play from "@lucide/svelte/icons/play";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import Search from "@lucide/svelte/icons/search";
  import Settings from "@lucide/svelte/icons/settings";
  import Sun from "@lucide/svelte/icons/sun";
  import Terminal from "@lucide/svelte/icons/terminal";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import X from "@lucide/svelte/icons/x";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import { Switch } from "$lib/components/ui/switch";
  import * as Dialog from "$lib/components/ui/dialog";
  import { connectSession, managerApi } from "$lib/api";
  import { request, type Request } from "../src/domain";
  import type { BuildReview, Generation, InputProposal, ManagerEvent, ManagerState, Operation, Plan, StepOutcome } from "../src/contracts";

  type Page = "overview" | "updates" | "activity" | "generations" | "settings";
  type Theme = "system" | "light" | "dark";
  const labels: Record<Request["action"], string> = {
    build: "Build configuration", switch: "Build and switch",
    boot: "Apply on next boot", rollback: "Roll back configuration",
    gc: "Clean generations and store", check: "Check for updates",
    "t3code-update": "Update T3 Code pin",
    "opencode-update": "Update OpenCode pin", "pince-update": "Update PINCE pin",
    "review-build": "Build reviewed outputs", "review-apply": "Apply reviewed outputs",
  };
  const nav: { id: Page; label: string; icon: typeof Home }[] = [
    { id: "overview", label: "Overview", icon: Home },
    { id: "updates", label: "Updates", icon: CloudDownload },
    { id: "activity", label: "Activity", icon: Activity },
    { id: "generations", label: "Generations", icon: ArchiveRestore },
    { id: "settings", label: "Settings", icon: Settings },
  ];

  let managerState = $state.raw<ManagerState>();
  let loading = $state(true), connected = $state(false), error = $state("");
  let page = $state<Page>(initialPage()), selectedHost = $state("");
  let scope = $state<Request["scope"]>("all");
  let updateInputs = $state(false), cleanup = $state(false);
  let previewOpen = $state(false), previewPlan = $state.raw<Plan>(), pending = $state.raw<Request>();
  let selectedId = $state(""), logText = $state(""), logSearch = $state("");
  let wrapLog = $state(true), followLog = $state(true), logElement = $state.raw<HTMLPreElement>();
  let retention = $state(3);
  let selectedInputs = $state<string[]>([]);
  let theme = $state<Theme>((localStorage.getItem("nix-manager-theme") as Theme | null) || "system");

  let selectedOperation = $derived.by(() => {
    if (!managerState || !selectedId) return undefined;
    return managerState.active?.id === selectedId
      ? managerState.active
      : managerState.operations.find((operation) => operation.id === selectedId);
  });
  let updateCount = $derived(managerState?.updates?.items.length ?? 0);
  let busy = $derived(Boolean(managerState?.active));
  let filteredLog = $derived.by(() => {
    const query = logSearch.trim().toLowerCase();
    return query
      ? logText.split("\n").filter((line) => line.toLowerCase().includes(query)).join("\n")
      : logText;
  });

  function initialPage(): Page {
    const value = new URLSearchParams(location.search).get("view");
    return nav.some((item) => item.id === value) ? (value as Page) : "overview";
  }
  function navigate(next: Page, replace = false) {
    page = next;
    history[replace ? "replaceState" : "pushState"](null, "", next === "overview" ? location.pathname : `?view=${next}`);
  }
  function applyTheme(next: Theme) {
    theme = next;
    localStorage.setItem("nix-manager-theme", next);
    const dark = next === "dark" || (next === "system" && matchMedia("(prefers-color-scheme: dark)").matches);
    document.documentElement.classList.toggle("dark", dark);
  }
  function actionContext(operation: Operation) {
    const selectedScope = operation.request.scope === "all" ? "Home + system" : operation.request.scope === "home" ? "Home" : "System";
    return `${operation.request.host || managerState?.config.hostKey} · ${selectedScope}`;
  }
  const date = (value: string) => new Date(value).toLocaleString(undefined, {
    month: "short", day: "numeric", hour: "2-digit", minute: "2-digit",
  });
  const bytes = (value: number) => `${(value / 1024 / 1024 / 1024).toFixed(2)} GiB`;
  const duration = (operation: Operation) => {
    const seconds = Math.max(0, ((operation.finishedAt ? Date.parse(operation.finishedAt) : Date.now()) - Date.parse(operation.startedAt)) / 1000);
    return seconds < 60 ? `${seconds.toFixed(seconds < 10 ? 1 : 0)}s` : `${Math.floor(seconds / 60)}m ${Math.floor(seconds % 60)}s`;
  };
  async function refresh(selectActive = true) {
    try {
      const next = await managerApi.state();
      managerState = next;
      selectedHost ||= next.config.hostKey;
      if (selectActive && next.active && !selectedId) selectedId = next.active.id;
      if (selectedId) await loadLog(selectedId);
      error = "";
    } catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
    finally { loading = false; }
  }
  async function loadLog(id: string) {
    try { logText = (await managerApi.log(id)).text; await scrollLog(); }
    catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  async function scrollLog() {
    if (!followLog) return;
    await tick();
    if (logElement) logElement.scrollTop = logElement.scrollHeight;
  }
  function operationRequest(action: Request["action"], overrides: Partial<Request> = {}) {
    return request({
      action, host: selectedHost || managerState?.config.hostKey || "",
      scope: action === "boot" ? "system" : action.endsWith("-update") ? "home" : scope,
      update: ["build", "switch", "boot"].includes(action) && updateInputs,
      cleanup: ["switch", "boot"].includes(action) && cleanup, ...overrides,
    });
  }
  async function preview(action: Request["action"], overrides: Partial<Request> = {}) {
    try {
      const next = operationRequest(action, overrides);
      previewPlan = await managerApi.plan(next); pending = next; previewOpen = true; error = "";
    } catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  async function runPending() {
    if (!pending) return;
    try {
      const operation = await managerApi.run(pending);
      previewOpen = false; selectedId = operation.id; navigate("activity"); await refresh(false);
    } catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  async function runCheck() {
    try {
      const operation = await managerApi.run(operationRequest("check"));
      selectedId = operation.id; navigate("activity"); await refresh(false);
    } catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  function buildReview() {
    void preview("review-build", {
      reviewId: crypto.randomUUID(),
      fingerprint: managerState?.advanced.repository.fingerprint,
    });
  }
  function applyReview(review: BuildReview) {
    selectedHost = review.host;
    scope = review.scope;
    void preview("review-apply", {
      reviewId: review.id,
      fingerprint: managerState?.advanced.repository.fingerprint,
    });
  }
  async function selectGeneration(kind: "home" | "system", generation: Generation) {
    try {
      const review = await managerApi.generationReview(kind, generation.number);
      await refresh(false);
      applyReview(review);
    } catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  function toggleInput(name: string) {
    selectedInputs = selectedInputs.includes(name)
      ? selectedInputs.filter((input) => input !== name)
      : [...selectedInputs, name];
  }
  async function proposeInputUpdate() {
    try {
      await managerApi.inputReview(selectedInputs);
      selectedInputs = [];
      await refresh(false);
    } catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  async function applyInputUpdate(proposal: InputProposal) {
    try {
      await managerApi.inputApply(proposal.id, managerState!.advanced.repository.fingerprint);
      await refresh(false);
    } catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  async function removeBuildReview(id: string) {
    if (!confirm("Remove this build review and its GC roots? The outputs may be collected later.")) return;
    try { await managerApi.removeReview(id); await refresh(false); }
    catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  async function cancelOperation() {
    if (!managerState?.active) return;
    try { await managerApi.cancel(managerState.active.id); await refresh(false); }
    catch (caught) { error = caught instanceof Error ? caught.message : String(caught); }
  }
  function selectOperation(operation: Operation) { selectedId = operation.id; void loadLog(operation.id); }
  async function copyLog() { await navigator.clipboard.writeText(logText); }
  function handleLogScroll(event: Event) {
    const element = event.currentTarget as HTMLPreElement;
    if (element.scrollHeight - element.scrollTop - element.clientHeight > 80)
      followLog = false;
  }
  const captureLog: Attachment<HTMLPreElement> = (element) => {
    logElement = element;
    return () => {
      if (logElement === element) logElement = undefined;
    };
  };
  function downloadLog() {
    const url = URL.createObjectURL(new Blob([logText], { type: "text/plain" }));
    const link = document.createElement("a"); link.href = url; link.download = `nix-manager-${selectedId}.log`; link.click(); URL.revokeObjectURL(url);
  }
  function handleEvent(event: ManagerEvent) {
    if (event.type === "output" && event.operationId === selectedId) { logText += event.text; void scrollLog(); }
    else if (event.type === "operation") void refresh(false);
  }
  function stepIcon(status: StepOutcome["status"]) {
    if (status === "succeeded") return CircleCheck;
    if (status === "failed" || status === "cancelled") return CircleX;
    if (status === "running") return LoaderCircle;
    return CircleDashed;
  }
  onMount(() => {
    applyTheme(theme); void refresh();
    const disconnect = connectSession({ onEvent: handleEvent, onConnection: (value) => { connected = value; if (value) void refresh(false); } });
    const timer = setInterval(() => void refresh(false), 15_000);
    return () => { disconnect(); clearInterval(timer); };
  });
</script>

<svelte:head><title>Nix Manager</title></svelte:head>
<svelte:window onpopstate={() => (page = initialPage())} />

<div class="app-shell">
  <aside class="sidebar">
    <button class="brand" aria-label="Nix Manager overview" onclick={() => navigate("overview")}>
      <span class="brand-mark" aria-hidden="true"><PackageCheck size={20} /></span>
      <span><strong>Nix Manager</strong><small>System control</small></span>
    </button>
    <nav aria-label="Main navigation">
      {#each nav as item (item.id)}
        {@const Icon = item.icon}
        <button class:active={page === item.id} aria-current={page === item.id ? "page" : undefined} onclick={() => navigate(item.id)}>
          <Icon size={17} /><span>{item.label}</span>
          {#if item.id === "updates" && updateCount}<Badge variant="secondary">{updateCount}</Badge>{/if}
        </button>
      {/each}
    </nav>
    <div class="sidebar-footer">
      <div class="connection" class:online={connected}><span></span>{connected ? "Connected" : "Reconnecting"}</div>
      <code title={managerState?.config.flake}>{managerState?.config.flake || "Loading checkout…"}</code>
    </div>
  </aside>

  <main class="workspace">
    <header class="topbar">
      <div class="page-title"><p>NixOS & Home Manager</p><h1>{nav.find((item) => item.id === page)?.label}</h1></div>
      <div class="configuration-picker">
        <div class="execution"><Laptop size={15} /><span>Execution</span><strong>This machine</strong></div>
        <label><span>Configuration</span><select bind:value={selectedHost} disabled={!managerState || busy} aria-label="Configuration">
          {#each Object.keys(managerState?.config.hosts || {}) as host (host)}<option value={host}>{host}</option>{/each}
        </select></label>
      </div>
    </header>

    {#if error}<div class="error-banner" role="alert"><CircleAlert size={18} /><span>{error}</span><button aria-label="Dismiss error" onclick={() => (error = "")}><X size={16} /></button></div>{/if}
    {#if managerState?.active}
      <button class="active-strip" onclick={() => { selectedId = managerState!.active!.id; navigate("activity"); }}>
        <LoaderCircle class="spin" size={18} /><span><strong>{labels[managerState.active.request.action]}</strong><small>{managerState.active.step}</small></span>
        <span class="elapsed">{duration(managerState.active)}</span><ChevronRight size={17} />
      </button>
    {/if}

    {#if loading}
      <section class="loading-state"><LoaderCircle class="spin" size={24} /><p>Connecting to Nix Manager…</p></section>
    {:else if page === "overview"}
      <section class="content overview">
        <div class="hero-row">
          <div><p class="eyebrow">Selected configuration</p><h2>{selectedHost}</h2><p>{managerState?.config.hosts[selectedHost]?.system || "Unknown platform"}</p></div>
          {#if selectedHost !== managerState?.config.hostKey}<Badge variant="outline">Different from local default</Badge>
          {:else}<Badge variant="secondary"><Check size={13} /> Local default</Badge>{/if}
        </div>
        <div class="overview-grid">
          <article class="panel action-card">
            <div class="panel-header">
              <div><p class="eyebrow">Primary workflow</p><h2>Build and apply</h2></div>
              <div class="scope-group" aria-label="Configuration scope">
                {#each [["all", "Both"], ["home", "Home"], ["system", "System"]] as item (item[0])}
                  <button class:active={scope === item[0]} onclick={() => (scope = item[0] as Request["scope"])}>{item[1]}</button>
                {/each}
              </div>
            </div>
            <div class="configuration-summary">
              <div><Home size={17} /><span>Home</span><code>{managerState?.config.hosts[selectedHost]?.homeConfiguration}</code></div>
              <div><HardDrive size={17} /><span>System</span><code>{managerState?.config.hosts[selectedHost]?.nixosConfiguration}</code></div>
            </div>
            <div class="toggle-row">
              <label><Switch aria-label="Update inputs first" bind:checked={updateInputs} disabled={busy} /><span><strong>Update inputs first</strong><small>Refresh the flake lock before building</small></span></label>
              <label><Switch aria-label="Clean up after success" bind:checked={cleanup} disabled={busy} /><span><strong>Clean up after success</strong><small>Retain three generations</small></span></label>
            </div>
            <div class="primary-actions">
              <Button variant="outline" size="lg" disabled={busy} onclick={buildReview}><Code2 /> Build for review</Button>
              <Button size="lg" disabled={busy} onclick={() => preview("switch")}><Play /> Review and switch <ArrowRight /></Button>
            </div>
            <Separator />
            <div class="maintenance-actions">
              <Button variant="ghost" disabled={busy} onclick={() => preview("boot")}><ArchiveRestore /> Apply next boot</Button>
              <Button variant="ghost" disabled={busy} onclick={() => preview("rollback")}><RefreshCw /> Roll back</Button>
              <Button variant="ghost" disabled={busy} onclick={() => preview("gc", { retention })}><Trash2 /> Clean up</Button>
            </div>
          </article>
          <aside class="right-column">
            <button class="panel update-card" onclick={() => navigate("updates") }>
              <div class="metric">{managerState?.updates ? updateCount : "—"}</div>
              <div><p class="eyebrow">Updates</p><h3>{!managerState?.updates ? "Not checked" : updateCount ? "Available" : "Up to date"}</h3><p>{managerState?.updates ? `Checked ${date(managerState.updates.checkedAt)}` : "Check inputs and package pins"}</p></div>
              <ChevronRight size={18} />
            </button>
            <article class="panel recent-card">
              <div class="panel-header"><h3>Recent activity</h3><Button variant="ghost" size="sm" onclick={() => navigate("activity")}>View all</Button></div>
              <div class="operation-list compact">
                {#each managerState?.operations.slice(0, 4) || [] as operation (operation.id)}
                  <button onclick={() => { selectOperation(operation); navigate("activity"); }}><span class={`status-dot ${operation.status}`}></span><span><strong>{labels[operation.request.action]}</strong><small>{date(operation.startedAt)} · {actionContext(operation)}</small></span><Badge variant={operation.status === "succeeded" ? "secondary" : "outline"}>{operation.status}</Badge></button>
                {:else}<div class="empty"><Activity size={22} /><p>No operations recorded yet.</p></div>{/each}
              </div>
            </article>
          </aside>
        </div>
      </section>
    {:else if page === "updates"}
      <section class="content">
        <div class="section-heading"><div><p class="eyebrow">Flake inputs and local pins</p><h2>Updates</h2><p>{managerState?.updates ? `Last checked ${date(managerState.updates.checkedAt)}` : "No cached update check"}</p></div><div><Button variant="outline" disabled={busy} onclick={runCheck}><RefreshCw /> Check now</Button><Button variant="ghost" disabled={busy} onclick={() => preview("switch", { update: true })}>Update all and switch</Button><Button disabled={busy || !selectedInputs.length} onclick={proposeInputUpdate}>Review selected inputs</Button></div></div>
        <article class="panel updates-panel">
          {#each managerState?.updates?.items || [] as item (`${item.kind}-${item.name}`)}
            <div class="update-row">{#if item.kind === "input"}<input type="checkbox" aria-label={`Select ${item.name}`} checked={selectedInputs.includes(item.name)} onchange={() => toggleInput(item.name)} />{:else}<span class="update-icon"><CloudDownload size={18} /></span>{/if}<span><strong>{item.name}</strong><small>{item.kind === "input" ? "Flake input" : "Package pin"}</small></span><code>{item.current}</code><ArrowRight size={15} /><code>{item.latest}</code></div>
          {:else}<div class="empty large"><CircleCheck size={28} /><h3>{managerState?.updates ? "Everything is current" : "Check for updates"}</h3><p>{managerState?.updates ? "No newer inputs or package pins were found." : "Compare the lock file and stable package releases without changing the checkout."}</p></div>{/each}
          {#each managerState?.updates?.errors || [] as message (message)}<div class="inline-error"><CircleAlert size={16} />{message}</div>{/each}
        </article>
        {#if managerState?.advanced.inputProposals.length}<article class="panel package-panel"><div class="panel-header"><div><h3>Reviewed lock changes</h3><p>These are the exact transitive changes resolved into a proposed lock file.</p></div></div>{#each managerState.advanced.inputProposals as proposal (proposal.id)}<div class="proposal"><span><strong>{proposal.inputs.join(", ")}</strong><small>{date(proposal.createdAt)} · {proposal.changes.length} lock nodes changed</small></span><details><summary>Review changes</summary>{#each proposal.changes as change (change.name)}<code>{change.name}: {change.before} → {change.after}</code>{/each}</details><Button variant="outline" disabled={busy || proposal.fingerprint !== managerState.advanced.repository.fingerprint} onclick={() => applyInputUpdate(proposal)}>Apply exact lock</Button></div>{/each}</article>{/if}
        <article class="panel package-panel">
          <div class="panel-header"><div><h3>Package pins</h3><p>Pin changes are applied by a later Home Manager switch.</p></div></div>
          {#each [["t3code-update", "T3 Code"], ["opencode-update", "OpenCode"], ["pince-update", "PINCE"]] as item (item[0])}
            <div class="package-row"><span class="update-icon"><PackageCheck size={18} /></span><span><strong>{item[1]}</strong><small>Validated source recipe</small></span><Button variant="outline" disabled={busy} onclick={() => preview(item[0] as Request["action"])}>Review update</Button></div>
          {/each}
        </article>
      </section>
    {:else if page === "activity"}
      <section class="content activity-layout">
        <article class="panel history-panel"><div class="panel-header"><div><p class="eyebrow">Latest 50</p><h2>Activity</h2></div></div><div class="operation-list">
          {#each managerState?.operations || [] as operation (operation.id)}
            <button class:selected={selectedId === operation.id} onclick={() => selectOperation(operation)}><span class={`status-dot ${operation.status}`}></span><span><strong>{labels[operation.request.action]}</strong><small>{date(operation.startedAt)} · {actionContext(operation)}</small></span><span class="duration">{duration(operation)}</span></button>
          {:else}<div class="empty large"><Activity size={28} /><h3>No operations yet</h3><p>Builds, update checks, and maintenance appear here.</p></div>{/each}
        </div></article>
        <article class="panel operation-detail">
          {#if selectedOperation}
            <div class="detail-header"><div><Badge variant="outline">{selectedOperation.status}</Badge><h2>{labels[selectedOperation.request.action]}</h2><p>{actionContext(selectedOperation)} · {date(selectedOperation.startedAt)}</p></div>{#if managerState?.active?.id === selectedOperation.id}<Button variant="destructive" onclick={cancelOperation}>Cancel</Button>{/if}</div>
            {#if selectedOperation.error}<div class="inline-error"><CircleAlert size={16} />{selectedOperation.error}</div>{/if}
            {#if selectedOperation.steps?.length}<ol class="timeline">{#each selectedOperation.steps as step (step.id)}{@const Icon = stepIcon(step.status)}<li class={step.status}><Icon class={step.status === "running" ? "spin" : undefined} size={17} /><span>{step.label}</span><small>{step.status}</small></li>{/each}</ol>{/if}
            <div class="log-toolbar"><label><Search size={15} /><input bind:value={logSearch} placeholder="Filter log" aria-label="Filter log" /></label><label class="mini-toggle"><Switch aria-label="Wrap log" size="sm" bind:checked={wrapLog} /> Wrap</label><label class="mini-toggle"><Switch aria-label="Follow log" size="sm" bind:checked={followLog} /> Follow</label><Button variant="ghost" size="icon-sm" aria-label="Copy log" onclick={copyLog}><Clipboard /></Button><Button variant="ghost" size="icon-sm" aria-label="Download log" onclick={downloadLog}><Download /></Button></div>
            <pre {@attach captureLog} class:wrap={wrapLog} onscroll={handleLogScroll}>{filteredLog || "No output recorded."}</pre>
          {:else}<div class="empty large"><Terminal size={30} /><h3>Select an operation</h3><p>Review its steps, outcome, and retained log.</p></div>{/if}
        </article>
      </section>
    {:else if page === "generations"}
      <section class="content generations-page">
        <div class="section-heading"><div><p class="eyebrow">Observed local state</p><h2>Generations and reviews</h2><p>Repository {managerState?.advanced.repository.clean ? "clean" : `${managerState?.advanced.repository.changes.length} changed paths`} · observed {managerState ? date(managerState.advanced.repository.observedAt) : "—"}</p></div><div class="retention"><label for="retention">Keep</label><input id="retention" type="number" min="1" max="100" bind:value={retention} /><span>generations</span><Button variant="outline" disabled={busy} onclick={() => preview("gc", { retention })}>Preview cleanup</Button></div></div>
        {#if !managerState?.advanced.repository.clean}<div class="review-warning"><CircleAlert size={17} /><span>The checkout has changed. Existing build reviews cannot be applied until it matches their recorded fingerprint.</span></div>{/if}
        <div class="generation-grid">
          {#each [["home", "Home Manager"], ["system", "NixOS system"]] as group (group[0])}
            <article class="panel generation-card"><div class="panel-header"><div><p class="eyebrow">{group[1]}</p><h3>Available generations</h3></div></div><div class="generation-list">
              {#each managerState?.advanced.generations[group[0] as "home" | "system"] || [] as generation, index (generation.number)}
                <div><span><strong>Generation {generation.number}</strong><small>{date(generation.createdAt)} · <code title={generation.output}>{generation.output.split("/").at(-1)}</code></small></span><div class="generation-actions">{#if index >= retention}<Badge variant="outline">Cleanup candidate</Badge>{/if}{#if generation.active}<Badge variant="secondary"><Check size={13} /> Active</Badge>{:else}<Button size="sm" variant="outline" disabled={busy} onclick={() => selectGeneration(group[0] as "home" | "system", generation)}>Review rollback</Button>{/if}</div></div>
              {:else}<div class="empty"><ArchiveRestore size={22} /><p>No generations were observed for this profile.</p></div>{/each}
            </div></article>
          {/each}
        </div>
        <article class="panel reviews-card"><div class="panel-header"><div><p class="eyebrow">GC-rooted outputs</p><h3>Build reviews</h3><p>Applying uses these exact store paths without reevaluating the checkout.</p></div><Button disabled={busy} onclick={buildReview}><Code2 /> Build current selection</Button></div><div class="review-list">
          {#each managerState?.advanced.reviews || [] as review (review.id)}<div><span><strong>{review.host} · {review.scope === "all" ? "Home + system" : review.scope}</strong><small>{date(review.createdAt)}{review.homeOutput ? ` · Home ${review.homeOutput.split("/").at(-1)}` : ""}{review.homeClosureSize ? ` (${bytes(review.homeClosureSize)})` : ""}{review.systemOutput ? ` · System ${review.systemOutput.split("/").at(-1)}` : ""}{review.systemClosureSize ? ` (${bytes(review.systemClosureSize)})` : ""}</small>{#if review.homeDiff || review.systemDiff}<details><summary>Package changes</summary><pre>{[review.homeDiff, review.systemDiff].filter(Boolean).join("\n")}</pre></details>{/if}</span><div class="review-actions"><Button size="sm" variant="outline" disabled={busy || review.fingerprint !== managerState?.advanced.repository.fingerprint} onclick={() => applyReview(review)}>Review apply</Button><Button size="icon-sm" variant="ghost" aria-label="Remove build review" disabled={busy} onclick={() => removeBuildReview(review.id)}><Trash2 /></Button></div></div>{:else}<div class="empty large"><Code2 size={26} /><h3>No reviewed builds</h3><p>Build the selected configuration to retain and inspect its exact output paths before activation.</p></div>{/each}
        </div></article>
      </section>
    {:else}
      <section class="content settings-page">
        <div class="section-heading"><div><p class="eyebrow">Application preferences</p><h2>Settings</h2><p>Stored locally in this browser profile.</p></div></div>
        <article class="panel settings-card"><div><Sun size={18} /><span><strong>Appearance</strong><small>Choose how the dashboard is displayed.</small></span></div><div class="theme-picker" role="group" aria-label="Appearance"><Button variant={theme === "light" ? "secondary" : "outline"} onclick={() => applyTheme("light")}><Sun /> Light</Button><Button variant={theme === "dark" ? "secondary" : "outline"} onclick={() => applyTheme("dark")}><Moon /> Dark</Button><Button variant={theme === "system" ? "secondary" : "outline"} onclick={() => applyTheme("system")}><Laptop /> System</Button></div></article>
        <article class="panel about-card"><div><PackageCheck size={20} /><span><strong>Nix Manager 1.0.0</strong><small>Bun + Effect · local authenticated dashboard</small></span></div><code>{managerState?.config.flake}</code></article>
      </section>
    {/if}
  </main>
</div>

<Dialog.Root bind:open={previewOpen}>
  <Dialog.Content class="sm:max-w-2xl">
    <Dialog.Header><Dialog.Title>{pending ? labels[pending.action] : "Review operation"}</Dialog.Title><Dialog.Description>{pending ? `${pending.host} · ${pending.scope === "all" ? "Home + system" : pending.scope}` : ""}</Dialog.Description></Dialog.Header>
    {#if previewPlan}<div class="review">
      {#if pending?.host !== managerState?.config.hostKey}<div class="review-warning"><CircleAlert size={17} /><span>You are applying the <strong>{pending?.host}</strong> configuration on this machine.</span></div>{/if}
      <ol>{#each previewPlan.steps as step, index (`${index}-${step.label}`)}<li><span>{index + 1}</span><div><strong>{step.label}</strong><details><summary>Command</summary><code>{step.argv.join(" ")}</code></details></div></li>{/each}</ol>
      {#each previewPlan.notes as note (note)}<p class="review-note"><CircleAlert size={15} />{note}</p>{/each}
    </div>{/if}
    <Dialog.Footer><Button variant="outline" onclick={() => (previewOpen = false)}>Back</Button><Button disabled={busy} onclick={runPending}>Run operation <ArrowRight /></Button></Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
