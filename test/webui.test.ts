import { expect, test } from "bun:test";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { join } from "node:path";
import { request } from "../src/domain";
import { fixture } from "./helpers";

async function until(check: () => boolean, timeout = 10_000) {
  const deadline = Date.now() + timeout;
  while (!check()) {
    if (Date.now() >= deadline)
      throw new Error("UI lifecycle condition timed out");
    await Bun.sleep(20);
  }
}

async function dashboard(noOpen = false) {
  const config = fixture();
  const bin = join(config.flake, "bin"),
    registry = join(config.flake, "hosts.json"),
    urlFile = join(config.flake, "opened-url"),
    release = join(config.flake, "release-build");
  mkdirSync(bin);
  writeFileSync(registry, JSON.stringify(config.hosts));
  // Replace only browser launch and build executables. The real CLI, server,
  // operation manager and shutdown path run together in a child process.
  writeFileSync(
    join(bin, "xdg-open"),
    `#!${process.execPath}\nawait Bun.write(process.env.NIX_MANAGER_TEST_URL, process.argv[2]);\n`,
    { mode: 0o755 },
  );
  writeFileSync(
    join(bin, "nix"),
    `#!${process.execPath}\nwhile (!(await Bun.file(process.env.NIX_MANAGER_TEST_RELEASE).exists())) await Bun.sleep(20);\n`,
    { mode: 0o755 },
  );
  const child = Bun.spawn(
    [
      process.execPath,
      new URL("../src/main.ts", import.meta.url).pathname,
      "--flake",
      config.flake,
      "--host",
      "test",
      "ui",
      noOpen ? "--no-open" : "--browser",
    ],
    {
      env: {
        ...process.env,
        PATH: `${bin}:${process.env.PATH}`,
        NIX_MANAGER_HOSTS: registry,
        NIX_MANAGER_STATE_DIR: config.stateDir,
        NIX_MANAGER_TEST_URL: urlFile,
        NIX_MANAGER_TEST_RELEASE: release,
      },
      stdout: "pipe",
      stderr: "pipe",
    },
  );
  const dispose = async () => {
    if (child.exitCode === null) child.kill("SIGTERM");
    const fallback = setTimeout(() => child.kill("SIGKILL"), 3_000);
    try {
      await child.exited;
    } finally {
      clearTimeout(fallback);
      rmSync(config.flake, { recursive: true, force: true });
    }
  };
  try {
    let url: string;
    if (noOpen) {
      const reader = child.stdout.getReader();
      const first = await reader.read();
      reader.releaseLock();
      url = new TextDecoder()
        .decode(first.value)
        .trim()
        .replace("Nix Manager: ", "");
    } else {
      await until(() => existsSync(urlFile));
      url = readFileSync(urlFile, "utf8");
    }
    const address = new URL(url),
      token = address.hash.slice(1);
    const connect = async () => {
      const socket = new WebSocket(`ws://${address.host}/api/session`, [
        "nix-manager",
        token,
      ]);
      await new Promise<void>((resolve, reject) => {
        socket.onopen = () => resolve();
        socket.onerror = () =>
          reject(new Error("Dashboard session failed to connect"));
      });
      return socket;
    };
    return {
      child,
      config,
      release,
      connect,
      dispose,
      base: address.origin,
      token,
    };
  } catch (error) {
    await dispose();
    throw error;
  }
}

test("standalone UI exits after the last window closes, but survives other windows and reloads", async () => {
  const app = await dashboard();
  const sockets: WebSocket[] = [];
  try {
    const first = await app.connect(),
      second = await app.connect();
    sockets.push(first, second);
    first.close();
    await Bun.sleep(1_500);
    expect(app.child.exitCode).toBeNull();
    second.close();
    await Bun.sleep(100);
    const reloaded = await app.connect();
    sockets.push(reloaded);
    await Bun.sleep(1_500);
    expect(app.child.exitCode).toBeNull();
    reloaded.close();
    await until(() => app.child.exitCode !== null);
    expect(app.child.exitCode).toBe(0);
    expect(await new Response(app.child.stderr).text()).toBe("");
  } finally {
    sockets.forEach((socket) => socket.close());
    await app.dispose();
  }
}, 15_000);

test("closing the UI lets an active build finish before the process exits", async () => {
  const app = await dashboard();
  const socket = await app.connect();
  try {
    const response = await fetch(`${app.base}/api/run`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${app.token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(
        request({ action: "build", host: "test", scope: "home" }),
      ),
    });
    expect(response.status).toBe(202);
    const operation = await response.json();
    socket.close();
    await Bun.sleep(1_500);
    expect(app.child.exitCode).toBeNull();
    writeFileSync(app.release, "done");
    await until(() => app.child.exitCode !== null);
    expect(app.child.exitCode).toBe(0);
    const saved = JSON.parse(
      readFileSync(
        join(app.config.stateDir, "operations", `${operation.id}.json`),
        "utf8",
      ),
    );
    expect(saved.status).toBe("succeeded");
  } finally {
    socket.close();
    await app.dispose();
  }
}, 15_000);

test("--no-open stays resident after its dashboard closes", async () => {
  const app = await dashboard(true);
  const socket = await app.connect();
  try {
    socket.close();
    await Bun.sleep(1_500);
    expect(app.child.exitCode).toBeNull();
    app.child.kill("SIGTERM");
    await until(() => app.child.exitCode !== null);
    expect(app.child.exitCode).toBe(0);
  } finally {
    socket.close();
    await app.dispose();
  }
}, 10_000);

// The Nix check supplies the native library built from the pinned source.
test.skipIf(!process.env.NIX_MANAGER_WEBUI_LIBRARY)(
  "native WebUI ABI loads and allocates an isolated window",
  async () => {
    const source = new URL("../src/window.ts", import.meta.url).pathname;
    const child = Bun.spawn(
      [
        process.execPath,
        "-e",
        `const { createWindow } = await import(${JSON.stringify(source)}); const window = createWindow(); window.close();`,
      ],
      { stdout: "pipe", stderr: "pipe" },
    );
    const code = await child.exited;
    expect(await new Response(child.stderr).text()).toBe("");
    expect(code).toBe(0);
  },
);
