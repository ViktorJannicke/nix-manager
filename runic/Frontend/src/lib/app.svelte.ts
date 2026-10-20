import type { DashboardClient, DashboardState } from "../generated/dashboard.js";
import type { OperationsClient, OperationsState } from "../generated/operations.js";
import type { ReviewsClient, ReviewsState } from "../generated/reviews.js";
import type { AppListClient, AppListState } from "../generated/appList.js";
import type { SoftwareClient, SoftwareState } from "../generated/software.js";
import type { SystemClient, SystemState } from "../generated/system.js";
import type { BuildReview, Fix, Operation, Plan, PreferenceChange, Request } from "../generated/types.js";
import { actionLabel, m, message, text, TextError } from "./i18n";
import { changesScope, oneStepProblem } from "./changes";
import { request, sameRequest, type Action, type Scope } from "./requests";

export type Page = "overview" | "software" | "computer" | "updates" | "changes" | "generations" | "activity" | "settings";
export type Theme = "system" | "light" | "dark";
export type Language = "system" | "en" | "de";
/** Simple shows one button and a plain summary; detailed shows every prepare, review and apply step. */
export type Detail = "simple" | "detailed";
const pages: Page[] = ["overview", "software", "computer", "updates", "changes", "generations", "activity", "settings"];

/** A connected View as `useView` returns it. */
export interface ViewHandle<Client, State> {
  readonly client: Client | undefined;
  readonly state: State | undefined;
  readonly error: unknown;
}

export interface Toast {
  id: number;
  tone: "info" | "success" | "error";
  title: string;
  body?: string;
  action?: { label: string; run: () => void };
}

export interface Confirmation {
  title: string;
  description: string;
  request: Request;
  confirmLabel: string;
  destructive?: boolean;
  /** Plain-language consequences shown above the technical plan. */
  points?: string[];
  running?: boolean;
  /** The dry run failed to reach the backend. */
  failed?: string;
}

/** Edits being prepared and applied in one step, shown in the progress dialog. */
export interface OneStep {
  /** What happens, such as "Applying your changes". */
  title: string;
  /** What is said when it is done, such as "Your changes are applied". */
  done: string;
  /** The operation preparing the edits; the one applying them follows with the same review. */
  prepare: string;
  reviewId: string;
}

/** Actions that change what is active. */
const activations = new Set(["review-apply", "switch", "boot", "rollback"]);

function initialPage(): Page {
  const value = new URLSearchParams(location.search).get("view");
  return pages.includes(value as Page) ? (value as Page) : "overview";
}

/**
 * The dashboard's page state over the generated clients: the Window's `DashboardViewModel` and the Views it
 * references (operations, reviews, the computer and software). App connects them and hands them over; pages read
 * them from here.
 */
class AppState {
  dashboardView = $state.raw<ViewHandle<DashboardClient, DashboardState>>();
  operationsView = $state.raw<ViewHandle<OperationsClient, OperationsState>>();
  reviewsView = $state.raw<ViewHandle<ReviewsClient, ReviewsState>>();
  systemView = $state.raw<ViewHandle<SystemClient, SystemState>>();
  softwareView = $state.raw<ViewHandle<SoftwareClient, SoftwareState>>();
  appListView = $state.raw<ViewHandle<AppListClient, AppListState>>();
  now = $state(Date.now());
  page = $state<Page>(initialPage());
  hostOverride = $state<string | undefined>(localStorage.getItem("nix-manager-host") || undefined);
  toasts = $state<Toast[]>([]);
  confirmation = $state<Confirmation>();
  selectedReview = $state<string>();
  /** The one-step change the progress dialog shows. */
  oneStep = $state<OneStep>();
  /** A curated feature for the Software page to open, switched on, as the fix for a failure. */
  offer = $state<string>();
  /** True while the pointer or focus is on a toast, so none closes while it is being read. */
  toastsHeld = false;

  private nextId = 1;
  /** Operations started from this window; their completion gets a follow-up. */
  private watched = new Set<string>();
  private settled = new Set<string>();
  /** Reviews prepared here in one step, whose application is followed too. */
  private followed = new Set<string>();

