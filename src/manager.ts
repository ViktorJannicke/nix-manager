import {
  appendFileSync,
  closeSync,
  existsSync,
  mkdirSync,
  openSync,
  readFileSync,
  readSync,
  readdirSync,
  renameSync,
  rmSync,
  statSync,
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
  buildRoot,
  plan,
  formatCommand,
  isPackage,
  reviewRoot,
} from "./domain";
import { Runner, runProcess, type Output } from "./process";
import {
  checkUpdates,
  networkLayer,
  updatePackage,
  type UpdateReport,
  Network,
} from "./updates";
import type {
  BuildReview,
  LogResult,
  ManagerEvent,
  Operation,
  StepOutcome,
} from "./contracts";
import { compareReview, getReview, removeReview, writeReviewedLock, type ReviewBase } from "./reviews";
import { readLock, repositoryState } from "./observe";
import { stripAnsi } from "./nix";
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
const LOG_LIMIT = 16 * 1024 * 1024;
const LOG_TAIL = 256 * 1024;
type EventBody =
  | Omit<Extract<ManagerEvent, { type: "operation" }>, "sequence" | "timestamp">
  | Omit<Extract<ManagerEvent, { type: "output" }>, "sequence" | "timestamp">
  | Omit<Extract<ManagerEvent, { type: "progress" }>, "sequence" | "timestamp">;
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
  private historyCache?: { key: string; operations: Operation[] };
  constructor(
    config: Config,
    private readonly output: (event: Output) => void = () => {},
    private readonly run: (
      command: Command,
      output?: (event: Output) => void,
    ) => Effect.Effect<import("./process").Result, ManagerError> = runProcess,
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
  private event(event: EventBody) {
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
    const directory = join(this.config.stateDir, "operations");
    if (!existsSync(directory)) return [];
    // Every persisted change renames a file into the directory, which
    // updates its modification time; reuse the parsed list until then.
    const key = `${statSync(directory).mtimeMs}`;
    if (this.historyCache?.key === key)
      return this.withLiveState(this.historyCache.operations);
    const operations = readdirSync(directory)
      .filter((n) => n.endsWith(".json"))
      .sort()
      .reverse()
      .slice(0, 50)
      .flatMap((file) => {
        try {
          const op: Operation = JSON.parse(readFileSync(join(directory, file), "utf8"));
          return op.flake === this.config.flake ? [op] : [];
        } catch {
          return [];
        }
      });
    this.historyCache = { key, operations };
    return this.withLiveState(operations);
  }
  private withLiveState(operations: Operation[]) {
    return operations.map((op) =>
      op.status === "running" && op.id !== this.current?.operation.id && !alive(op.pid)
        ? {
            ...op,
            status: "interrupted" as const,
            error:
              "The manager exited before recording completion. Check the log and active generations before retrying.",
          }
        : op.id === this.current?.operation.id
          ? this.current.operation
          : op,
    );
  }
  log(id: string, limit = LOG_TAIL): LogResult {
    const path = this.path(id, "log");
    if (!existsSync(path)) return { text: "", end: 0, truncated: false };
    const fd = openSync(path, "r");
    try {
      const size = statSync(path).size;
      const length = Math.min(size, limit);
      const buffer = Buffer.alloc(length);
      readSync(fd, buffer, 0, length, size - length);
      let text = buffer.toString("utf8");
      // Drop a partial first line (and any split UTF-8 sequence) from the tail.
      if (length < size) text = text.slice(text.indexOf("\n") + 1);
      return { text, end: size, truncated: length < size };
    } finally {
      closeSync(fd);
    }
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
  /** Completes a request: new review IDs and the stored review to apply. */
  resolve(r: Request): { request: Request; review?: BuildReview } {
    if (r.action === "review-build" && !r.reviewId)
      return { request: { ...r, reviewId: crypto.randomUUID() } };
    if (r.action === "review-apply" && r.reviewId)
      return { request: r, review: getReview(this.config, r.reviewId) };
    return { request: r };
  }
  plan(r: Request) {
    const { request, review } = this.resolve(r);
    return plan(this.config, request, review);
  }
  start(input: Request): Operation {
    const { request: r, review } = this.resolve(input);
    const preview = plan(this.config, r, review);
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
    let logBytes = 0;
    let lastPersist = 0;
    const persist = () => {
      lastPersist = Date.now();
      atomicJson(this.path(operation.id, "json"), operation);
    };
    try {
      mkdirSync(join(this.config.stateDir, "operations"), {
        recursive: true,
        mode: 0o700,
      });
      if (r.action === "review-build")
        mkdirSync(reviewRoot(this.config, r.reviewId!), { recursive: true, mode: 0o700 });
      if (r.action === "switch" || r.action === "boot") {
        rmSync(buildRoot(this.config), { recursive: true, force: true });
        mkdirSync(buildRoot(this.config), { recursive: true, mode: 0o700 });
      }
      persist();
    } catch (error) {
      release();
      throw error;
    }
    let runningStep: StepOutcome | undefined;
    const output = (event: Output) => {
      this.output(event);
      if (event.stream === "progress") {
        if (!runningStep) return;
        runningStep.progress = event.progress;
        this.event({
          type: "progress",
          operationId: operation.id,
          stepId: runningStep.id,
          progress: event.progress,
        });
        if (Date.now() - lastPersist > 2_000) persist();
        return;
      }
      // Keep bounded logs on disk; the command runner separately retains a tail
      // for hash discovery and errors. Byte offsets let clients splice the
      // live stream onto a fetched log tail without gaps or duplicates.
      if (logBytes >= LOG_LIMIT) return;
      let text = stripAnsi(event.text).replace(/\r(?!\n)/g, "\n");
      if (logBytes + Buffer.byteLength(text) >= LOG_LIMIT)
        text += "\n[Log limit reached: 16 MiB]\n";
      appendFileSync(this.path(operation.id, "log"), text, { mode: 0o600 });
      const offset = logBytes;
      logBytes += Buffer.byteLength(text);
      this.event({
        type: "output",
        operationId: operation.id,
        stream: event.stream,
        text,
        offset,
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
      runningStep = step;
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
        if (step.progress) step.progress = { ...step.progress, current: [] };
      }
      runningStep = undefined;
      persist();
      this.changed(operation);
    };
    const quiet = (c: Command) => this.run({ ...c, quiet: true }, () => {});
    const runLayer = Layer.succeed(Runner, {
      run: (c: Command) =>
        Effect.gen(this, function* () {
          const outcome = startStep(c.label);
          log(`${c.label}\n  ${formatCommand(c)}`);
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
        return;
      }
      if (isPackage(r.action)) {
        const outcome = startStep(
          `Update ${r.action.replace("-update", "")} package pin`,
        );
        yield* updatePackage(this.config, r, log);
        finishStep(outcome, "succeeded");
        return;
      }
      // The checkout state a review is evaluated from; captured before any
      // command reads it, so later edits make the review visibly stale.
      let base: ReviewBase | undefined;
      if (r.action === "review-build" && !r.dryRun)
        base = yield* Effect.promise(async () => ({
          fingerprint: (await repositoryState(this.config.flake)).fingerprint,
          lock: await readLock(this.config.flake),
        }));
      for (const step of preview.steps) {
        if (r.dryRun) {
          const outcome = startStep(step.label);
          log(formatCommand(step));
          finishStep(outcome, "succeeded");
        } else if (step.internal) {
          const outcome = startStep(step.label);
          if (step.internal === "compare") {
            const result = yield* compareReview(this.config, r, base!, operation.id, quiet);
            const packages = (result.home?.changes.length ?? 0) + (result.system?.changes.length ?? 0);
            log(`${packages} package changes are ready for review.`);
          } else yield* writeReviewedLock(this.config, review!);
          finishStep(outcome, "succeeded");
        } else yield* (yield* Runner).run(step);
      }
    }).pipe(Effect.provide(Layer.merge(runLayer, this.network)));
    const promise = Promise.resolve().then(async () => {
      try {
        const exit = await Effect.runPromiseExit(program, {
          signal: controller.signal,
        });
        if (Exit.isSuccess(exit)) {
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
          // Unfinished reviews would otherwise retain partial outputs, and a
          // review's GC roots are redundant once its profiles reference them.
          if (r.reviewId && !r.dryRun) {
            if (r.action === "review-build" && operation.status !== "succeeded")
              removeReview(this.config, r.reviewId);
            if (r.action === "review-apply" && operation.status === "succeeded")
              removeReview(this.config, r.reviewId);
          }
          if (r.action === "switch" || r.action === "boot")
            rmSync(buildRoot(this.config), { recursive: true, force: true });
        } catch (error) {
          console.error(error);
        }
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
