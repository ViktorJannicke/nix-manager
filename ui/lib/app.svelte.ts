import type {
  BuildProgress,
  BuildReview,
  ManagerEvent,
  Operation,
  Plan,
  SessionMessage,
  Snapshot,
  StepOutcome,
} from "../../src/contracts";
import { actionLabels, request, type Request, type Scope } from "../../src/domain";
import type { Fix } from "../../src/explain";
import type { Preferences } from "../../src/preferences";
import { connectSession, managerApi } from "./api";
import { plural } from "./format";

export type Page = "overview" | "software" | "computer" | "updates" | "changes" | "generations" | "activity" | "settings";
export type Theme = "system" | "light" | "dark";
/** Simple shows one button and a plain summary; detailed shows every prepare, review and apply step. */
export type Detail = "simple" | "detailed";
const pages: Page[] = ["overview", "software", "computer", "updates", "changes", "generations", "activity", "settings"];

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
  plan?: Plan;
  error?: string;
  running?: boolean;
  token: number;
}

/** Edits being prepared and applied in one step, shown in the progress dialog. */
export interface OneStep {
  /** What happens, such as "Installing Firefox". */
  title: string;
  /** What is said when it is done, such as "Firefox is installed". */
  done: string;
  /** The operation preparing the edits; the one applying them follows with the same review. */
  prepare: string;
  reviewId: string;
}

interface LogView {
  id: string;
  text: string;
  /** Byte offset in the stored log that `text` ends at. */
  end: number;
  truncated: boolean;
  loading: boolean;
}

const LOG_CHARACTERS = 400_000;
const encoder = new TextEncoder();
const message = (error: unknown) => (error instanceof Error ? error.message : String(error));
/** Actions that change what is active. */
const activations = new Set<Request["action"]>(["review-apply", "switch", "boot", "rollback"]);

function initialPage(): Page {
  const value = new URLSearchParams(location.search).get("view");
  return pages.includes(value as Page) ? (value as Page) : "overview";
}

class AppState {
  snapshot = $state.raw<Snapshot>();
  connected = $state(false);
  now = $state(Date.now());
  page = $state<Page>(initialPage());
  selectedOperation = $state<string>();
  selectedReview = $state<string>();
  hostOverride = $state<string | undefined>(localStorage.getItem("nix-manager-host") || undefined);
  // Preferences come with each snapshot; browser storage only keeps the first paint from flickering.
  theme = $state<Theme>((localStorage.getItem("nix-manager-theme") as Theme | null) || "system");
  detail = $state<Detail>((localStorage.getItem("nix-manager-detail") as Detail | null) || "simple");
  toasts = $state<Toast[]>([]);
  confirmation = $state<Confirmation>();
  /** A curated feature for the Software page to open, switched on, as the fix for a failure. */
  offer = $state<string>();
  log = $state<LogView>({ id: "", text: "", end: 0, truncated: false, loading: false });
  /** Live progress for the running step, keyed by operation ID. */
  progress = $state.raw<Record<string, { stepId: string; progress: BuildProgress }>>({});
  refreshing = $state(false);
  /** The one-step change the progress dialog shows. */
  oneStep = $state<OneStep>();

  private nextId = 1;
  private queued: Extract<ManagerEvent, { type: "output" }>[] = [];
  /** Operations started from this window; their completion gets a follow-up. */
  private watched = new Set<string>();
  private settled = new Set<string>();
  /** Reviews prepared here in one step, whose application is followed too. */
  private followed = new Set<string>();

  // Derived views of the snapshot used across pages.
  config = $derived(this.snapshot?.config);
  system = $derived(this.snapshot?.system);
  active = $derived(this.snapshot?.active);
  busy = $derived(Boolean(this.snapshot?.active));
  reviews = $derived(this.snapshot?.reviews ?? []);
  updates = $derived(this.snapshot?.updates);
  host = $derived(
    this.hostOverride && this.snapshot?.config.hosts[this.hostOverride]
      ? this.hostOverride
      : (this.snapshot?.config.hostKey ?? ""),
  );
  foreignHost = $derived(Boolean(this.snapshot && this.host !== this.snapshot.config.hostKey));
  inputUpdates = $derived(this.updates?.items.filter((item) => item.kind === "input") ?? []);
  packageUpdates = $derived(this.updates?.items.filter((item) => item.kind === "package") ?? []);
  /**
   * The latest change that activated something, while it can be undone: it
   * recorded what it replaced and those generations still exist.
   */
  lastChange = $derived.by(() => {
    const last = this.snapshot?.operations.find(
      (operation) => operation.status === "succeeded" && activations.has(operation.request.action) && !operation.request.dryRun,
    );
    if (!last?.undo || last.request.activation === "boot") return undefined;
    const kept = (scope: "home" | "system") => {
      const number = last.undo!.generations[scope];
      return number === undefined || Boolean(this.system?.generations[scope].some((generation) => generation.number === number));
    };
    return kept("home") && kept("system") ? (last as Operation & { undo: NonNullable<Operation["undo"]> }) : undefined;
  });

