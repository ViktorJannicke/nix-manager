import * as clack from "@clack/prompts";
import type { Config, Plan, Request } from "./domain";
import { formatCommand } from "./domain";
import type { Operation } from "./contracts";
import type { Output } from "./process";
import type { UpdateReport } from "./updates";

const actionName = (request: Request) =>
  ({
    build: "Build configuration",
    switch: "Build and switch",
    boot: "Apply on next boot",
    rollback: "Roll back configuration",
    gc: "Clean generations and store",
    check: "Check for updates",
    "t3code-update": "Update T3 Code pin",
    "opencode-update": "Update OpenCode pin",
    "pince-update": "Update PINCE pin",
    "review-build": "Build reviewed outputs",
    "review-apply": "Apply reviewed outputs",
  })[request.action];

export class TerminalRenderer {
  private task?: ReturnType<typeof clack.taskLog>;
  readonly dynamic: boolean;

  constructor(
    private readonly options: {
      json: boolean;
      plain: boolean;
      verbose: boolean;
    },
  ) {
    this.dynamic =
      !options.json &&
      !options.plain &&
      process.env.NO_COLOR === undefined &&
      Boolean(process.stderr.isTTY) &&
      process.env.TERM !== "dumb";
  }

  begin(request: Request, config: Config) {
    if (!this.dynamic) return;
    clack.intro(
      `Nix Manager · ${request.host || config.hostKey} · ${request.scope === "all" ? "Home + system" : request.scope}`,
      { output: process.stderr },
    );
    this.task = clack.taskLog({
      title: actionName(request),
      limit: 6,
      retainLog: this.options.verbose,
      output: process.stderr,
    });
  }

  output = (event: Output) => {
    if (this.options.json) {
      process.stderr.write(event.text);
      return;
    }
    if (!this.task) {
      process[event.stream].write(event.text);
      return;
    }
    for (const line of event.text.replace(/\r/g, "").split("\n"))
      if (line) this.task.message(line, { raw: true });
  };

  heartbeat(operation: Operation) {
    const elapsed = Math.floor(
      (Date.now() - Date.parse(operation.startedAt)) / 1000,
    );
    const message = `${elapsed}s elapsed · ${operation.step}`;
    if (this.task) this.task.message(message);
    else process.stderr.write(`→ ${message}\n`);
  }

  finish(operation: Operation | undefined) {
    if (!operation) return;
    if (this.task) {
      if (operation.status === "succeeded")
        this.task.success("Complete", { showLog: this.options.verbose });
      else this.task.error(operation.error || operation.status, { showLog: true });
      this.task = undefined;
    } else if (!this.options.json && operation.status === "succeeded")
      console.log("✓ Done.");
    if (
      !this.options.json &&
      operation.status !== "succeeded" &&
      operation.steps?.length
    ) {
      const symbol = {
        pending: "·",
        running: "◐",
        succeeded: "✓",
        failed: "×",
        cancelled: "×",
        skipped: "·",
      } as const;
      process.stderr.write(
        operation.steps
          .map((step) => `${symbol[step.status]} ${step.label} (${step.status})`)
          .join("\n") + "\n",
      );
    }
  }

  fail(error: unknown) {
    if (!this.task) return;
    this.task.error(error instanceof Error ? error.message : String(error), {
      showLog: true,
    });
    this.task = undefined;
  }
}

export function printPlan(plan: Plan) {
  for (const note of plan.notes) console.log(`! ${note}`);
  plan.steps.forEach((step, index) =>
    console.log(`${String(index + 1).padStart(2)}  ${step.label}\n    ${formatCommand(step)}`),
  );
}

export function printHistory(history: readonly Operation[], host: string) {
  if (!history.length) {
    console.log("No operations recorded yet.");
    return;
  }
  const width = Math.max(60, process.stdout.columns || 100);
  for (const operation of history) {
    const when = new Date(operation.startedAt).toLocaleString();
    const status = operation.status.padEnd(11);
    const name = actionName(operation.request).padEnd(29);
    const context = `${operation.request.host || host}${operation.request.dryRun ? " · preview" : ""}`;
    console.log(`${when}  ${status} ${name} ${context}`.slice(0, width));
  }
}

export function printStatus(
  config: Config,
  updates: UpdateReport | undefined,
  latest: Operation | undefined,
) {
  const host = config.hosts[config.hostKey];
  console.log(`Nix Manager · ${config.hostKey}`);
  console.log(`Checkout  ${config.flake}`);
  console.log(`Home      ${host.homeConfiguration}`);
  console.log(`System    ${host.nixosConfiguration}`);
  console.log(
    updates
      ? `Updates   ${updates.items.length} available · ${updates.errors.length} errors · ${new Date(updates.checkedAt).toLocaleString()}`
      : "Updates   Not checked · run nix-manager check",
  );
  if (latest)
    console.log(
      `Latest    ${latest.status} · ${actionName(latest.request)} · ${new Date(latest.startedAt).toLocaleString()}`,
    );
}
