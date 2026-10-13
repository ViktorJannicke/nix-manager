import { expect, test } from "bun:test";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { Effect } from "effect";
import { Manager } from "../src/manager";
import { startServer } from "../src/server";
import { request } from "../src/domain";
import { readPreferences } from "../src/preferences";
import { fixture } from "./helpers";

test("loopback API enforces authentication, origin, host, schema and single operation", async () => {
  const config = fixture(),
    manager = new Manager(config, undefined, () => Effect.never);
  const service = startServer(manager, "// fixture");
  const base = service.url.split("/#")[0],
    auth = { Authorization: `Bearer ${service.token}` },
    headers = { ...auth, "Content-Type": "application/json" };
  try {
    expect(service.server.hostname).toBe("127.0.0.1");
    const page = await fetch(base);
    expect(page.headers.get("Content-Security-Policy")).toContain(
      "frame-ancestors 'none'",
    );
    expect(page.headers.get("Content-Security-Policy")).toContain(
      "style-src 'self' 'unsafe-inline'",
    );
    expect(await page.text()).not.toContain(service.token);
    expect((await fetch(`${base}/api/state`)).status).toBe(401);
    expect((await fetch(`${base}/api/session`)).status).toBe(401);
    expect(
      (
        await fetch(`${base}/api/session`, {
          headers: { "Sec-WebSocket-Protocol": "nix-manager, wrong-token" },
        })
      ).status,
    ).toBe(401);
    expect(
      (
        await fetch(`${base}/api/session`, {
          headers: {
            "Sec-WebSocket-Protocol": `nix-manager, ${service.token}`,
            Origin: "https://malicious.example",
          },
        })
      ).status,
    ).toBe(403);
    expect(service.connected).toBe(false);
    expect((await fetch(`${base}/api/state`, { headers: auth })).status).toBe(
      200,
    );
    expect(
      (
        await fetch(`${base}/api/state`, {
          headers: { ...auth, Origin: "https://malicious.example" },
        })
      ).status,
    ).toBe(403);
    expect(
      (
        await fetch(`${base}/api/state`, {
          headers: { ...auth, Host: "malicious.example" },
        })
      ).status,
    ).toBe(403);
    expect(
      (
        await fetch(`${base}/api/run`, {
          method: "POST",
          headers,
          body: JSON.stringify({ ...request(), action: "arbitrary" }),
        })
      ).status,
    ).toBe(400);
    expect(
      (
        await fetch(`${base}/api/run`, {
          method: "POST",
          headers,
          body: JSON.stringify({ ...request(), unexpected: "bad" }),
        })
      ).status,
    ).toBe(400);
    const preview = await fetch(`${base}/api/plan`, {
      method: "POST",
      headers,
      body: JSON.stringify(request({ action: "switch" })),
    });
    expect(preview.status).toBe(200);
    expect(manager.history()).toHaveLength(0);
    const run = await fetch(`${base}/api/run`, {
      method: "POST",
      headers,
      body: JSON.stringify(request()),
    });
    expect(run.status).toBe(202);
    expect(
      (
        await fetch(`${base}/api/run`, {
          method: "POST",
          headers,
          body: JSON.stringify(request()),
        })
      ).status,
    ).toBe(409);
    expect(
      (
        await fetch(`${base}/api/cancel`, {
          method: "POST",
          headers,
          body: '{"id":"wrong"}',
        })
      ).status,
    ).toBe(409);
    expect((await fetch(`${base}/api/log/bad`, { headers: auth })).status).toBe(
      400,
    );
  } finally {
    await manager.close();
    service.close();
    rmSync(config.flake, { recursive: true, force: true });
  }
});

