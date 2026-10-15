import type { DashboardClient, DashboardState } from "../generated/dashboard.js";
import type { OperationsClient, OperationsState } from "../generated/operations.js";
import type { Operation, Plan, Request } from "../generated/types.js";
import { actionLabel, m, stored } from "./i18n";
import { request, sameRequest, type Action } from "./requests";

/** The pages of this version; Software, Computer, Changes and the setup assistant follow with their backend. */
export type Page = "overview" | "updates" | "generations" | "activity" | "settings";
export type Theme = "system" | "light" | "dark";
const pages: Page[] = ["overview", "updates", "generations", "activity", "settings"];

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

const message = (error: unknown) => (error instanceof Error ? error.message : String(error));

function initialPage(): Page {
  const value = new URLSearchParams(location.search).get("view");
  return pages.includes(value as Page) ? (value as Page) : "overview";
}

/**
 * The dashboard's page state over the two generated clients: the Window's `DashboardViewModel` and its
 * `OperationsViewModel`. App connects both and hands them over; pages read them from here.
 */
class AppState {
  dashboardView = $state.raw<ViewHandle<DashboardClient, DashboardState>>();
  operationsView = $state.raw<ViewHandle<OperationsClient, OperationsState>>();
  now = $state(Date.now());
  page = $state<Page>(initialPage());
  hostOverride = $state<string | undefined>(localStorage.getItem("nix-manager-host") || undefined);
  toasts = $state<Toast[]>([]);
  confirmation = $state<Confirmation>();
  /** True while the pointer or focus is on a toast, so none closes while it is being read. */
  toastsHeld = false;

  private nextId = 1;
  /** Operations started from this window; their completion gets a follow-up. */
  private watched = new Set<string>();
  private settled = new Set<string>();

  dashboard = $derived(this.dashboardView?.state);
  operations = $derived(this.operationsView?.state);
  connected = $derived(Boolean(this.dashboardView?.client && this.operationsView?.client));
  config = $derived(this.dashboard?.configuration);
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
    void this.savePreferences({ theme, language: null });
  }

  setLanguage(language: "system" | "en" | "de") {
    void this.savePreferences({ theme: null, language });
  }

  /** The Bridge sends every member of the change, so the preferences not changed here are null. */
  private async savePreferences(change: { theme: string | null; language: string | null }) {
    try {
      await this.dashboardView?.client?.savePreferences({
        ...change,
        detail: null,
        prepareUpdates: null,
        cleanUp: null,
        history: null,
        backup: null,
        appDetails: null,
      });
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
    if (toast.action) return;
    const close = () => (this.toastsHeld ? setTimeout(close, 2_000) : this.dismiss(id));
    setTimeout(close, timeout);
  }
  dismiss(id: number) {
    this.toasts = this.toasts.filter((toast) => toast.id !== id);
  }
  fail(error: unknown, title = m.common_something_went_wrong()) {
    this.toast({ tone: "error", title, body: stored(message(error)) });
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
      if (state.startError) throw new Error(state.startError);
      const operation = state.active ?? undefined;
      if (operation) {
        this.watched.add(operation.id);
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
      if (operation.status === "running" || !this.watched.has(operation.id) || this.settled.has(operation.id)) continue;
      this.settled.add(operation.id);
      const label = actionLabel(operation.request.action);
      const view = { label: m.toast_view_log(), run: () => this.show(operation.id) };
      if (operation.status === "cancelled")
        this.toast({ tone: "info", title: m.toast_cancelled({ label }), body: stored(operation.error) || undefined });
      else if (operation.status !== "succeeded")
        this.toast({
          tone: "error",
          title: operation.explanation
            ? m.toast_failed_because({ label, reason: stored(operation.explanation.title) })
            : m.toast_failed({ label }),
          body: stored(operation.explanation?.body ?? operation.error) || undefined,
          action: view,
        });
      else if (operation.request.action === "check") {
        const count = this.updates?.items.length ?? 0;
        this.toast({ tone: "success", title: count ? m.updates_available({ count }) : m.updates_up_to_date() });
      } else this.toast({ tone: "success", title: m.toast_finished({ label }) });
    }
  }

  /** Shows an operation's steps and output on the Activity page. */
  show(id: string) {
    this.selectOperation(id);
    this.navigate("activity");
  }
}

export const app = new AppState();

export type { Plan };
