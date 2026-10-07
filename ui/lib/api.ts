import type {
  LogResult,
  BuildReview,
  InputProposal,
  ManagerEvent,
  ManagerState,
  Operation,
  Plan,
} from "../../src/contracts";
import type { Request } from "../../src/domain";

const fragment = location.hash.slice(1);
const token = fragment || sessionStorage.getItem("nix-manager-token") || "";
if (fragment) sessionStorage.setItem("nix-manager-token", fragment);
history.replaceState(null, "", `${location.pathname}${location.search}`);

export async function api<T>(
  path: string,
  data?: unknown,
  timeout = 15_000,
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
  const result = await response.json();
  if (!response.ok)
    throw new Error(result.error || `Request failed (${response.status})`);
  return result;
}

export const managerApi = {
  state: () => api<ManagerState>("state"),
  plan: (request: Request) => api<Plan>("plan", request),
  run: (request: Request) => api<Operation>("run", request),
  cancel: (id: string) => api<{ ok: true }>("cancel", { id }),
  log: (id: string) => api<LogResult>(`log/${id}`),
  generationReview: (kind: "home" | "system", number: number) =>
    api<BuildReview>("generation-review", { kind, number }),
  inputReview: (inputs: string[]) =>
    api<InputProposal>("input-review", { inputs }, 5 * 60_000),
  inputApply: (id: string, fingerprint: string) =>
    api<InputProposal>("input-apply", { id, fingerprint }),
  removeReview: (id: string) => api<{ ok: true }>("review-remove", { id }),
};

export function connectSession(options: {
  onEvent: (event: ManagerEvent) => void;
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
        options.onEvent(JSON.parse(String(message.data)) as ManagerEvent);
      } catch {
        // The next state snapshot recovers from a malformed event.
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