  start() {
    this.applyTheme(this.theme);
    const media = matchMedia("(prefers-color-scheme: dark)");
    const onScheme = () => this.applyTheme(this.theme);
    media.addEventListener("change", onScheme);
    const disconnect = connectSession({
      onMessage: (message) => this.handle(message),
      onConnection: (connected) => {
        this.connected = connected;
        // Reconnects receive a snapshot; reload the log to cover the gap.
        if (connected && this.log.id) void this.loadLog(this.log.id);
      },
    });
    const clock = setInterval(() => (this.now = Date.now()), 1_000);
    const onPop = () => (this.page = initialPage());
    addEventListener("popstate", onPop);
    return () => {
      disconnect();
      clearInterval(clock);
      media.removeEventListener("change", onScheme);
      removeEventListener("popstate", onPop);
    };
  }

  /** Prepares going back from an applied change, for review. */
  undo(operation: Operation) {
    return this.prepare(operation.request.scope, { host: operation.request.host, undo: operation.id });
  }

  /** Opens the feature that allows what a failure refused, for the configuration that refused it. */
  applyFix(fix: Fix, requested?: Scope) {
    const scope = fix.scope ?? (requested === "home" ? "home" : "system");
    this.offer = scope === "system" ? "non-free" : "non-free-home";
    this.navigate("software");
  }

  navigate(page: Page) {
    if (page === this.page) return;
    this.page = page;
    history.pushState(null, "", page === "overview" ? location.pathname : `?view=${page}`);
    document.getElementById("main")?.scrollTo({ top: 0 });
  }

  applyTheme(theme: Theme) {
    this.theme = theme;
    localStorage.setItem("nix-manager-theme", theme);
    const dark =
      theme === "dark" || (theme === "system" && matchMedia("(prefers-color-scheme: dark)").matches);
    document.documentElement.classList.toggle("dark", dark);
  }

  setTheme(theme: Theme) {
    this.applyTheme(theme);
    void this.savePreferences({ theme });
  }

  setDetail(detail: Detail) {
    this.detail = detail;
    localStorage.setItem("nix-manager-detail", detail);
    void this.savePreferences({ detail });
  }

  /** Saves preferences that only the server uses; the next snapshot carries them. */
  async savePreferences(preferences: Preferences) {
    try {
      await managerApi.preferences(preferences);
    } catch (error) {
      this.fail(error, "Could not save the setting");
    }
  }

  setHost(host: string | undefined) {
    this.hostOverride = host && host !== this.snapshot?.config.hostKey ? host : undefined;
    if (this.hostOverride) localStorage.setItem("nix-manager-host", this.hostOverride);
    else localStorage.removeItem("nix-manager-host");
  }

  toast(toast: Omit<Toast, "id">, timeout = toast.tone === "error" ? 12_000 : 6_000) {
    const id = this.nextId++;
    this.toasts = [...this.toasts, { ...toast, id }].slice(-4);
    setTimeout(() => this.dismiss(id), timeout);
  }
  dismiss(id: number) {
    this.toasts = this.toasts.filter((toast) => toast.id !== id);
  }
  fail(error: unknown, title = "Something went wrong") {
    this.toast({ tone: "error", title, body: message(error) });
  }

  // --- Session state -------------------------------------------------------

