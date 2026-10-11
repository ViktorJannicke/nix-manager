// A small server for the setup assistant, used before there is a checkout to
// manage. It serves the same interface, which shows only the assistant, or
// why the dashboard could not start when it was opened without a terminal.

import { timingSafeEqual } from "node:crypto";
import { homedir } from "node:os";
import { Effect } from "effect";
import { securityHeaders, type UiAssets } from "./server";
import { createCheckout, readSetup, setupSteps, targetProblem, type SetupInfo, type SetupStep } from "./setup";
import { runnerLayer } from "./process";
import { resolveLocale } from "./preferences";
import type { Locale } from "./i18n";

export interface SetupState {
  info?: SetupInfo;
  /** Where the new checkout goes, once creation started. */
  target?: string;
  steps: { id: SetupStep; label: string; status: "waiting" | "running" | "done" | "failed" }[];
  running: boolean;
  error?: string;
  /** The dashboard's address, once it runs. */
  next?: string;
  /** Why the dashboard could not start; nothing else is offered then. */
  problem?: { message: string; flake: string };
  /** The desktop's language; there are no preferences before setup. */
  locale: Locale;
}

export function startSetupServer(assets: UiAssets, onCreated: (path: string) => void, problem?: SetupState["problem"]) {
  const token = crypto.randomUUID() + crypto.randomUUID();
  const state: SetupState = { steps: setupSteps.map((step) => ({ ...step, status: "waiting" })), running: false, problem, locale: resolveLocale({}) };
  let lastSeen = Date.now();
  const read = () =>
    problem ? Promise.resolve() :
    Effect.runPromise(readSetup().pipe(Effect.provide(runnerLayer()))).then(
      (info) => (state.info = info),
      (error) => (state.error = `${error.message ?? error}`),
    );
  const loaded = read();
  const json = (data: unknown, status = 200) => Response.json(data, { status, headers: securityHeaders });
  const create = (target: string) => {
    const info = state.info!;
    state.running = true;
    state.error = undefined;
    state.target = target;
    state.steps = setupSteps.map((step) => ({ ...step, status: "waiting" }));
    const progress = (id: SetupStep) => {
      for (const step of state.steps) if (step.status === "running") step.status = "done";
      state.steps.find((step) => step.id === id)!.status = "running";
    };
    Effect.runPromise(createCheckout(info, target, progress).pipe(Effect.provide(runnerLayer()))).then(
      (path) => {
        for (const step of state.steps) if (step.status === "running") step.status = "done";
        state.running = false;
        onCreated(path);
      },
      (error) => {
        for (const step of state.steps) if (step.status === "running") step.status = "failed";
        state.running = false;
        state.error = `${error.message ?? error}`;
      },
    );
  };
  const server = Bun.serve({
    hostname: "127.0.0.1",
    port: 0,
    maxRequestBodySize: 16_384,
    async fetch(req, server) {
      const url = new URL(req.url);
      if (req.headers.get("host") !== `127.0.0.1:${server.port}`) return json({ error: "Invalid host." }, 403);
      if (req.headers.has("origin") && req.headers.get("origin") !== `http://127.0.0.1:${server.port}`)
        return json({ error: "Invalid origin." }, 403);
      if (url.pathname.startsWith("/api/")) {
        const bearer = req.headers.get("authorization") || "";
        const expected = `Bearer ${token}`;
        if (Buffer.byteLength(bearer) !== Buffer.byteLength(expected) || !timingSafeEqual(Buffer.from(bearer), Buffer.from(expected)))
          return json({ error: "Unauthorized. Open the link from nix-manager." }, 401);
        if (url.pathname !== "/api/setup") return json({ error: "Not available during setup." }, 404);
        lastSeen = Date.now();
        await loaded;
        if (req.method === "GET") return json(state);
        if (req.method !== "POST") return json({ error: "Method not allowed." }, 405);
        if (problem) return json({ error: "Nix Manager could not start." }, 409);
        if (req.headers.get("content-type") !== "application/json") return json({ error: "Expected application/json." }, 415);
        const { target, refresh } = ((await req.json().catch(() => ({}))) ?? {}) as { target?: unknown; refresh?: unknown };
        if (state.running || state.next) return json({ error: "Setup is already running." }, 409);
        if (refresh === true) {
          state.error = undefined;
          await read();
          return json(state);
        }
        if (typeof target !== "string") return json({ error: "Choose a folder." }, 400);
        const path = target.replace(/^~(?=$|\/)/, homedir());
        const refused = state.info?.blocked ?? targetProblem(path);
        if (refused) return json({ error: refused }, 400);
        create(path);
        return json(state);
      }
      if (req.method !== "GET") return json({ error: "Method not allowed." }, 405);
      const asset = assets[url.pathname];
      if (!asset) return new Response("Not found", { status: 404, headers: securityHeaders });
      return new Response(asset.base64 ? Buffer.from(asset.body, "base64") : asset.body, {
        headers: { ...securityHeaders, "Content-Type": asset.type },
      });
    },
  });
  return {
    url: `http://127.0.0.1:${server.port}/?${problem ? "problem" : "setup"}#${token}`,
    /** Whether a page asked for the state recently, or setup is running. */
    get attended() {
      return state.running || Date.now() - lastSeen < 20_000;
    },
    /** Sends open pages on to the dashboard. */
    handOver(next: string) {
      state.next = next;
    },
    close() {
      server.stop(true);
    },
  };
}
