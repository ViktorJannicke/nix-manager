import { Schema } from "effect";
import { timingSafeEqual } from "node:crypto";
import { Request as RequestSchema, ManagerError, resolveHost } from "./domain";
import type { Manager } from "./manager";
import { mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import { extname, join, relative } from "node:path";
import { tmpdir, userInfo } from "node:os";
import { Observer } from "./observe";
import { listReviews, removeReview } from "./reviews";
import type { SessionMessage, Snapshot } from "./contracts";
import { Store } from "./store";
import { detectHardware } from "./hardware";

export interface UiAsset {
  body: string;
  type: string;
  /** Binary assets such as fonts are embedded as base64. */
  base64?: boolean;
}
export type UiAssets = Record<string, UiAsset>;
declare const NIX_MANAGER_UI_ASSETS: string;
const mime = (path: string) =>
  ({
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".svg": "image/svg+xml",
    ".woff2": "font/woff2",
  })[extname(path)] || "application/octet-stream";
export function asset(path: string, data: Buffer): UiAsset {
  const type = mime(path);
  return /^text\/|svg/.test(type)
    ? { body: data.toString("utf8"), type }
    : { body: data.toString("base64"), type, base64: true };
}
async function readAssets(root: string): Promise<UiAssets> {
  const walk = async (directory: string): Promise<string[]> =>
    (
      await Promise.all(
        (await readdir(directory, { withFileTypes: true })).map((entry) => {
          const path = join(directory, entry.name);
          return entry.isDirectory() ? walk(path) : Promise.resolve([path]);
        }),
      )
    ).flat();
  return Object.fromEntries(
    await Promise.all(
      (await walk(root)).map(async (path) => {
        const name = relative(root, path);
        return [
          name === "index.html" ? "/" : `/${name}`,
          asset(path, await readFile(path)),
        ];
      }),
    ),
  );
}
export async function browserAssets(): Promise<UiAssets> {
  if (typeof NIX_MANAGER_UI_ASSETS !== "undefined")
    return JSON.parse(NIX_MANAGER_UI_ASSETS);
  const output = await mkdtemp(join(tmpdir(), "nix-manager-ui-"));
  try {
    const { build } = await import("vite");
    await build({
      configFile: new URL("../vite.config.ts", import.meta.url).pathname,
      logLevel: "silent",
      build: { outDir: output, emptyOutDir: true },
    });
    return await readAssets(output);
  } finally {
    await rm(output, { recursive: true, force: true });
  }
}
export function startServer(
  manager: Manager,
  input: UiAssets | string,
  port = 0,
) {
  const assets: UiAssets =
    typeof input === "string"
      ? {
          "/": {
            body: '<!doctype html><script type="module" src="/assets/app.js"></script>',
            type: "text/html; charset=utf-8",
          },
          "/assets/app.js": { body: input, type: "text/javascript" },
        }
      : input;
  const token = crypto.randomUUID() + crypto.randomUUID();
  const observer = new Observer(manager.config);
  // Assigned below; the store reports catalog and draft changes as snapshots.
  let broadcastLater = () => {};
  const store = new Store(manager.config, () => broadcastLater());
  const fingerprint = () => observer.current?.repository.fingerprint ?? "";
  const lockHash = () => observer.current?.repository.lockHash;
  // Read once; suggestions for hardware plugged in later appear after a restart.
  const hardware = detectHardware(process.env.NIX_MANAGER_SYSFS);
  let clients = 0;
  let connected = false;
  let disconnectedAt: number | undefined;
  const sockets = new Set<{
    send(data: string): unknown;
    getBufferedAmount(): number;
  }>();
  const snapshot = async (): Promise<Snapshot> => ({
    config: {
      flake: manager.config.flake,
      hosts: manager.config.hosts,
      hostKey: manager.config.hostKey,
      homeProfile: manager.config.homeProfile,
      user: userInfo().username,
      hardware,
    },
    active: manager.active,
    operations: manager.history(),
    updates: manager.updates(),
    system: observer.current,
    reviews: await listReviews(manager.config),
    draft: await store.draft(fingerprint()),
    catalog: store.catalogStatus(lockHash()),
  });
  const send = (message: SessionMessage) => {
    const text = JSON.stringify(message);
    for (const socket of sockets) {
      // Output is recoverable from the bounded log API. Drop chunks for a slow
      // client instead of allowing it to put pressure on the executor.
      if (message.type !== "snapshot" && message.type !== "operation" && socket.getBufferedAmount() > 256_000)
        continue;
      socket.send(text);
    }
  };
  // Coalesce bursts of step changes into one snapshot per tick.
  let scheduled: ReturnType<typeof setTimeout> | undefined;
  const broadcast = () => {
    if (scheduled || !sockets.size) return;
    scheduled = setTimeout(async () => {
      scheduled = undefined;
      try {
        send({ type: "snapshot", snapshot: await snapshot() });
      } catch (error) {
        console.error(error);
      }
    }, 100);
  };
  broadcastLater = broadcast;
  const observe = () =>
    observer.refresh().catch((error) => console.error(`Observe system: ${error}`));
  let wasBusy = manager.busy;
  const unsubscribeChange = manager.onChange(() => {
    // Generations, the checkout and reviews change when an operation ends.
    if (wasBusy && !manager.busy) void observe();
    wasBusy = manager.busy;
    broadcast();
  });
  const unsubscribeObserver = observer.onChange(broadcast);
  const unsubscribeEvents = manager.onEvent((event) => send(event));
  void observe();
  // Notice edits to the checkout and changes made outside the manager while
  // a dashboard is open.
  const ticker = setInterval(() => {
    if (sockets.size) void observe();
  }, 30_000);
  const headers = {
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "no-referrer",
    "Content-Security-Policy":
      // Bits UI applies transient inline styles for dialog scroll locking and
      // positioning. Scripts remain restricted to the bundled same-origin app.
      "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'",
  };
  const json = (data: unknown, status = 200) =>
    Response.json(data, { status, headers });
  const body = async (req: Request) => {
    const value = await req.json().catch(() => undefined);
    return value && typeof value === "object" ? (value as Record<string, unknown>) : {};
  };
  const server = Bun.serve({
    hostname: "127.0.0.1",
    port,
    maxRequestBodySize: 65_536,
    async fetch(req, server) {
      const url = new URL(req.url);
      const origin = `http://127.0.0.1:${server.port}`;
      if (req.headers.get("host") !== `127.0.0.1:${server.port}`)
        return json({ error: "Invalid host." }, 403);
      if (req.headers.has("origin") && req.headers.get("origin") !== origin)
        return json({ error: "Invalid origin." }, 403);
      if (url.pathname.startsWith("/api/")) {
        const session = url.pathname === "/api/session";
        // Browsers cannot set Authorization on a WebSocket handshake. Carry
        // the token as a protocol offer, keeping it out of URLs and logs.
        const protocols = req.headers
          .get("sec-websocket-protocol")
          ?.split(",")
          .map((p) => p.trim());
        const bearer = session
          ? protocols?.[0] === "nix-manager" && protocols.length === 2
            ? `Bearer ${protocols[1]}`
            : ""
          : req.headers.get("authorization") || "";
        const expected = `Bearer ${token}`;
        if (
          Buffer.byteLength(bearer) !== Buffer.byteLength(expected) ||
          !timingSafeEqual(Buffer.from(bearer), Buffer.from(expected))
        )
          return json(
            { error: "Unauthorized. Open the link from nix-manager." },
            401,
          );
        if (session) {
          if (
            req.method === "GET" &&
            server.upgrade(req, {
              headers: { "Sec-WebSocket-Protocol": "nix-manager" },
            })
          )
            return;
          return json({ error: "Expected a WebSocket connection." }, 400);
        }
        try {
          if (req.method === "GET" && url.pathname === "/api/state")
            return json(await snapshot());
          if (req.method === "GET" && url.pathname.startsWith("/api/log/"))
            return json(manager.log(url.pathname.slice(9)));
          if (req.method === "POST") {
            if (req.headers.get("content-type") !== "application/json")
              return json({ error: "Expected application/json." }, 415);
            if (url.pathname === "/api/refresh") {
              await observer.refresh();
              return json(await snapshot());
            }
            if (url.pathname === "/api/review-remove") {
              const { id } = await body(req);
              if (typeof id !== "string")
                return json({ error: "Invalid review." }, 400);
              if (manager.active?.request.reviewId === id)
                return json({ error: "This review is in use by the running operation." }, 409);
              removeReview(manager.config, id);
              broadcast();
              return json({ ok: true });
            }
            if (url.pathname === "/api/draft") {
              const { changes } = await body(req);
              return json(await store.setDraft(changes));
            }
            if (url.pathname.startsWith("/api/software/")) {
              const input = await body(req);
              const host = typeof input.host === "string" && input.host ? input.host : manager.config.hostKey;
              resolveHost(manager.config.hosts, host);
              const scope = input.scope === "system" ? "system" : "home";
              const text = (value: unknown) => (typeof value === "string" ? value.slice(0, 200) : "");
              const strings = (value: unknown) =>
                Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
              switch (url.pathname.slice(14)) {
                case "inventory":
                  return json(await store.inventory(host, fingerprint()));
                case "catalog": {
                  const hash = lockHash();
                  if (!hash) return json({ error: "The checkout has not been read yet." }, 409);
                  store.buildCatalog(hash, input.rebuild === true);
                  return json(store.catalogStatus(hash));
                }
                case "search":
                  return json(store.search(lockHash(), text(input.query), { free: input.free === true, working: input.working === true }));
                case "lookup":
                  return json(store.lookup(lockHash(), strings(input.attrs)));
                case "options":
                  return json(await store.searchOptions(host, scope, text(input.query), fingerprint()));
                case "describe":
                  return json(await store.describe(host, scope, strings(input.options), fingerprint()));
                case "values":
                  return json(await store.values(host, scope, strings(input.options)));
              }
            }
            if (url.pathname === "/api/plan" || url.pathname === "/api/run") {
              const r = Schema.decodeUnknownSync(RequestSchema, {
                onExcessProperty: "error",
              })(await req.json());
              if (url.pathname === "/api/plan") return json(manager.plan(r));
              return json(manager.start(r), 202);
            }
            if (url.pathname === "/api/cancel") {
              const { id } = await body(req);
              if (id !== manager.active?.id)
                return json(
                  { error: "That operation is no longer active in this window." },
                  409,
                );
              manager.cancel();
              return json({ ok: true });
            }
          }
          return json({ error: "Unknown endpoint or method." }, 404);
        } catch (error) {
          return json(
            { error: error instanceof Error ? error.message : String(error) },
            error instanceof ManagerError && /running/.test(error.message)
              ? 409
              : 400,
          );
        }
      }
      if (req.method !== "GET")
        return json({ error: "Method not allowed." }, 405);
      const asset = assets[url.pathname];
      return asset
        ? new Response(asset.base64 ? Buffer.from(asset.body, "base64") : asset.body, {
            headers: {
              ...headers,
              "Content-Type": asset.type,
              "Content-Security-Policy": headers[
                "Content-Security-Policy"
              ].replace(
                "connect-src 'self'",
                `connect-src 'self' ws://127.0.0.1:${server.port}`,
              ),
            },
          })
        : new Response("Not found", { status: 404, headers });
    },
    websocket: {
      // Native ping/pong keeps hidden tabs connected without JavaScript timers.
      sendPings: true,
      idleTimeout: 60,
      async open(socket) {
        sockets.add(socket);
        clients++;
        connected = true;
        disconnectedAt = undefined;
        socket.send(JSON.stringify({ type: "snapshot", snapshot: await snapshot() } satisfies SessionMessage));
        if (!observer.current || Date.now() - Date.parse(observer.current.observedAt) > 5_000)
          void observe();
      },
      close(socket) {
        sockets.delete(socket);
        if (--clients === 0) disconnectedAt = Date.now();
      },
      message() {},
    },
  });
  return {
    server,
    token,
    observer,
    url: `http://127.0.0.1:${server.port}/#${token}`,
    get clients() {
      return clients;
    },
    get connected() {
      return connected;
    },
    get disconnectedAt() {
      return disconnectedAt;
    },
    close() {
      clearInterval(ticker);
      if (scheduled) clearTimeout(scheduled);
      unsubscribeChange();
      unsubscribeObserver();
      unsubscribeEvents();
      server.stop(true);
    },
  };
}
