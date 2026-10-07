import { Schema } from "effect";
import { timingSafeEqual } from "node:crypto";
import { Request as RequestSchema, ManagerError, plan } from "./domain";
import { acquireLock, type Manager } from "./manager";
import { mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import { extname, join, relative } from "node:path";
import { tmpdir } from "node:os";
import { advancedState, applyInputProposal, generationReview, getReview, proposeInputs, removeReview, repositoryState } from "./advanced";

export interface UiAsset {
  body: string;
  type: string;
}
export type UiAssets = Record<string, UiAsset>;
declare const NIX_MANAGER_UI_ASSETS: string;
const mime = (path: string) =>
  ({
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".svg": "image/svg+xml",
  })[extname(path)] || "application/octet-stream";
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
          { body: await readFile(path, "utf8"), type: mime(path) },
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
  let clients = 0;
  let connected = false;
  let disconnectedAt: number | undefined;
  const sockets = new Set<{
    send(data: string): unknown;
    getBufferedAmount(): number;
  }>();
  const headers = {
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "no-referrer",
    "Content-Security-Policy":
      // Bits UI applies transient inline styles for dialog scroll locking and
      // positioning. Scripts remain restricted to the bundled same-origin app.
      "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'; form-action 'none'",
  };
  const json = (data: unknown, status = 200) =>
    Response.json(data, { status, headers });
  const server = Bun.serve({
    hostname: "127.0.0.1",
    port,
    maxRequestBodySize: 16_384,
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
            return json({
              config: {
                flake: manager.config.flake,
                hosts: manager.config.hosts,
                hostKey: manager.config.hostKey,
                homeProfile: manager.config.homeProfile,
              },
              active: manager.active,
              operations: manager.history(),
              updates: manager.updates(),
              advanced: advancedState(manager.config),
            });
          if (req.method === "GET" && url.pathname.startsWith("/api/log/"))
            return json({ text: manager.log(url.pathname.slice(9)) });
          if (req.method === "POST") {
            if (req.headers.get("content-type") !== "application/json")
              return json({ error: "Expected application/json." }, 415);
            if (url.pathname === "/api/generation-review") {
              const body = await req.json();
              if (
                !body ||
                !["home", "system"].includes(body.kind) ||
                !Number.isInteger(body.number)
              )
                return json({ error: "Invalid generation selection." }, 400);
              return json(
                generationReview(manager.config, body.kind, body.number),
                201,
              );
            }
            if (url.pathname === "/api/input-review") {
              const body = await req.json();
              if (!body || !Array.isArray(body.inputs))
                return json({ error: "Select flake inputs to update." }, 400);
              const release = acquireLock(manager.config.stateDir);
              try {
                return json(await proposeInputs(manager.config, body.inputs), 201);
              } finally {
                release();
              }
            }
            if (url.pathname === "/api/input-apply") {
              const body = await req.json();
              if (!body || typeof body.id !== "string" || typeof body.fingerprint !== "string")
                return json({ error: "Invalid input proposal." }, 400);
              const release = acquireLock(manager.config.stateDir);
              try {
                return json(applyInputProposal(manager.config, body.id, body.fingerprint));
              } finally {
                release();
              }
            }
            if (url.pathname === "/api/review-remove") {
              const body = await req.json();
              if (!body || typeof body.id !== "string")
                return json({ error: "Invalid build review." }, 400);
              removeReview(manager.config, body.id);
              return json({ ok: true });
            }
            if (["/api/plan", "/api/run"].includes(url.pathname)) {
              const r = Schema.decodeUnknownSync(RequestSchema, {
                onExcessProperty: "error",
              })(await req.json());
              if (
                ["review-build", "review-apply"].includes(r.action) &&
                r.fingerprint !== repositoryState(manager.config.flake).fingerprint
              )
                return json(
                  { error: "The checkout changed. Refresh and review the plan again." },
                  409,
                );
              const resolved =
                r.action === "review-apply"
                  ? (() => {
                      const review = getReview(manager.config, r.reviewId!);
                      if (review.fingerprint !== r.fingerprint)
                        throw new ManagerError({ message: "The checkout changed after this review was built." });
                      return { ...r, homeOutput: review.homeOutput, systemOutput: review.systemOutput };
                    })()
                  : { ...r, homeOutput: undefined, systemOutput: undefined };
              if (url.pathname === "/api/plan")
                return json(plan(manager.config, resolved));
              return json(manager.start(resolved), 202);
            }
            if (url.pathname === "/api/cancel") {
              const body = await req.json();
              if (!body || body.id !== manager.active?.id)
                return json(
                  {
                    error: "That operation is no longer active in this window.",
                  },
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
        ? new Response(asset.body, {
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
      open(socket) {
        sockets.add(socket);
        clients++;
        connected = true;
        disconnectedAt = undefined;
      },
      close(socket) {
        sockets.delete(socket);
        if (--clients === 0) disconnectedAt = Date.now();
      },
      message() {},
    },
  });
  const unsubscribe = manager.onEvent((event) => {
    const message = JSON.stringify(event);
    for (const socket of sockets) {
      // Output is recoverable from the bounded log API. Drop chunks for a slow
      // client instead of allowing it to put pressure on the executor.
      if (event.type === "output" && socket.getBufferedAmount() > 256_000)
        continue;
      socket.send(message);
    }
  });
  return {
    server,
    token,
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
      unsubscribe();
      server.stop(true);
    },
  };
}
