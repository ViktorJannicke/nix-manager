import { afterEach, expect, test } from "bun:test";
import { existsSync, mkdirSync, readFileSync, realpathSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
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
import { generations, hashText, repositoryState } from "../src/observe";
import { getReview, listReviews, writeReviewedLock } from "../src/reviews";
import { buildRoot, reviewRoot } from "../src/domain";

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
  const root = buildRoot(config);
  expect(p.steps.map((s) => s.argv.slice(0, 2).join(" "))).toEqual([
    "nix flake",
    "nix build",
    "nix build",
    "nix-env --profile",
    `${root}/home/activate --driver-version`,
    "pkexec /immutable/bin/nix-manager",
    "nix-env --profile",
    "pkexec /immutable/bin/nix-manager",
  ]);
  // Activation uses exactly the outputs the build steps produced.
  expect(p.steps[1].argv).toContain(`${root}/home`);
  expect(p.steps[2].argv).toContain(`${root}/system`);
  expect(p.steps[3].argv).toEqual(["nix-env", "--profile", config.homeProfile, "--set", `${root}/home`]);
  expect(p.steps[4].env).toEqual({ HOME_MANAGER_BACKUP_EXT: "hm-backup" });
  expect(p.steps[5].argv.slice(-3)).toEqual(["activate-output", "switch", `${root}/system`]);
  expect(p.steps[6].argv).toContain(config.homeProfile);
  expect(p.steps.filter((s) => s.progress)).toHaveLength(2);
  expect(p.steps[1].argv).toContain(
    `${config.flake}#homeConfigurations.user-test.activationPackage`,
  );
  // Names that are not plain Nix identifiers are quoted in the flake fragment.
  const quoted = plan(
    { ...config, hosts: { test: { ...config.hosts.test, homeConfiguration: "user@test" } } },
    request({ action: "build", scope: "home" }),
  );
  expect(quoted.steps[0].argv).toContain(
    `${config.flake}#homeConfigurations."user@test".activationPackage`,
  );
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
  "Build home configuration",
  "Build system configuration",
  "Activate Home Manager",
])
  test(`${failure} failure prevents subsequent activation/cleanup`, async () => {
    const config = setup(),
      calls: string[] = [];
    const run = (c: Command) => {
      const key = c.label;
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
    expect(calls.some((c) => c.startsWith("Activate system") || c.startsWith("Keep"))).toBe(
      false,
    );
    expect(existsSync(buildRoot(config))).toBe(false);
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
test("generation inventory marks the current home output", async () => {
  const config = setup();
  const first = join(config.flake, "generation-one");
  const second = join(config.flake, "generation-two");
  mkdirSync(first);
  mkdirSync(second);
  rmSync(config.homeProfile);
  symlinkSync(second, config.homeProfile);
  symlinkSync(first, `${config.homeProfile}-1-link`);
  symlinkSync(second, `${config.homeProfile}-2-link`);
  const found = await generations(config.homeProfile, realpathSync(config.homeProfile));
  expect(found.map((generation) => generation.number)).toEqual([2, 1]);
  expect(found[0].current).toBe(true);
  expect(found[1].current).toBe(false);
  const rollback = plan(
    config,
    request({ action: "review-build", scope: "home", generation: 1, reviewId: "cccccccc" }),
  );
  expect(rollback.steps[0].argv).toContain(`${config.homeProfile}-1-link`);
  expect(rollback.steps[0].argv).toContain(`${reviewRoot(config, "cccccccc")}/home`);
  expect(rollback.steps.at(-1)?.internal).toBe("compare");
  expect(() =>
    plan(config, request({ action: "review-build", generation: 1, reviewId: "cccccccc" })),
  ).toThrow("either");
});
test("reviewed builds retain, compare and later apply the exact outputs", async () => {
  const config = setup();
  writeFileSync(join(config.flake, "flake.lock"), '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a","lastModified":1}}}}');
  const outputs = {
    home: join(config.flake, "home-output"),
    system: join(config.flake, "system-output"),
  };
  mkdirSync(outputs.home);
  mkdirSync(outputs.system);
  const calls: Command[] = [];
  const manager = new Manager(config, undefined, (command) =>
    Effect.sync(() => {
      calls.push(command);
      if (command.argv.includes("--output-lock-file"))
        writeFileSync(
          command.argv[command.argv.indexOf("--output-lock-file") + 1],
          '{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"b","lastModified":2,"type":"github","owner":"NixOS","repo":"nixpkgs"}}}}',
        );
      if (command.argv.includes("--out-link"))
        symlinkSync(
          command.label.includes("home") ? outputs.home : outputs.system,
          command.argv[command.argv.indexOf("--out-link") + 1],
        );
      if (command.argv[2] === "diff-closures")
        return { code: 0, stdout: "firefox: 130.0 → 131.0, +12.0 KiB\n", stderr: "" };
      return { code: 0, stdout: "", stderr: "" };
    }),
  );
  manager.start(request({ action: "review-build", reviewId: "aaaaaaaa", inputs: ["nixpkgs"] }));
  const built = await manager.wait();
  expect(built?.status).toBe("succeeded");
  // The checkout's lock is untouched until the review is applied.
  expect(readFileSync(join(config.flake, "flake.lock"), "utf8")).toContain('"rev":"a"');
  const builds = calls.filter((c) => c.argv[1] === "build");
  expect(builds.every((c) => c.argv.includes("--reference-lock-file"))).toBe(true);
  const [review] = await listReviews(config);
  expect(review.source).toBe("inputs");
  expect(review.home?.path).toBe(outputs.home);
  expect(review.system?.path).toBe(outputs.system);
  expect(review.inputs?.changes).toEqual([
    expect.objectContaining({ name: "nixpkgs", after: expect.objectContaining({ rev: "b", source: "NixOS/nixpkgs" }) }),
  ]);
  const apply = manager.plan(request({ action: "review-apply", reviewId: review.id }));
  expect(apply.steps[0].internal).toBe("write-lock");
  expect(apply.steps[1].argv).toContain(outputs.home);
  expect(apply.steps.at(-1)?.argv).toContain(outputs.system);
  const boot = manager.plan(request({ action: "review-apply", reviewId: review.id, activation: "boot" }));
  expect(boot.steps.map((step) => step.label)).toEqual([
    "Write the reviewed flake.lock",
    "Activate system on next boot",
  ]);
  manager.start(request({ action: "review-apply", reviewId: review.id, scope: "home" }));
  expect((await manager.wait())?.status).toBe("succeeded");
  expect(readFileSync(join(config.flake, "flake.lock"), "utf8")).toContain('"rev":"b"');
  // Applied reviews release their GC roots; the profile now references them.
  expect(await listReviews(config)).toEqual([]);
});
test("a failed review build removes its partial outputs", async () => {
  const config = setup();
  const manager = new Manager(config, undefined, () =>
    Effect.fail(new ManagerError({ message: "evaluation error" })),
  );
  manager.start(request({ action: "review-build", reviewId: "dddddddd", scope: "home" }));
  expect((await manager.wait())?.status).toBe("failed");
  expect(existsSync(reviewRoot(config, "dddddddd"))).toBe(false);
});
test("the reviewed lock is written only if flake.lock is unchanged", async () => {
  const config = setup();
  const lock = join(config.flake, "flake.lock");
  writeFileSync(lock, '{"nodes":{"root":{}}}');
  const id = "bbbbbbbb";
  mkdirSync(reviewRoot(config, id), { recursive: true });
  writeFileSync(join(reviewRoot(config, id), "flake.lock"), '{"nodes":{"root":{},"nixpkgs":{}}}');
  const review = {
    id,
    host: "test",
    scope: "home" as const,
    createdAt: new Date().toISOString(),
    fingerprint: (await repositoryState(config.flake)).fingerprint,
    source: "inputs" as const,
    inputs: { names: ["nixpkgs"], changes: [], baseLockHash: hashText('{"nodes":{"root":{}}}') },
  };
  writeFileSync(join(reviewRoot(config, id), "review.json"), JSON.stringify(review));
  expect(getReview(config, id).id).toBe(id);
  await Effect.runPromise(writeReviewedLock(config, review));
  expect(readFileSync(lock, "utf8")).toContain("nixpkgs");
  await expect(Effect.runPromise(writeReviewedLock(config, review))).rejects.toThrow("changed");
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
        if (e.stream === "progress") return;
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
