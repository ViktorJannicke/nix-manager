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
import { realpath } from "node:fs/promises";
import { join } from "node:path";
import { dlopen, FFIType } from "bun:ffi";
import { Cause, Effect, Either, Exit, Layer, Option } from "effect";
import {
  type Config,
  type Request,
  type Command,
  ManagerError,
  buildRoot,
  plan,
  request,
  formatCommand,
  goBackSteps,
  isPackage,
  reviewRoot,
  systemProfile,
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
  UndoContents,
  UndoPoint,
} from "./contracts";
import { compareReview, forgetBoot, getReview, keepForBoot, listReviews, removeReview, writeReviewedLock, type ReviewBase } from "./reviews";
import { profileGeneration, readLock, repositoryState } from "./observe";
import { readDraft, writeDraft } from "./draft";
import { describeChange, oneStepReviewProblem } from "./changes";
import { readSources, remaining, restoreSources, stageSources, undoFiles, verifyEdits, writeSources, type Staged } from "./edits";
import type { ReviewEdits } from "./contracts";
import { stripAnsi } from "./nix";
import { recordChange } from "./history";
import { readPreferences } from "./preferences";
export type { Operation } from "./contracts";

const stepId = (index: number, label: string) =>
  `${index + 1}-${label.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "")}`;