  dashboard = $derived(this.dashboardView?.state);
  operations = $derived(this.operationsView?.state);
  connected = $derived(Boolean(this.dashboardView?.client && this.operationsView?.client));
  config = $derived(this.dashboard?.configuration);
  detail = $derived<Detail>(this.dashboard?.detail === "detailed" ? "detailed" : "simple");
  active = $derived(this.operations?.active ?? undefined);
  busy = $derived(Boolean(this.operations?.busy));
  /** The history with the running operation in it; the backend adds it to the history when it ends. */
  history = $derived.by(() => {
    const list = this.operations?.operations ?? [];
    const active = this.active;
    if (!active) return list;
    return list.some((operation) => operation.id === active.id)
      ? list.map((operation) => (operation.id === active.id ? active : operation))
      : [active, ...list];
  });
  updates = $derived(this.operations?.updates ?? undefined);
  inputUpdates = $derived(this.updates?.items.filter((item) => item.kind === "input") ?? []);
  packageUpdates = $derived(this.updates?.items.filter((item) => item.kind === "package") ?? []);
  reviews = $derived(this.reviewsView?.state?.reviews ?? []);
  /** The last observation of the computer: restore points, the checkout, restart and disk space. */
  system = $derived(this.systemView?.state?.system ?? undefined);
  /** The pending edits, rendered against the observed checkout. */
  draft = $derived(this.dashboard?.draft ?? undefined);
  pendingChanges = $derived(this.draft?.changes ?? []);
  host = $derived(
    this.hostOverride && this.config?.hosts.some((host) => host.name === this.hostOverride)
      ? this.hostOverride
      : (this.config?.hostKey ?? ""),
  );
  foreignHost = $derived(Boolean(this.config && this.host !== this.config.hostKey));
  /** The dry run of the open confirmation, once the backend answered it. */
  preview = $derived.by(() => {
    const preview = this.dashboard?.preview;
    return preview && this.confirmation && sameRequest(preview.request, this.confirmation.request) ? preview : undefined;
  });
  /**
   * The latest change that activated something, while it can be undone: it recorded what it replaced and those
   * generations still exist.
   */
  lastChange = $derived.by(() => {
    const last = this.history.find(
      (operation) => operation.status === "succeeded" && activations.has(operation.request.action) && !operation.request.dryRun,
    );
    const undo = last?.undo;
    if (!last || !undo || last.request.activation === "boot") return undefined;
    const kept = (scope: "home" | "system") => {
      const number = undo.generations[scope];
      return number === null || Boolean(this.system?.generations[scope].some((generation) => generation.number === number));
    };
    return kept("home") && kept("system") ? { operation: last, undo } : undefined;
  });

  /** Starts the clock and follows the browser history; returns the cleanup. */
  start() {
    const clock = setInterval(() => (this.now = Date.now()), 1_000);
    const onPop = () => (this.page = initialPage());
    addEventListener("popstate", onPop);
    return () => {
      clearInterval(clock);
      removeEventListener("popstate", onPop);
    };
  }

  navigate(page: Page) {
    if (page === this.page) return;
    this.page = page;
    history.pushState(null, "", page === "overview" ? location.pathname : `?view=${page}`);
    document.getElementById("main")?.scrollTo({ top: 0 });
  }

  // --- Preferences ---------------------------------------------------------

  setTheme(theme: Theme) {
    void this.savePreferences({ theme });
  }

  setLanguage(language: Language) {
    void this.savePreferences({ language });
  }

  setDetail(detail: Detail) {
    void this.savePreferences({ detail });
  }

  /** Saves the preferences in `change`, leaving the rest as they are; the Window's state carries them back. */
  async savePreferences(change: PreferenceChange) {
    try {
      await this.dashboardView?.client?.savePreferences(change);
    } catch (error) {
      this.fail(error, m.settings_save_failed());
    }
  }

  setHost(host: string | undefined) {
    this.hostOverride = host && host !== this.config?.hostKey ? host : undefined;
    if (this.hostOverride) localStorage.setItem("nix-manager-host", this.hostOverride);
    else localStorage.removeItem("nix-manager-host");
  }

  // --- Toasts --------------------------------------------------------------

