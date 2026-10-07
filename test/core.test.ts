import { afterEach, expect, test } from "bun:test";
import { mkdirSync, readFileSync, realpathSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { Effect } from "effect";
import { parseCli } from "../src/cli";
import {
  plan,
  request,
  ManagerError,
  resolveHost,
  type Command,
} from "../src/domain";
import { Manager, acquireLock } from "../src/manager";
import { runProcess } from "../src/process";
import { fixture } from "./helpers";
import { advancedState, applyInputProposal, generationReview, repositoryState, reviewPath, reviews } from "../src/advanced";

const cleanups: (() => void)[] = [];
afterEach(() => {
  cleanups
    .splice(0)
    .reverse()
    .forEach((f) => f());
});
const setup = () => {
  const config = fixture();
  cleanups.push(() => rmSync(config.flake, { recursive: true, force: true }));
  return config;
};

test("CLI preserves modifier grammar, defaults and literal passthrough", async () => {
  expect((await parseCli(["update", "cleanup", "switch"]))!.request).toMatchObject({
    scope: "all",
    update: true,
    cleanup: true,
    action: "switch",
  });
  expect((await parseCli(["boot"]))!.request.scope).toBe("system");
  expect((await parseCli(["cleanup"]))!.request.action).toBe("gc");
  expect((await parseCli(["gc", "switch"]))!.request).toMatchObject({
    action: "switch",
    cleanup: true,
  });
  expect(
    (await parseCli(["home", "switch", "--", "$(touch /tmp/no)"]))!.request.extra,
  ).toEqual(["$(touch /tmp/no)"]);
  expect((await parseCli(["ui"]))!.mode).toBe("ui");
});
test("invalid CLI combinations fail before commands run", async () => {
  for (const args of [
    ["switch", "boot"],
    ["home", "all", "switch"],
    ["cleanup", "build"],
    ["update", "rollback"],
    ["cleanup", "rollback"],
    ["--force", "switch"],
    ["system", "t3code-update"],
    ["home", "boot"],
    ["gc", "--", "foo"],
    ["--flake"],
    ["tray", "--interval", "1"],
    ["ui", "switch"],
    ["ui", "update"],
    ["ui", "--dry-run"],
  ])
    await expect(parseCli(args)).rejects.toThrow();
});
test("generated help, version and completions do not need a checkout", async () => {
  const source = new URL("../src/main.ts", import.meta.url).pathname;
  for (const [args, expected] of [
    [["--help"], "USAGE"],
    [["--version"], "1.0.0"],
    [["--completions", "fish"], "complete"],
  ] as const) {
    const child = Bun.spawn([process.execPath, source, ...args], {
      cwd: "/",
      env: { ...process.env, NO_COLOR: "1" },
      stdout: "pipe",
      stderr: "pipe",
    });
    const [code, stdout, stderr] = await Promise.all([
      child.exited,
      new Response(child.stdout).text(),
      new Response(child.stderr).text(),
    ]);
    expect(code).toBe(0);
    expect(stdout).toContain(expected);
    expect(stdout).not.toContain("\x1b[");
    expect(stderr).toBe("");
  }
});
test("plan builds all scopes before activations and cleanup; updates only once", () => {
  const config = setup(),
    p = plan(
      config,
      request({ action: "switch", update: true, cleanup: true }),
    );
  expect(p.steps.map((s) => s.argv.slice(0, 2).join(" "))).toEqual([
    "nix flake",
    "home-manager build",
    "nixos-rebuild build",
    "home-manager switch",
    "pkexec /immutable/bin/nix-manager",
    "nix-env --profile",
    "pkexec /immutable/bin/nix-manager",
  ]);
  expect(p.steps[5].argv).toContain(config.homeProfile);
  expect(p.steps.filter((s) => s.argv[1] === "flake")).toHaveLength(1);
  expect(p.notes.join(" ")).toContain("may already be active");
});
test("host resolution accepts registered names only", () => {
  const config = setup();
  expect(resolveHost(config.hosts, "test-machine")[0]).toBe("test");
  expect(() => plan(config, request({ host: "unknown" }))).toThrow("Unknown");
});
test("home gc skips absent profiles and targets only the Home Manager profile", () => {
  const config = setup();
  expect(
    plan(
      { ...config, homeProfileExists: false },
      request({ action: "gc", scope: "home" }),
    ).steps.map((s) => s.argv),
  ).toEqual([["nix-store", "--gc"]]);
  expect(
    plan(config, request({ action: "gc", scope: "home" })).steps[0].argv,
  ).toEqual([
    "nix-env",
    "--profile",
    config.homeProfile,
    "--delete-generations",
    "+3",
  ]);
});
for (const failure of [
  "home-manager build",
  "nixos-rebuild build",
  "home-manager switch",
])
  test(`${failure} failure prevents subsequent activation/cleanup`, async () => {
    const config = setup(),
      calls: string[] = [];
    const run = (c: Command) => {
      const key = c.argv.slice(0, 2).join(" ");
      calls.push(key);
      return key === failure
        ? Effect.fail(new ManagerError({ message: "Test failure" }))
        : Effect.succeed({ code: 0, stdout: "", stderr: "" });
    };
    const manager = new Manager(config, undefined, run);
    manager.start(request({ action: "switch", cleanup: true }));
    const result = await manager.wait();
    expect(result?.status).toBe("failed");
    expect(result?.steps?.some((step) => step.status === "failed")).toBe(true);
    expect(result?.steps?.some((step) => step.status === "skipped")).toBe(true);
    expect(calls.at(-1)).toBe(failure);
    expect(
      calls.some((c) => c.startsWith("pkexec") || c.startsWith("nix-env")),
    ).toBe(false);
    expect(new Manager(config).history()[0].status).toBe("failed");
  });
test("operation events are ordered and persist step outcomes", async () => {
  const config = setup();
  const manager = new Manager(config, undefined, () =>
    Effect.succeed({ code: 0, stdout: "", stderr: "" }),
  );
  const events: Array<{
    sequence: number;
    type: string;
    operation?: { status: string; steps?: Array<{ status: string }> };
  }> = [];
  const unsubscribe = manager.onEvent((event) => events.push(event));
  manager.start(request({ action: "build", scope: "home" }));
  const operation = await manager.wait();
  unsubscribe();
  expect(operation?.status).toBe("succeeded");
  expect(operation?.steps).toEqual([
    expect.objectContaining({
      label: "Build home configuration",
      status: "succeeded",
    }),
  ]);
  expect(events.length).toBeGreaterThanOrEqual(3);
  expect(
    events.some(
      (event) =>
        event.operation?.status === "running" &&
        event.operation.steps?.some((step) => step.status === "running"),
    ),
  ).toBe(true);
  expect(events.map((event) => event.sequence)).toEqual(
    events.map((event) => event.sequence).slice().sort((a, b) => a - b),
  );
});
test("history remains compatible with records that predate step outcomes", () => {
  const config = setup();
  const directory = join(config.stateDir, "operations");
  mkdirSync(directory, { recursive: true });
  writeFileSync(
    join(directory, "1000000000000-old.json"),
    JSON.stringify({
      id: "1000000000000-old",
      pid: process.pid,
      flake: config.flake,
      request: request(),
      status: "succeeded",
      startedAt: new Date(0).toISOString(),
      finishedAt: new Date(1).toISOString(),
      step: "Complete",
    }),
  );
  const [operation] = new Manager(config).history();
  expect(operation.status).toBe("succeeded");
  expect(operation.steps).toBeUndefined();
});
test("generation inventory marks the active home output", () => {
  const config = setup();
  const first = join(config.flake, "generation-one");
  const second = join(config.flake, "generation-two");
  mkdirSync(first);
  mkdirSync(second);
  rmSync(config.homeProfile);
  symlinkSync(second, config.homeProfile);
  symlinkSync(first, `${config.homeProfile}-1-link`);
  symlinkSync(second, `${config.homeProfile}-2-link`);
  const state = advancedState(config);
  expect(state.generations.home.map((generation) => generation.number)).toEqual([
    2, 1,
  ]);
  expect(state.generations.home[0].active).toBe(true);
  expect(state.generations.home[1].active).toBe(false);
  const review = generationReview(config, "home", 1);
  expect(realpathSync(join(reviewPath(config, review.id), "home"))).toBe(first);
});
test("reviewed builds retain and later apply the exact outputs", async () => {
  const config = setup();
  const fingerprint = repositoryState(config.flake).fingerprint;
  const outputs = {
    home: join(config.flake, "home-output"),
    system: join(config.flake, "system-output"),
  };
  mkdirSync(outputs.home);
  mkdirSync(outputs.system);
  const manager = new Manager(config, undefined, (command) =>
    Effect.sync(() => {
      const outLink = command.argv[command.argv.indexOf("--out-link") + 1];
      symlinkSync(command.label.includes("home") ? outputs.home : outputs.system, outLink);
      return { code: 0, stdout: "", stderr: "" };
    }),
  );
  manager.start(
    request({
      action: "review-build",
      reviewId: "aaaaaaaa",
      fingerprint,
    }),
  );
  expect((await manager.wait())?.status).toBe("succeeded");
  const [review] = reviews(config);
  expect(review.homeOutput).toBe(outputs.home);
  expect(review.systemOutput).toBe(outputs.system);
  const apply = plan(
    config,
    request({
      action: "review-apply",
      reviewId: review.id,
      fingerprint,
      homeOutput: review.homeOutput,
      systemOutput: review.systemOutput,
    }),
  );
  expect(apply.steps[0].argv).toContain(outputs.home);
  expect(apply.steps.at(-1)?.argv).toContain(outputs.system);
});
test("cleanup retention is validated and included in both profile plans", () => {
  const config = setup();
  const cleanup = plan(config, request({ action: "gc", retention: 7 }));
  expect(cleanup.steps[0].argv).toContain("+7");
  expect(cleanup.steps[1].argv.at(-1)).toBe("7");
  expect(() => plan(config, request({ action: "gc", retention: 0 }))).toThrow(
    "Retention",
  );
});
test("an input proposal applies its exact lock and rejects stale checkouts", () => {
  const config = setup();
  const lock = join(config.flake, "flake.lock");
  writeFileSync(lock, '{"nodes":{"root":{}}}');
  const fingerprint = repositoryState(config.flake).fingerprint;
  const id = "bbbbbbbb";
  const root = join(config.stateDir, "input-proposals", id);
  mkdirSync(root, { recursive: true });
  writeFileSync(join(root, "flake.lock"), '{"nodes":{"root":{},"nixpkgs":{}}}');
  writeFileSync(
    join(root, "proposal.json"),
    JSON.stringify({ id, fingerprint, createdAt: new Date().toISOString(), inputs: ["nixpkgs"], changes: [] }),
  );
  applyInputProposal(config, id, fingerprint);
  expect(readFileSync(lock, "utf8")).toContain("nixpkgs");
  expect(() => applyInputProposal(config, id, fingerprint)).toThrow("changed");
});
test("dry run does not invoke any external command", async () => {
  const manager = new Manager(setup(), undefined, () => {
    throw new Error("Must not run");
  });
  manager.start(
    request({ action: "switch", update: true, cleanup: true, dryRun: true }),
  );
  expect((await manager.wait())?.status).toBe("succeeded");
});
test("flock excludes other manager instances and releases after completion", async () => {
  const config = setup();
  const release = acquireLock(config.stateDir);
  expect(() => new Manager(config).start(request())).toThrow("Another");
  release();
  const next = acquireLock(config.stateDir);
  next();
});
test("cancellation interrupts a command and releases the operation lock", async () => {
  const config = setup();
  const manager = new Manager(config, undefined, () => Effect.never);
  manager.start(request());
  await Bun.sleep(20);
  manager.cancel();
  expect((await manager.wait())?.status).toBe("cancelled");
  const release = acquireLock(config.stateDir);
  release();
});
test("cancellation waits for privileged activation and prevents cleanup", async () => {
  const config = setup();
  let started!: () => void, complete!: () => void;
  const entered = new Promise<void>((r) => {
    started = r;
  });
  const finish = new Promise<void>((r) => {
    complete = r;
  });
  const calls: Command[] = [];
  const manager = new Manager(config, undefined, (c) => {
    calls.push(c);
    if (c.argv.includes("--privileged"))
      return Effect.promise(async () => {
        started();
        await finish;
        return { code: 0, stdout: "", stderr: "" };
      });
    return Effect.succeed({ code: 0, stdout: "", stderr: "" });
  });
  manager.start(request({ action: "switch", scope: "system", cleanup: true }));
  await entered;
  const pending = manager.wait();
  manager.cancel();
  await Bun.sleep(15);
  expect(manager.busy).toBe(true);
  expect(() => acquireLock(config.stateDir)).toThrow("Another");
  complete();
  expect((await pending)?.status).toBe("cancelled");
  expect(calls).toHaveLength(2);
  expect(calls.some((c) => c.argv.includes("gc"))).toBe(false);
});
test("an output sink failure terminates the child before returning an error", async () => {
  let pid = 0;
  const exit = await Effect.runPromiseExit(
    runProcess(
      {
        label: "output failure",
        argv: [
          process.execPath,
          "-e",
          "console.log(process.pid); setInterval(() => {}, 1000);",
        ],
      },
      (e) => {
        pid = Number(e.text.trim());
        throw new Error("Disk full");
      },
    ),
  );
  expect(exit._tag).toBe("Failure");
  expect(() => process.kill(pid, 0)).toThrow();
});
test("runner preserves argv and drains both output streams", async () => {
  const literal = "$(touch should-not-exist); spaces ' quotes";
  const result = await Effect.runPromise(
    runProcess({
      label: "fixture",
      argv: [
        process.execPath,
        "-e",
        "console.log(process.argv[1]); console.error('stderr');",
        literal,
      ],
    }),
  );
  expect(result.stdout.trim()).toBe(literal);
  expect(result.stderr.trim()).toBe("stderr");
});
test("runner interruption terminates the entire child process group", async () => {
  const controller = new AbortController();
  let pid = 0,
    announced!: () => void;
  const ready = new Promise<void>((resolve) => {
    announced = resolve;
  });
  const run = Effect.runPromiseExit(
    runProcess(
      {
        label: "long command",
        argv: [
          process.execPath,
          "-e",
          "const c = Bun.spawn([process.execPath, '-e', 'setInterval(() => {}, 1000)']); console.log(c.pid); setInterval(() => {}, 1000);",
        ],
      },
      (e) => {
        if (e.stream === "stdout") {
          pid = Number(e.text.trim());
          announced();
        }
      },
    ),
    { signal: controller.signal },
  );
  await ready;
  controller.abort();
  await run;
  expect(pid).toBeGreaterThan(0);
  // A reparented zombie is already dead; /proc disappears once init reaps it.
  const status = await Bun.file(`/proc/${pid}/stat`)
    .text()
    .catch(() => "");
  expect(status === "" || status.includes(") Z ")).toBe(true);
}, 8000);
