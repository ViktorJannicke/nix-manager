import type {
  LogResult,
  Operation,
  Plan,
  SessionMessage,
  Snapshot,
} from "../../src/contracts";
import type { Request } from "../../src/domain";
import type { Preferences } from "../../src/preferences";
import type { CleanupEstimate } from "../../src/space";
import type { HistoryEntry } from "../../src/history";

const fragment = location.hash.slice(1);
const token = fragment || sessionStorage.getItem("nix-manager-token") || "";
if (fragment) sessionStorage.setItem("nix-manager-token", fragment);
history.replaceState(null, "", `${location.pathname}${location.search}`);

export const hasToken = () => Boolean(token);

export async function api<T>(
  path: string,
  data?: unknown,
  timeout = 30_000,
): Promise<T> {
  const response = await fetch(`/api/${path}`, {
    method: data === undefined ? "GET" : "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      ...(data === undefined ? {} : { "Content-Type": "application/json" }),
    },
    body: data === undefined ? undefined : JSON.stringify(data),
    signal: AbortSignal.timeout(timeout),
  });
  const result = await response.json().catch(() => ({}));
  if (!response.ok)
    throw new Error(result.error || `Request failed (${response.status})`);
  return result as T;
}

/** An authenticated image as an object URL, or undefined when there is none. */
export async function imageUrl(path: string) {
  const response = await fetch(`/api/${path}`, { headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(30_000) });
  return response.ok ? URL.createObjectURL(await response.blob()) : undefined;
}

export const managerApi = {
  state: () => api<Snapshot>("state"),
  refresh: () => api<Snapshot>("refresh", {}, 60_000),
  plan: (request: Request) => api<Plan>("plan", request),
  run: (request: Request) => api<Operation>("run", request),
  cancel: (id: string) => api<{ ok: true }>("cancel", { id }),
  hold: (id: string) => api<{ ok: true }>("hold", { id }),
  preferences: (preferences: Preferences) => api<Preferences>("preferences", preferences),
  log: (id: string) => api<LogResult>(`log/${id}`),
  history: () => api<HistoryEntry[]>("history"),
  cleanupEstimate: (scope: "home" | "all", retention: number) =>
    api<CleanupEstimate>("cleanup-estimate", { scope, retention }, 60_000),
  removeReview: (id: string) => api<{ ok: true }>("review-remove", { id }),
};

/** Keeps a session open, reconnecting with backoff; each connect starts with a snapshot. */
export function connectSession(options: {
  onMessage: (message: SessionMessage) => void;
  onConnection: (connected: boolean) => void;
}) {
  let closed = false;
  let attempt = 0;
  let socket: WebSocket | undefined;
  let retry: ReturnType<typeof setTimeout> | undefined;
  const connect = () => {
    if (closed || !token) return;
    socket = new WebSocket(`ws://${location.host}/api/session`, [
      "nix-manager",
      token,
    ]);
    socket.addEventListener("open", () => {
      attempt = 0;
      options.onConnection(true);
    });
    socket.addEventListener("message", (message) => {
      try {
        options.onMessage(JSON.parse(String(message.data)) as SessionMessage);
      } catch (error) {
        // The next snapshot recovers from a malformed or unhandled event.
        console.error(error);
      }
    });
    socket.addEventListener("close", () => {
      options.onConnection(false);
      if (closed) return;
      retry = setTimeout(connect, Math.min(8_000, 250 * 2 ** attempt++));
    });
  };
  connect();
  return () => {
    closed = true;
    if (retry) clearTimeout(retry);
    socket?.close();
  };
}