  private handle(event: SessionMessage) {
    if (event.type === "snapshot") {
      const previous = this.snapshot;
      this.snapshot = event.snapshot;
      const { theme, detail } = event.snapshot.preferences;
      if (theme && theme !== this.theme) this.applyTheme(theme);
      if (detail && detail !== this.detail) {
        this.detail = detail;
        localStorage.setItem("nix-manager-detail", detail);
      }
      if (!this.selectedOperation && event.snapshot.active)
        this.selectedOperation = event.snapshot.active.id;
      for (const operation of event.snapshot.operations) this.settle(operation);
      if (!previous && event.snapshot.active) void this.loadLog(event.snapshot.active.id);
      return;
    }
    if (event.type === "operation") {
      // Applying edits prepared in one step starts on the server; it is followed like its preparation.
      const r = event.operation.request;
      if (r.action === "review-apply" && this.followed.has(r.reviewId!)) this.watched.add(event.operation.id);
      this.patch(event.operation);
      this.settle(event.operation);
      return;
    }
    if (event.type === "progress") {
      this.progress = {
        ...this.progress,
        [event.operationId]: { stepId: event.stepId, progress: event.progress },
      };
      return;
    }
    if (event.operationId !== this.log.id) return;
    if (this.log.loading) this.queued.push(event);
    else this.append(event);
  }

  /** Applies an operation event before the next snapshot arrives. */
  private patch(operation: Operation) {
    const snapshot = this.snapshot;
    if (!snapshot) return;
    const running = operation.status === "running";
    const operations = snapshot.operations.some((item) => item.id === operation.id)
      ? snapshot.operations.map((item) => (item.id === operation.id ? operation : item))
      : [operation, ...snapshot.operations];
    this.snapshot = {
      ...snapshot,
      active: running ? operation : snapshot.active?.id === operation.id ? undefined : snapshot.active,
      operations,
    };
  }

  stepProgress(operation: Operation, step: StepOutcome): BuildProgress | undefined {
    const live = this.progress[operation.id];
    return step.status === "running" && live?.stepId === step.id ? live.progress : step.progress;
  }

  // --- Logs ----------------------------------------------------------------

  selectOperation(id: string) {
    this.selectedOperation = id;
    if (this.log.id !== id) void this.loadLog(id);
  }

  async loadLog(id: string) {
    this.queued = [];
    this.log = { id, text: this.log.id === id ? this.log.text : "", end: 0, truncated: false, loading: true };
    try {
      const result = await managerApi.log(id);
      if (this.log.id !== id) return;
      this.log = { id, text: result.text, end: result.end, truncated: result.truncated, loading: false };
      const queued = this.queued;
      this.queued = [];
      for (const event of queued) this.append(event);
    } catch (error) {
      if (this.log.id === id) this.log = { ...this.log, loading: false };
      this.fail(error, "Could not load the log");
    }
  }

  private append(event: Extract<ManagerEvent, { type: "output" }>) {
    // The stored log is append-only, so offsets tell exactly which events the
    // fetched tail already contains and whether any were missed.
    if (event.offset < this.log.end) return;
    if (event.offset > this.log.end) {
      void this.loadLog(this.log.id);
      return;
    }
    let text = this.log.text + event.text;
    let truncated = this.log.truncated;
    if (text.length > LOG_CHARACTERS) {
      text = text.slice(text.indexOf("\n", text.length - LOG_CHARACTERS) + 1);
      truncated = true;
    }
    this.log = {
      ...this.log,
      text,
      truncated,
      end: this.log.end + encoder.encode(event.text).length,
    };
  }

  // --- Actions -------------------------------------------------------------

  request(action: Request["action"], overrides: Partial<Request> = {}): Request {
    return request({ action, host: this.host, ...overrides });
  }

  async run(next: Request) {
    try {
      const operation = await managerApi.run(next);
      this.watched.add(operation.id);
      this.patch(operation);
      this.selectedOperation = operation.id;
      void this.loadLog(operation.id);
      return operation;
    } catch (error) {
      this.fail(error, `Could not start: ${actionLabels[next.action]}`);
      return undefined;
    }
  }

  /** Opens the confirmation dialog and loads the technical plan. */
  async confirm(confirmation: Omit<Confirmation, "plan" | "error" | "running" | "token">) {
    // State proxies are not identical to their source, so a token tells
    // whether the dialog still shows this request when the plan arrives.
    const token = this.nextId++;
    this.confirmation = { ...confirmation, token };
    try {
      const plan = await managerApi.plan(confirmation.request);
      if (this.confirmation?.token === token) this.confirmation.plan = plan;
    } catch (error) {
      if (this.confirmation?.token === token) this.confirmation.error = message(error);
    }
  }

