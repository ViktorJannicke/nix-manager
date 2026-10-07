import {
  appendFileSync,
  closeSync,
  existsSync,
  mkdirSync,
  openSync,
  readFileSync,
  readdirSync,
  renameSync,
  unlinkSync,
  writeFileSync,
} from "node:fs";
import { join } from "node:path";
import { dlopen, FFIType } from "bun:ffi";
import { Cause, Effect, Exit, Layer, Option } from "effect";
import {
  type Config,
  type Request,
  type Command,
  ManagerError,
  plan,
  formatCommand,
  isPackage,
} from "./domain";
import { Runner, runProcess, type Output } from "./process";
import {
  checkUpdates,
  networkLayer,
  updatePackage,
  type UpdateReport,
  Network,
} from "./updates";
import type { ManagerEvent, Operation, StepOutcome } from "./contracts";
import { finalizeReview, markReviewApplied, reviewPath } from "./advanced";
export type { Operation } from "./contracts";
let libc: ReturnType<typeof openLibc> | undefined;
const openLibc = () =>
  dlopen("libc.so.6", {
    flock: { args: [FFIType.i32, FFIType.i32], returns: FFIType.i32 },
  });
export function acquireLock(stateDir: string): () => void {
  mkdirSync(stateDir, { recursive: true, mode: 0o700 });
  const fd = openSync(join(stateDir, "operation.lock"), "a", 0o600);
  libc ??= openLibc();
  if (libc.symbols.flock(fd, 2 | 4) !== 0) {
    closeSync(fd);
    throw new ManagerError({
      message:
        "Another nix-manager operation is running. Wait for it to finish.",
    });
  }
  // Closing releases flock, including on crashes. Never unlink the lock inode.
  return () => closeSync(fd);
}
const atomicJson = (path: string, data: unknown) => {
  const temp = `${path}.${crypto.randomUUID()}.tmp`;
  try {
    writeFileSync(temp, JSON.stringify(data, null, 2) + "\n", { mode: 0o600 });
    renameSync(temp, path);
  } finally {
    if (existsSync(temp)) unlinkSync(temp);
  }
};
const alive = (pid: number) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
};
export class Manager {
  readonly config: Config;
  private current?: {
    operation: Operation;
    controller: AbortController;
    promise: Promise<Operation>;
  };
  private listeners = new Set<() => void>();
  private eventListeners = new Set<(event: ManagerEvent) => void>();
  private sequence = 0;
  private logBytes = 0;
  private lastPersist = 0;
  constructor(
    config: Config,
    private readonly output: (event: Output) => void = () => {},
    private readonly run = runProcess,
    private readonly network: Layer.Layer<Network> = networkLayer,
  ) {
    this.config = config;
  }
  onChange(listener: () => void) {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }
  onEvent(listener: (event: ManagerEvent) => void) {
    this.eventListeners.add(listener);
    return () => {
      this.eventListeners.delete(listener);
    };
  }
  private event(
    event:
      | Omit<
          Extract<ManagerEvent, { type: "operation" }>,
          "sequence" | "timestamp"
        >
      | Omit<
          Extract<ManagerEvent, { type: "output" }>,
          "sequence" | "timestamp"
        >,
  ) {
    const next = {
      ...event,
      sequence: ++this.sequence,
      timestamp: new Date().toISOString(),
    } as ManagerEvent;
    for (const listener of this.eventListeners) {
      try {
        listener(next);
      } catch (e) {
        console.error(e);
      }
    }
  }
  private changed(operation = this.current?.operation) {
    for (const listener of this.listeners) {
      try {
        listener();
      } catch (e) {
        console.error(e);
      }
    }
    if (operation)
      this.event({ type: "operation", operation: structuredClone(operation) });
  }
  private path(id: string, extension: string) {
    if (!/^[0-9]+-[a-f0-9-]+$/.test(id))
      throw new ManagerError({ message: "Invalid operation ID." });
    return join(this.config.stateDir, "operations", `${id}.${extension}`);
  }
  history(): Operation[] {
    if (!existsSync(join(this.config.stateDir, "operations"))) return [];
    return readdirSync(join(this.config.stateDir, "operations"))
      .filter((n) => n.endsWith(".json"))
      .sort()
      .reverse()
      .slice(0, 50)
      .flatMap((file) => {
        try {
          const op: Operation = JSON.parse(
            readFileSync(
              join(this.config.stateDir, "operations", file),
              "utf8",
            ),
          );
          if (op.flake !== this.config.flake) return [];
          if (op.status === "running" && !alive(op.pid))
            return [
              {
                ...op,
                status: "interrupted" as const,
                error:
                  "The manager exited before recording completion. Check the log and active generations before retrying.",
              },
            ];
          return [op];
        } catch {
          return [];
        }
      });
  }
  log(id: string) {
    return existsSync(this.path(id, "log"))
      ? readFileSync(this.path(id, "log"), "utf8").slice(-128_000)
      : "";
  }
  updates(): UpdateReport | undefined {
    try {
      const saved = JSON.parse(
        readFileSync(join(this.config.stateDir, "updates.json"), "utf8"),
      );
      return saved.flake === this.config.flake ? saved.report : undefined;
    } catch {
      return undefined;
    }
  }
  get busy() {
    return this.current !== undefined;
  }
  get active() {
    return this.current?.operation;
  }
  start(r: Request): Operation {
    const preview = plan(this.config, r);
    if (this.busy)
      throw new ManagerError({ message: "An operation is already running." });
    const release = acquireLock(this.config.stateDir);
    const operation: Operation = {
      id: `${Date.now()}-${crypto.randomUUID()}`,
      pid: process.pid,
      flake: this.config.flake,
      request: r,
      status: "running",
      startedAt: new Date().toISOString(),
      step: "Preparing",
      steps: (preview.steps.length
        ? preview.steps.map((command) => command.label)
        : [
            r.action === "check"
              ? "Check inputs and package releases"
              : `Update ${r.action.replace("-update", "")} package pin`,
          ]
      ).map((label, index): StepOutcome => ({
        id: `${index + 1}-${label.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "")}`,
        label,
        status: "pending",
      })),
    };
    const controller = new AbortController();
    this.logBytes = 0;
    const persist = () =>
      atomicJson(this.path(operation.id, "json"), operation);
    try {
      mkdirSync(join(this.config.stateDir, "operations"), {
        recursive: true,
        mode: 0o700,
      });
      if (r.action === "review-build")
        mkdirSync(reviewPath(this.config, r.reviewId!), {
          recursive: true,
          mode: 0o700,
        });
      persist();
    } catch (error) {
      release();
      throw error;
    }
    const output = (event: Output) => {
      // Keep bounded logs on disk; the command runner separately retains a tail
      // for hash discovery and errors. UI output is always rendered as text.
      if (this.logBytes < 16 * 1024 * 1024) {
        const text = event.text.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, "");
        appendFileSync(this.path(operation.id, "log"), text, { mode: 0o600 });
        this.logBytes += Buffer.byteLength(text);
        if (this.logBytes >= 16 * 1024 * 1024)
          appendFileSync(
            this.path(operation.id, "log"),
            "\n[Log limit reached: 16 MiB]\n",
          );
      }
      this.output(event);
      this.event({
        type: "output",
        operationId: operation.id,
        stream: event.stream,
        text: event.text.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, ""),
      });
    };
    const log = (text: string) =>
      output({ stream: "stdout", text: `→ ${text}\n` });
    const startStep = (label: string) => {
      const step = operation.steps?.find(
        (candidate) => candidate.label === label && candidate.status === "pending",
      );
      if (step) {
        step.status = "running";
        step.startedAt = new Date().toISOString();
      }
      operation.step = label;
      persist();
      this.changed(operation);
      return step;
    };
    const finishStep = (
      step: StepOutcome | undefined,
      status: StepOutcome["status"],
    ) => {
      if (step) {
        step.status = status;
        step.finishedAt = new Date().toISOString();
      }
      persist();
      this.changed(operation);
    };
    const runLayer = Layer.succeed(Runner, {
      run: (c: Command) =>
        Effect.gen(this, function* () {
          const outcome = startStep(c.label);
          log(`${c.label}\n  ${formatCommand(c)}`);
          if (Date.now() - this.lastPersist > 500) {
            persist();
            this.lastPersist = Date.now();
          }
          // An unprivileged process cannot signal root's rebuild descendants.
          // Defer cancellation until that command exits; keep the lock throughout.
          if (
            c.argv[0] === this.config.privileged[0] &&
            c.argv.includes("--privileged")
          ) {
            operation.finishingPrivilegedStep = true;
            persist();
            this.changed();
            const result = yield* this.run(c, output).pipe(
              Effect.uninterruptible,
              Effect.ensuring(
                Effect.sync(() => {
                  operation.finishingPrivilegedStep = false;
                }),
              ),
            );
            finishStep(outcome, "succeeded");
            return result;
          }
          const result = yield* this.run(c, output);
          finishStep(outcome, "succeeded");
          return result;
        }),
    });
    const program = Effect.gen(this, function* () {
      for (const note of preview.notes) log(note);
      if (r.action === "check") {
        const outcome = startStep("Check inputs and package releases");
        if (r.dryRun) {
          log(
            "Would check inputs and package releases without changing the checkout.",
          );
          finishStep(outcome, "succeeded");
          return;
        }
        const report = yield* checkUpdates(this.config);
        yield* Effect.try({
          try: () =>
            atomicJson(join(this.config.stateDir, "updates.json"), {
              flake: this.config.flake,
              report,
            }),
          catch: (e) =>
            new ManagerError({ message: `Save update report: ${e}` }),
        });
        for (const item of report.items)
          log(`${item.name}: ${item.current} → ${item.latest}`);
        for (const error of report.errors) log(`Check failed: ${error}`);
        log(
          `${report.items.length} updates found${report.errors.length ? `; ${report.errors.length} checks failed` : ""}.`,
        );
        if (report.errors.length)
          return yield* Effect.fail(
            new ManagerError({
              message:
                "Some update checks failed. The report contains the successful results and errors.",
            }),
          );
        finishStep(outcome, "succeeded");
      } else if (isPackage(r.action)) {
        const outcome = startStep(
          `Update ${r.action.replace("-update", "")} package pin`,
        );
        yield* updatePackage(this.config, r, log);
        finishStep(outcome, "succeeded");
      }
      else
        for (const step of preview.steps) {
          if (r.dryRun) {
            const outcome = startStep(step.label);
            log(formatCommand(step));
            finishStep(outcome, "succeeded");
          }
          else yield* (yield* Runner).run(step);
        }
    }).pipe(Effect.provide(Layer.merge(runLayer, this.network)));
    const promise = Promise.resolve().then(async () => {
      try {
        const exit = await Effect.runPromiseExit(program, {
          signal: controller.signal,
        });
        if (Exit.isSuccess(exit)) {
          if (r.action === "review-build") finalizeReview(this.config, r);
          if (r.action === "review-apply")
            markReviewApplied(this.config, r.reviewId!);
          operation.status = "succeeded";
          operation.step = r.dryRun ? "Preview complete" : "Complete";
        } else {
          operation.status = Cause.isInterruptedOnly(exit.cause)
            ? "cancelled"
            : "failed";
          const failure = Cause.failureOption(exit.cause);
          operation.error =
            operation.status === "cancelled"
              ? "Cancelled. Any completed activation or input update remains applied."
              : Option.isSome(failure)
                ? failure.value.message
                : Cause.pretty(exit.cause);
          for (const step of operation.steps || []) {
            if (step.status === "running") {
              step.status =
                operation.status === "cancelled" ? "cancelled" : "failed";
              step.finishedAt = new Date().toISOString();
            } else if (step.status === "pending") step.status = "skipped";
          }
          log(operation.error);
        }
      } catch (e) {
        operation.status = "failed";
        operation.error = String(e);
        for (const step of operation.steps || []) {
          if (step.status === "running") step.status = "failed";
          else if (step.status === "pending") step.status = "skipped";
        }
      } finally {
        operation.finishedAt = new Date().toISOString();
        try {
          persist();
        } finally {
          release();
          this.current = undefined;
          this.changed(operation);
        }
      }
      return operation;
    });
    this.current = { operation, controller, promise };
    this.changed(operation);
    return operation;
  }
  async wait() {
    return this.current?.promise;
  }
  cancel() {
    if (this.current) {
      this.current.operation.step = this.current.operation.finishingPrivilegedStep
        ? "Stopping after privileged step"
        : "Cancelling";
      this.changed(this.current.operation);
    }
    this.current?.controller.abort();
  }
  async close() {
    this.cancel();
    await this.wait();
  }
}