  /** Toasts close on their own unless they offer an action, which stays until it is used or dismissed. */
  toast(toast: Omit<Toast, "id">, timeout = toast.tone === "error" ? 12_000 : 6_000) {
    const id = this.nextId++;
    this.toasts = [...this.toasts, { ...toast, id }].slice(-4);
    const title = toast.tone === "error" ? `${m.toast_error()} ${toast.title}` : toast.title;
    this.announce(toast.body ? `${title}. ${toast.body}` : title);
    if (toast.action) return;
    const close = () => (this.toastsHeld ? setTimeout(close, 2_000) : this.dismiss(id));
    setTimeout(close, timeout);
  }
  /**
   * Has the screen reader speak a status that a live region shows: WebKitGTK reports no live region to Orca, so the
   * Window announces it (DashboardViewModel.Announce). In a browser the live region speaks, and this does nothing.
   */
  announce(message: string) {
    if (message) void this.dashboardView?.client?.announce({ message }).catch(() => {});
  }
  dismiss(id: number) {
    this.toasts = this.toasts.filter((toast) => toast.id !== id);
  }
  fail(error: unknown, title = m.common_something_went_wrong()) {
    this.toast({ tone: "error", title, body: message(error) });
  }

  // --- Operations ----------------------------------------------------------

  /** The operation with `id`: the running one with its live progress, or its record. */
  operation(id: string | null | undefined) {
    return this.history.find((operation) => operation.id === id);
  }

  selectOperation(id: string) {
    if (this.operations?.selectedOperationId === id) return;
    void this.operationsView?.client?.setSelectedOperationId(id).catch((error) => this.fail(error, m.log_load_failed()));
  }

  request(action: Action, overrides: Partial<Request> = {}): Request {
    return request({ action, host: this.host, ...overrides });
  }

  /** Starts an operation; the backend runs it beyond this Window. */
  async run(next: Request) {
    const client = this.operationsView?.client;
    if (!client) return undefined;
    try {
      const state = await client.start(next);
      if (state.startError) throw new TextError(state.startError);
      const operation = state.active ?? undefined;
      if (operation) {
        this.watched.add(operation.id);
        this.selectOperation(operation.id);
        // It may have ended before the start returned.
        this.settle(this.history);
      }
      return operation;
    } catch (error) {
      this.fail(error, m.toast_could_not_start({ action: actionLabel(next.action) }));
      return undefined;
    }
  }

  /** Opens the confirmation dialog and asks the backend for the dry run of its request. */
  async confirm(confirmation: Omit<Confirmation, "running" | "failed">) {
    this.confirmation = confirmation;
    try {
      await this.dashboardView?.client?.previewPlan($state.snapshot(confirmation.request) as Request);
    } catch (error) {
      if (this.confirmation?.request === confirmation.request) this.confirmation.failed = message(error);
    }
  }

  async accept() {
    const confirmation = this.confirmation;
    if (!confirmation || confirmation.running) return;
    confirmation.running = true;
    await this.run($state.snapshot(confirmation.request) as Request);
    if (this.confirmation === confirmation) this.confirmation = undefined;
  }

  /** Prepares a review of `scope`: the checkout as it is, or what `overrides` ask for, such as input updates. */
  prepare(scope: Scope, overrides: Partial<Request> = {}) {
    return this.run(this.request("review-build", { scope, ...overrides }));
  }

  /** Prepares the pending edits and applies them right away if they are safe to, showing progress. */
  async installNow(title: string, done: string) {
    const operation = await this.prepare("home", { edits: true, then: "apply" });
    const reviewId = operation?.request.reviewId;
    if (!operation || !reviewId) return;
    this.followed.add(reviewId);
    this.oneStep = { title, done, prepare: operation.id, reviewId };
  }

  /** Whether the pending edits, all of them, can be applied in one step. */
  get pendingInOneStep() {
    return this.pendingChanges.length > 0 && !this.draft?.problems.length && !oneStepProblem(this.pendingChanges) && !this.foreignHost;
  }

  /** Prepares the pending edits for review. */
  preparePending() {
    return this.prepare(changesScope(this.pendingChanges), { edits: true });
  }

  /** The one button for the pending edits in simple mode: applies them in one step where it can, otherwise prepares them for review. */
  applyPending() {
    if (!this.pendingInOneStep) return this.preparePending();
    return this.installNow(m.onestep_applying(), m.onestep_applied());
  }

  /** Keeps the one-step change from being applied; it waits on the Changes page. */
  async hold(operationId: string) {
    try {
      const state = await this.reviewsView?.client?.hold(operationId);
      if (state?.error) throw new TextError(state.error);
    } catch (error) {
      this.fail(error, m.changes_could_not_hold());
    }
  }