  async accept() {
    const confirmation = this.confirmation;
    if (!confirmation || confirmation.running) return;
    confirmation.running = true;
    const operation = await this.run($state.snapshot(confirmation.request) as Request);
    if (this.confirmation === confirmation) this.confirmation = undefined;
    return operation;
  }

  prepare(scope: Request["scope"], overrides: Partial<Request> = {}) {
    return this.run(this.request("review-build", { scope, ...overrides }));
  }

  /** Prepares the pending edits and applies them right away if they are safe to, showing progress. */
  async installNow(title: string, done: string) {
    const operation = await this.prepare("home", { edits: true, then: "apply" });
    if (!operation) return;
    this.followed.add(operation.request.reviewId!);
    this.oneStep = { title, done, prepare: operation.id, reviewId: operation.request.reviewId! };
  }

  /** Keeps the one-step change from being applied; it waits on the Changes page. */
  async hold(operationId: string) {
    try {
      await managerApi.hold(operationId);
    } catch (error) {
      this.fail(error, "Could not stop it");
    }
  }

  async cancel() {
    if (!this.active) return;
    try {
      await managerApi.cancel(this.active.id);
    } catch (error) {
      this.fail(error, "Could not cancel");
    }
  }

  async discard(review: BuildReview) {
    try {
      await managerApi.removeReview(review.id);
      if (this.selectedReview === review.id) this.selectedReview = undefined;
      this.toast({ tone: "info", title: "Prepared changes discarded" });
    } catch (error) {
      this.fail(error, "Could not discard the changes");
    }
  }

  async refresh() {
    this.refreshing = true;
    try {
      this.snapshot = await managerApi.refresh();
    } catch (error) {
      this.fail(error, "Could not refresh");
    } finally {
      this.refreshing = false;
    }
  }

  /** Follow-up guidance when an operation started here completes. */
  private settle(operation: Operation) {
    if (operation.status === "running" || !this.watched.has(operation.id)) return;
    if (this.settled.has(operation.id)) return;
    this.settled.add(operation.id);
    const r = operation.request;
    const label = actionLabels[r.action];
    // The progress dialog tells what happened to a one-step change while it is open.
    const shown = this.oneStep && (this.oneStep.prepare === operation.id || this.oneStep.reviewId === r.reviewId);
    if (operation.status === "cancelled") {
      if (shown) return;
      this.toast({ tone: "info", title: `${label} cancelled`, body: operation.error });
      return;
    }
    if (operation.status !== "succeeded") {
      if (shown) return;
      this.toast({
        tone: "error",
        title: operation.explanation ? `${label} failed: ${operation.explanation.title}` : `${label} failed`,
        body: operation.explanation?.body ?? operation.error,
        action: operation.explanation?.fix
          ? { label: "Allow non-free software", run: () => this.applyFix(operation.explanation!.fix!, r.scope) }
          : {
              label: "View log",
              run: () => {
                this.selectOperation(operation.id);
                this.navigate("activity");
              },
            },
      });
      return;
    }
    if (r.action === "review-build" && r.then === "apply" && !operation.held) return;
    if (r.action === "review-build") {
      this.selectedReview = r.reviewId;
      if (shown) return;
      this.navigate("changes");
      this.toast({ tone: "success", title: "Changes are ready to review", body: "Nothing on the computer has changed yet." });
    } else if (r.action === "review-apply") {
      if (shown) return;
      this.toast({
        tone: "success",
        title: r.activation === "boot" ? "Will apply on next boot" : "Changes applied",
        body: r.activation === "boot" ? "The new system becomes active after a restart." : undefined,
        action: operation.undo && r.activation !== "boot" ? { label: "Undo", run: () => void this.undo(operation) } : undefined,
      });
      this.navigate("overview");
    } else if (r.action === "check") {
      const count = this.snapshot?.updates?.items.length ?? 0;
      this.toast({
        tone: "success",
        title: count ? `${plural(count, "update")} available` : "Everything is up to date",
      });
    } else if (r.action.endsWith("-update")) {
      this.toast({
        tone: "success",
        title: `${label}: pin saved`,
        body: "Prepare your user's changes to build and apply the new version.",
        action: { label: "Prepare", run: () => void this.prepare("home") },
      });
    } else this.toast({ tone: "success", title: `${label} finished` });
  }
}

export const app = new AppState();