/** What applying a review changes, in words. */
const describeReview = (review: BuildReview): string[] => {
  const changes = [
    ...(review.edits?.changes.map((change) => describeChange(change).title) ?? []),
    ...(review.inputs ? [review.inputs.names.length ? `Update ${review.inputs.names.join(", ")}` : "Update all inputs"] : []),
    ...(review.undo ? [`Undo: ${review.undo.point.changes.join("; ")}`] : []),
    ...(review.source === "generation" && review.generation !== undefined && !review.undo
      ? [`Go back to restore point ${review.generation} of ${review.scope === "home" ? "your user" : "the system"}`]
      : []),
  ];
  return changes.length ? changes : ["Apply the configuration"];
};
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
export const atomicJson = (path: string, data: unknown) => {
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
  /** The undo point an applied change recorded, with the earlier file contents. */
  private undoPoint(id: string) {
    const operation = this.history().find((op) => op.id === id);
    if (!operation?.undo)
      throw new ManagerError({ message: "This change cannot be undone: it recorded nothing to go back to." });
    const path = this.path(id, "undo");
    const contents: UndoContents = existsSync(path) ? JSON.parse(readFileSync(path, "utf8")) : {};
    return { operation, point: operation.undo, contents };
  }
  /** Completes a request: new review IDs, the stored review to apply, and the change to undo. */
  resolve(r: Request): { request: Request; review?: BuildReview; undo?: { operation: Operation; point: UndoPoint } } {
    if (r.action === "review-build" && r.undo) {
      const { operation, point } = this.undoPoint(r.undo);
      // An undo covers what the change applied, on the host it applied to.
      return {
        request: { ...r, scope: operation.request.scope, host: operation.request.host, reviewId: r.reviewId ?? crypto.randomUUID() },
        undo: { operation, point },
      };
    }
    if (r.action === "review-build" && !r.reviewId)
      return { request: { ...r, reviewId: crypto.randomUUID() } };
    if (r.action === "review-apply" && r.reviewId)
      return { request: r, review: getReview(this.config, r.reviewId) };
    return { request: r };
  }
  plan(r: Request) {
    const { request, review, undo } = this.resolve(r);
    return plan(this.config, request, review, undo?.point);
  }
  start(input: Request): Operation {
    const { request: r, review, undo } = this.resolve(input);
    const preview = plan(this.config, r, review, undo?.point);
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
      ).map((label, index): StepOutcome => ({ id: stepId(index, label), label, status: "pending" })),
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
    // Set when an activation succeeds, so the change can be undone.
    let undoable: (Pick<UndoPoint, "generations" | "files"> & { contents: UndoContents }) | undefined;
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
      // The draft as it was when the review started; later edits wait for the next one.
      const draft = r.edits ? readDraft(this.config) : [];
      let staged: Staged | undefined;
      let edits: ReviewEdits | undefined;
      // What applying replaces, recorded so the change can be undone.
      const replaced: { files: UndoPoint["files"]; contents: UndoContents } = { files: [], contents: {} };
      const record = (before: Map<string, string | undefined>) => {
        const { files, contents } = undoFiles(this.config, before);
        replaced.files.push(...files);
        Object.assign(replaced.contents, contents);
      };
      const generations: UndoPoint["generations"] = {};
      if (r.action === "review-apply" && !r.dryRun) {
        if (r.scope !== "system" && review?.home)
          generations.home = yield* Effect.promise(() => profileGeneration(this.config.homeProfile));
        if (r.scope !== "home" && review?.system)
          generations.system = yield* Effect.promise(() => profileGeneration(systemProfile));
      }
      // After a failed apply the earlier generations become active again and
      // the written files are put back, so the computer is as it was before.
      const goBack = (error: ManagerError) =>
        Effect.gen(this, function* () {
          const failed = operation.steps!.find((step) => step.status === "running");
          const touched = (prefix: string) => operation.steps!.some((step) => step.label.startsWith(prefix) && step.status !== "pending");
          const home = touched("Select Home Manager");
          const back = goBackSteps(this.config, {
            system: touched("Activate system") ? generations.system : undefined,
            home: home ? generations.home : undefined,
          });
          const files = replaced.files.length > 0;
          if (!back.length && !files) return yield* Effect.fail(error);
          finishStep(failed, "failed");
          const putBack = "Put back the configuration files";
          const labels = [...back.map((step) => step.label), ...(files ? [putBack] : [])];
          operation.steps!.push(
            ...labels.map((label, index): StepOutcome => ({ id: stepId(operation.steps!.length + index, label), label, status: "pending" })),
          );
          log("Going back to how things were before.");
          // Each part goes back on its own: files are put back even when a generation could not be activated.
          const parts: Effect.Effect<unknown, ManagerError, Runner>[] = back.map((step) => Effect.flatMap(Runner, (runner) => runner.run(step)));
          if (files)
            parts.push(
              Effect.gen(this, function* () {
                const step = startStep(putBack);
                const restored = yield* Effect.either(
                  restoreSources(this.config, { changes: [], generations, files: replaced.files }, replaced.contents).pipe(
                    Effect.provide(Layer.succeed(Runner, { run: quiet })),
                  ),
                );
                finishStep(step, Either.isRight(restored) ? "succeeded" : "failed");
                if (Either.isLeft(restored)) return yield* Effect.fail(restored.left);
              }),
            );
          const results = yield* Effect.forEach(parts, (part) => Effect.either(part));
          const outcome = results.find(Either.isLeft) ?? Either.right(undefined);
          // Without an earlier home generation the new one stays selected, so only the rest went back.
          const first = home && generations.home === undefined;
          operation.rollback = Either.isRight(outcome) ? { done: !first } : { done: false, error: outcome.left.message };
          const what = error.explanation?.body ?? error.message;
          return yield* Effect.fail(
            new ManagerError({
              message: error.message,
              explanation: {
                title: error.explanation?.title ?? `${failed?.label ?? "Applying"} failed`,
                body: Either.isRight(outcome)
                  ? first
                    ? `${what} ${files ? "The files were put back, but there" : "There"} was no earlier restore point of your user to go back to, so the new version stays in use.`
                    : `${what} Everything went back to how it was before${files ? ", files included" : ""}.`
                  : `${what} Going back did not work either: ${outcome.left.message} The Restore points page can take you back.`,
              },
            }),
          );
        });
      if (r.action === "review-build" && !r.dryRun)
        base = yield* Effect.promise(async () => ({
          fingerprint: (await repositoryState(this.config.flake)).fingerprint,
          lock: await readLock(this.config.flake),
        }));
      const steps = Effect.gen(this, function* () {
        for (const step of preview.steps) {
          if (r.dryRun) {
            const outcome = startStep(step.label);
            log(formatCommand(step));
            finishStep(outcome, "succeeded");
          } else if (step.internal) {
            const outcome = startStep(step.label);
            if (step.internal === "compare") {
              const result = yield* compareReview(
                this.config,
                r,
                base!,
                operation.id,
                quiet,
                edits,
                undo && { operationId: undo.operation.id, appliedAt: undo.operation.finishedAt ?? undo.operation.startedAt, point: undo.point },
                operation.steps?.reduce((total, step) => total + (step.progress?.bytes.done ?? 0), 0),
              );
              const packages = (result.home?.changes.length ?? 0) + (result.system?.changes.length ?? 0);
              log(`${packages} package ${packages === 1 ? "change is" : "changes are"} ready for review.`);
            } else if (step.internal === "stage-sources") {
              staged = yield* stageSources(this.config, r, draft).pipe(
                Effect.provide(Layer.succeed(Runner, { run: quiet })),
              );
              for (const file of staged.files) log(`Edited ${file.path}`);
            } else if (step.internal === "verify-edits") {
              edits = yield* verifyEdits(this.config, r, staged!, draft).pipe(
                Effect.provide(Layer.succeed(Runner, { run: quiet })),
              );
              for (const effect of edits.effects) {
                for (const item of effect.added) log(`${effect.scope}: adds ${item.name} (${item.file})`);
                for (const item of effect.removed) log(`${effect.scope}: removes ${item.name} (${item.file})`);
                for (const option of effect.options)
                  log(`${effect.scope}: ${option.option} = ${JSON.stringify(option.after) ?? "default"}`);
              }
            } else if (step.internal === "write-sources") {
              const before = readSources(this.config, review!.edits!.files.map((file) => file.path));
              yield* writeSources(this.config, review!.id, review!.edits!);
              record(before);
            } else if (step.internal === "restore-sources") {
              const { point } = review!.undo!;
              const { contents } = this.undoPoint(review!.undo!.operationId);
              const before = readSources(this.config, point.files.map((file) => file.path));
              yield* restoreSources(this.config, point, contents).pipe(Effect.provide(Layer.succeed(Runner, { run: quiet })));
              record(before);
            } else {
              const before = readSources(this.config, ["flake.lock"]);
              yield* writeReviewedLock(this.config, review!);
              record(before);
            }
            finishStep(outcome, "succeeded");
          } else yield* (yield* Runner).run(step);
        }
      });
      yield* r.action === "review-apply" && !r.dryRun ? steps.pipe(Effect.catchAll(goBack)) : steps;
      if (r.action === "review-apply" && !r.dryRun && (r.activation ?? "switch") === "switch")
        if (generations.home !== undefined || generations.system !== undefined) undoable = { generations, ...replaced };
      const preferences = readPreferences(this.config);
      if (r.action === "review-apply" && !r.dryRun && replaced.files.length && preferences.history) {
        const paths = replaced.files.map((file) => file.path);
        const kept = yield* recordChange(this.config, paths, describeReview(review!), preferences.backup ?? false).pipe(
          Effect.provide(Layer.succeed(Runner, { run: quiet })),
        );
        if (kept) log(kept);
      }
    }).pipe(Effect.provide(Layer.merge(runLayer, this.network)));
    // Set when prepared edits are applied right after, in one step.
    let follow: Request | undefined;
    const promise = Promise.resolve().then(async () => {
      try {
        const exit = await Effect.runPromiseExit(program, {
          signal: controller.signal,
        });
        if (Exit.isSuccess(exit)) {
          operation.status = "succeeded";
          operation.step = r.dryRun ? "Preview complete" : "Complete";
          if (review && undoable) {
            operation.undo = { changes: describeReview(review), generations: undoable.generations, files: undoable.files };
            atomicJson(this.path(operation.id, "undo"), undoable.contents);
          }
          if (r.then === "apply" && !r.dryRun) {
            try {
              operation.held ??= oneStepReviewProblem(getReview(this.config, r.reviewId!));
            } catch (error) {
              operation.held = error instanceof Error ? error.message : String(error);
            }
            if (operation.held) log(`Waiting for your review: ${operation.held}`);
            else follow = request({ action: "review-apply", scope: "home", host: r.host, reviewId: r.reviewId, activation: "switch" });
          }
        } else {
          operation.status = Cause.isInterruptedOnly(exit.cause)
            ? "cancelled"
            : "failed";
          const failure = Cause.failureOption(exit.cause);
          if (Option.isSome(failure) && operation.status === "failed") operation.explanation = failure.value.explanation;
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
            if (r.action === "review-apply" && operation.status === "succeeded") {
              if (review?.edits)
                writeDraft(this.config, remaining(readDraft(this.config), review.edits.changes));
              if (r.activation === "boot" && review?.home && review.system && r.scope !== "system") await keepForBoot(this.config, review);
              else removeReview(this.config, r.reviewId);
            }
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
      if (follow)
        try {
          this.start(follow);
        } catch (error) {
          operation.held = `It could not be applied: ${error instanceof Error ? error.message : String(error)}`;
          persist();
          this.changed(operation);
        }
      return operation;
    });
    this.current = { operation, controller, promise };
    this.changed(operation);
    return operation;
  }
  /** Keeps edits being prepared in one step from being applied, so they wait for review. */
  hold(id: string) {
    const operation = this.current?.operation;
    if (operation?.id !== id || operation.request.then !== "apply") return false;
    operation.held = "You chose to review it first.";
    this.changed(operation);
    return true;
  }
  async wait() {
    return this.current?.promise;
  }
  /**
   * Applies the home part of a review whose system part was applied on next
   * boot, once the computer has started into that system.
   */
  async resumeAfterBoot(booted = "/run/booted-system") {
    const system = await realpath(booted).catch(() => undefined);
    const review = (await listReviews(this.config)).find(
      (item) => item.afterBoot && item.afterBoot.system === system && item.host === this.config.hostKey,
    );
    if (!review || this.busy) return undefined;
    forgetBoot(this.config, review);
    return this.start(request({ action: "review-apply", scope: "home", host: review.host, reviewId: review.id, activation: "switch" }));
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