  /** Prepares going back from an applied change, for review. */
  undo(operation: Operation) {
    return this.prepare(operation.request.scope as Scope, { host: operation.request.host, undo: operation.id });
  }

  async discard(review: BuildReview) {
    try {
      const state = await this.reviewsView?.client?.remove(review.id);
      if (state?.error) throw new TextError(state.error);
      if (this.selectedReview === review.id) this.selectedReview = undefined;
      this.toast({ tone: "info", title: m.changes_discarded() });
    } catch (error) {
      this.fail(error, m.changes_could_not_discard());
    }
  }

  /** Opens the feature that allows what a failure refused, for the configuration that refused it. */
  applyFix(fix: Fix, requested?: string) {
    const scope = fix.scope ?? (requested === "home" ? "home" : "system");
    this.offer = scope === "system" ? "non-free" : "non-free-home";
    this.navigate("software");
  }

  async cancel() {
    try {
      await this.operationsView?.client?.cancel();
    } catch (error) {
      this.fail(error, m.toast_could_not_cancel());
    }
  }

  async refresh() {
    try {
      await this.operationsView?.client?.refresh();
    } catch (error) {
      this.fail(error, m.toast_could_not_refresh());
    }
  }

  /** Follow-up for operations started from this Window once they end; App calls it when the history changes. */
  settle(operations: readonly Operation[]) {
    for (const operation of operations) {
      const r = operation.request;
      // Applying edits prepared in one step starts in the backend; it is followed like its preparation.
      if (r.action === "review-apply" && r.reviewId && this.followed.has(r.reviewId)) this.watched.add(operation.id);
      if (operation.status === "running" || !this.watched.has(operation.id) || this.settled.has(operation.id)) continue;
      this.settled.add(operation.id);
      this.follow(operation);
    }
  }

  private follow(operation: Operation) {
    const r = operation.request;
    const label = actionLabel(r.action);
    // The progress dialog tells what happened to a one-step change while it is open.
    const shown = Boolean(this.oneStep && (this.oneStep.prepare === operation.id || this.oneStep.reviewId === r.reviewId));
    if (operation.status === "cancelled") {
      if (!shown) this.toast({ tone: "info", title: m.toast_cancelled({ label }), body: text(operation.error) || undefined });
      return;
    }
    if (operation.status !== "succeeded") {
      if (shown) return;
      const fix = operation.explanation?.fix;
      this.toast({
        tone: "error",
        title: operation.explanation
          ? m.toast_failed_because({ label, reason: text(operation.explanation.title) })
          : m.toast_failed({ label }),
        body: text(operation.explanation?.body ?? operation.error) || undefined,
        action: fix
          ? { label: m.toast_allow_non_free(), run: () => this.applyFix(fix, r.scope) }
          : { label: m.toast_view_log(), run: () => this.show(operation.id) },
      });
      return;
    }
    if (r.action === "review-build" && r.then === "apply" && !operation.held) return;
    if (r.action === "review-build") {
      this.selectedReview = r.reviewId ?? undefined;
      if (shown) return;
      this.navigate("changes");
      this.toast({ tone: "success", title: m.toast_ready_to_review(), body: m.toast_nothing_changed_yet() });
    } else if (r.action === "review-apply") {
      if (shown) return;
      const boot = r.activation === "boot";
      this.toast({
        tone: "success",
        title: boot ? m.toast_will_apply_on_next_boot() : m.toast_changes_applied(),
        body: boot ? m.toast_active_after_restart() : undefined,
        action: operation.undo && !boot ? { label: m.common_undo(), run: () => void this.undo(operation) } : undefined,
      });
      this.navigate("overview");
    } else if (r.action === "check") {
      const count = this.updates?.items.length ?? 0;
      this.toast({ tone: "success", title: count ? m.updates_available({ count }) : m.updates_up_to_date() });
    } else if (r.action.endsWith("-update")) {
      this.toast({
        tone: "success",
        title: m.toast_pin_saved({ label }),
        body: m.toast_pin_saved_body(),
        action: { label: m.common_prepare(), run: () => void this.prepare("home") },
      });
    } else this.toast({ tone: "success", title: m.toast_finished({ label }) });
  }

  /** Shows an operation's steps and output on the Activity page. */
  show(id: string) {
    this.selectOperation(id);
    this.navigate("activity");
  }
}

export const app = new AppState();

export type { Plan };
