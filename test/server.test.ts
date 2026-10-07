import { expect, test } from "bun:test";
import { rmSync } from "node:fs";
import { Effect } from "effect";
import { Manager } from "../src/manager";
import { startServer } from "../src/server";
import { request } from "../src/domain";
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
  socket.onmessage = (message) => events.push(JSON.parse(String(message.data)));
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
