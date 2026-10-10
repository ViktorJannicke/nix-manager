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
import { actionLabels, request, type Request } from "../../src/domain";
import { connectSession, managerApi } from "./api";
import { plural } from "./format";

export type Page = "overview" | "software" | "updates" | "changes" | "generations" | "activity" | "settings";
export type Theme = "system" | "light" | "dark";
const pages: Page[] = ["overview", "software", "updates", "changes", "generations", "activity", "settings"];

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
  theme = $state<Theme>((localStorage.getItem("nix-manager-theme") as Theme | null) || "system");
  toasts = $state<Toast[]>([]);
  confirmation = $state<Confirmation>();
  log = $state<LogView>({ id: "", text: "", end: 0, truncated: false, loading: false });
  /** Live progress for the running step, keyed by operation ID. */
  progress = $state.raw<Record<string, { stepId: string; progress: BuildProgress }>>({});
  refreshing = $state(false);

  private nextId = 1;
  private queued: Extract<ManagerEvent, { type: "output" }>[] = [];
  /** Operations started from this window; their completion gets a follow-up. */
  private watched = new Set<string>();
  private settled = new Set<string>();

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
      if (!this.selectedOperation && event.snapshot.active)
        this.selectedOperation = event.snapshot.active.id;
      for (const operation of event.snapshot.operations) this.settle(operation);
      if (!previous && event.snapshot.active) void this.loadLog(event.snapshot.active.id);
      return;
    }
    if (event.type === "operation") {
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
    if (operation.status === "cancelled") {
      this.toast({ tone: "info", title: `${label} cancelled`, body: operation.error });
      return;
    }
    if (operation.status !== "succeeded") {
      this.toast({
        tone: "error",
        title: operation.explanation ? `${label} failed: ${operation.explanation.title}` : `${label} failed`,
        body: operation.explanation?.body ?? operation.error,
        action: {
          label: "View log",
          run: () => {
            this.selectOperation(operation.id);
            this.navigate("activity");
          },
        },
      });
      return;
    }
    if (r.action === "review-build") {
      this.selectedReview = r.reviewId;
      this.navigate("changes");
      this.toast({ tone: "success", title: "Changes are ready to review", body: "Nothing has been activated yet." });
    } else if (r.action === "review-apply") {
      this.toast({
        tone: "success",
        title: r.activation === "boot" ? "Will apply on next boot" : "Changes applied",
        body: r.activation === "boot" ? "The new system becomes active after a restart." : undefined,
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
        body: "Prepare home changes to build and apply the new version.",
        action: { label: "Prepare", run: () => void this.prepare("home") },
      });
    } else this.toast({ tone: "success", title: `${label} finished` });
  }
}

export const app = new AppState();