test("preferences are kept in the state directory and only known ones are accepted", async () => {
  const config = fixture(),
    manager = new Manager(config, undefined, () => Effect.never);
  const service = startServer(manager, "// fixture");
  const base = service.url.split("/#")[0],
    headers = { Authorization: `Bearer ${service.token}`, "Content-Type": "application/json" };
  const state = async () => (await (await fetch(`${base}/api/state`, { headers })).json()).preferences;
  const save = (body: unknown) => fetch(`${base}/api/preferences`, { method: "POST", headers, body: JSON.stringify(body) });
  try {
    // Without earlier operations the dashboard starts simple, with a kept history.
    expect(await state()).toEqual({ detail: "simple", history: true });
    expect(await (await save({ detail: "detailed" })).json()).toEqual({ detail: "detailed", history: true });
    expect((await save({ theme: "dark" })).status).toBe(200);
    expect(await state()).toEqual({ detail: "detailed", history: true, theme: "dark" });
    expect((await save({ detail: "expert" })).status).toBe(400);
    expect((await save({ token: "x" })).status).toBe(400);
    expect(JSON.parse(readFileSync(join(config.stateDir, "preferences.json"), "utf8"))).toEqual({ detail: "detailed", history: true, theme: "dark" });
    const estimate = (body: unknown) => fetch(`${base}/api/cleanup-estimate`, { method: "POST", headers, body: JSON.stringify(body) });
    for (const body of [{ scope: "system", retention: 3 }, { scope: "home", retention: 0 }, { scope: "home", retention: "3" }])
      expect((await estimate(body)).status).toBe(400);
  } finally {
    await manager.close();
    service.close();
    rmSync(config.flake, { recursive: true, force: true });
  }
});

test("someone who ran operations before starts with the detailed view", () => {
  const config = fixture();
  try {
    mkdirSync(join(config.stateDir, "operations"), { recursive: true });
    writeFileSync(join(config.stateDir, "operations", "1-a.json"), "{}");
    expect(readPreferences(config)).toEqual({ detail: "detailed" });
    // The choice sticks.
    rmSync(join(config.stateDir, "operations"), { recursive: true });
    expect(readPreferences(config)).toEqual({ detail: "detailed" });
  } finally {
    rmSync(config.flake, { recursive: true, force: true });
  }
});

test("a new user's first operation does not count as earlier use", async () => {
  const config = fixture();
  const manager = new Manager(config, undefined, () => Effect.never);
  try {
    // As `nix-manager software prepare` does before anything reads preferences.
    manager.start(request({ action: "build", scope: "home" }));
    expect(readPreferences(config)).toEqual({ detail: "simple", history: true });
  } finally {
    await manager.close();
    rmSync(config.flake, { recursive: true, force: true });
  }
});

test("authenticated sessions receive ordered operation and output events", async () => {
  const config = fixture();
  const manager = new Manager(config, undefined, (_command, output) =>
    Effect.sync(() => {
      output?.({ stream: "stdout", text: "fixture output\n" });
      return { code: 0, stdout: "fixture output\n", stderr: "" };
    }),
  );
  const service = startServer(manager, "// fixture");
  const socket = new WebSocket(
    `ws://127.0.0.1:${service.server.port}/api/session`,
    ["nix-manager", service.token],
  );
  const events: Array<{
    type: string;
    sequence: number;
    text?: string;
    operation?: { status: string };
  }> = [];
  let snapshots = 0;
  socket.onmessage = (message) => {
    const event = JSON.parse(String(message.data));
    if (event.type === "snapshot") snapshots++;
    else events.push(event);
  };
  try {
    await new Promise<void>((resolve, reject) => {
      socket.onopen = () => resolve();
      socket.onerror = () => reject(new Error("WebSocket did not connect"));
    });
    manager.start(request({ action: "build", scope: "home" }));
    await manager.wait();
    const deadline = Date.now() + 1_000;
    while (
      !events.some(
        (event) =>
          event.type === "operation" && event.operation?.status === "succeeded",
      ) &&
      Date.now() < deadline
    )
      await Bun.sleep(10);
    expect(events.some((event) => event.text?.includes("fixture output"))).toBe(
      true,
    );
    expect(events.at(-1)?.operation?.status).toBe("succeeded");
    // A full snapshot arrives on connect, so clients need no initial poll.
    expect(snapshots).toBeGreaterThanOrEqual(1);
    const output = events.find((event) => event.type === "output") as { offset?: number } | undefined;
    expect(output?.offset).toBe(0);
    expect(events.map((event) => event.sequence)).toEqual(
      events.map((event) => event.sequence).slice().sort((a, b) => a - b),
    );
  } finally {
    socket.close();
    await manager.close();
    service.close();
    rmSync(config.flake, { recursive: true, force: true });
  }
});
