import { Cause, Effect, Exit, Option } from "effect";
import { existsSync, realpathSync } from "node:fs";
import { parseCli } from "./cli";
import { loadConfig } from "./config";
import { ManagerError, plan } from "./domain";
import { runnerLayer, runProcess } from "./process";
import { Manager } from "./manager";
import {
  printHistory,
  printPlan,
  printStatus,
  TerminalRenderer,
} from "./terminal";

export async function privileged(args: string[]) {
  if (process.getuid?.() !== 0)
    throw new ManagerError({ message: "Privileged operations require root." });
  const [action, ...extra] = args;
  const run = (argv: string[]) =>
    Effect.runPromise(
      runProcess({ label: "Privileged operation", argv }, (e) =>
        process[e.stream].write(e.text),
      ),
    );
  if (action === "switch" || action === "boot")
    await run(["nixos-rebuild", action, ...extra]);
  else if (action === "rollback")
    await run(["nixos-rebuild", "switch", "--rollback", ...extra]);
  else if (action === "activate-output" && extra.length === 2) {
    const [mode, requested] = extra;
    if (mode !== "switch" && mode !== "boot")
      throw new ManagerError({ message: "Invalid reviewed activation mode." });
    if (
      !/^\/nix\/store\/[a-z0-9]{32}-[^/]+$/.test(requested) ||
      !existsSync(requested)
    )
      throw new ManagerError({ message: "Invalid reviewed system output." });
    const output = realpathSync(requested);
    await run([
      "nix-env",
      "--profile",
      "/nix/var/nix/profiles/system",
      "--set",
      output,
    ]);
    await run([`${output}/bin/switch-to-configuration`, mode]);
  } else if (action === "gc" && extra.length <= 1) {
    const retention = extra.length ? Number(extra[0]) : 3;
    if (!Number.isInteger(retention) || retention < 1 || retention > 100)
      throw new ManagerError({ message: "Invalid generation retention." });
    await run([
      "nix-env",
      "--profile",
      "/nix/var/nix/profiles/system",
      "--delete-generations",
      `+${retention}`,
    ]);
    await run(["nix-store", "--gc"]);
  } else
    throw new ManagerError({
      message: "Unknown privileged operation or unexpected arguments.",
    });
}
async function main(args: string[]) {
  if (args[0] === "--privileged") {
    await privileged(args.slice(1));
    return;
  }
  const cli = await parseCli(args);
  if (!cli) return;
  const config = await Effect.runPromise(
    loadConfig(cli.flake, cli.host).pipe(Effect.provide(runnerLayer())),
  );
  const terminal = new TerminalRenderer(cli);
  const manager = new Manager(
    config,
    cli.mode === "run" ? terminal.output : undefined,
  );
  if (cli.mode === "history" || cli.mode === "status") {
    const data =
      cli.mode === "history"
        ? manager.history()
        : {
            host: config.hostKey,
            flake: config.flake,
            config: config.hosts[config.hostKey],
            updates: manager.updates() || null,
            latest: manager.history()[0] || null,
          };
    if (cli.json) console.log(JSON.stringify(data, null, 2));
    else if (cli.mode === "history")
      printHistory(manager.history(), config.hostKey);
    else
      printStatus(config, manager.updates(), manager.history()[0]);
    return;
  }
  if (cli.mode === "run") {
    const r = { ...cli.request, host: cli.request.host || config.hostKey };
    // A normal command preview is entirely read-only, including operation state.
    if (r.dryRun && !r.action.endsWith("-update")) {
      const p = plan(config, r);
      if (cli.json) console.log(JSON.stringify(p, null, 2));
      else {
        printPlan(p);
      }
      return;
    }
    const interrupt = () => manager.cancel();
    process.on("SIGINT", interrupt);
    process.on("SIGTERM", interrupt);
    const heartbeat = setInterval(() => {
      const active = manager.active;
      if (active) terminal.heartbeat(active);
    }, 15_000);
    try {
      terminal.begin(r, config);
      try {
        manager.start(r);
      } catch (error) {
        terminal.fail(error);
        throw error;
      }
      const result = await manager.wait();
      if (cli.json)
        console.log(
          JSON.stringify(
            r.action === "check"
              ? { operation: result, updates: manager.updates() }
              : result,
            null,
            2,
          ),
        );
      else terminal.finish(result);
      process.exitCode =
        result?.status === "succeeded"
          ? 0
          : result?.status === "cancelled"
            ? 130
            : 1;
    } finally {
      clearInterval(heartbeat);
      process.off("SIGINT", interrupt);
      process.off("SIGTERM", interrupt);
    }
    return;
  }
  const { startServer, browserAssets } = await import("./server");
  const service = startServer(manager, await browserAssets());
  let window: ReturnType<typeof import("./window").createWindow> | undefined;
  let tray: Awaited<ReturnType<typeof import("./tray").startTray>> | undefined;
  let resolveShutdown!: () => void;
  const stopped = new Promise<void>((resolve) => {
    resolveShutdown = resolve;
  });
  const open = async () => {
    if (cli.browser)
      await Effect.runPromise(
        runProcess({ label: "Open browser", argv: ["xdg-open", service.url] }),
      );
    else {
      const { createWindow } = await import("./window");
      window ??= createWindow();
      window.show(service.url);
    }
  };
  const shutdown = () => {
    resolveShutdown();
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
  let idle: ReturnType<typeof setInterval> | undefined;
  try {
    if (cli.mode === "tray") {
      const { startTray } = await import("./tray");
      tray = await startTray(manager, open, shutdown, cli.interval);
      console.log("Nix Manager is running in the tray.");
    } else if (!cli.noOpen) await open();
    if (cli.noOpen) console.log(`Nix Manager: ${service.url}`);
    // Only standalone UIs exit with the last dashboard. Give reloads a short
    // reconnect grace period and let an active operation finish before exiting.
    if (cli.mode === "ui" && !cli.noOpen) {
      const openedAt = Date.now();
      idle = setInterval(() => {
        const closed =
          service.disconnectedAt !== undefined &&
          Date.now() - service.disconnectedAt >= 1_000;
        const neverConnected =
          !service.connected && Date.now() - openedAt > 180_000;
        if (!manager.busy && (closed || neverConnected)) shutdown();
      }, 250);
    }
    await stopped;
  } finally {
    if (idle) clearInterval(idle);
    tray?.close();
    await manager.close();
    service.close();
    window?.close();
    process.off("SIGINT", shutdown);
    process.off("SIGTERM", shutdown);
  }
}
if (import.meta.main) {
  const exit = await Effect.runPromiseExit(
    Effect.tryPromise({
      try: () => main(process.argv.slice(2)),
      catch: (error) => error,
    }),
  );
  if (Exit.isFailure(exit)) {
    const failure = Cause.failureOption(exit.cause);
    if (Option.isSome(failure) && failure.value instanceof ManagerError) {
      if (failure.value.message !== "Command-line parsing failed.")
        console.error(`Error: ${failure.value.message}`);
    } else console.error(Cause.pretty(exit.cause));
    process.exitCode = 1;
  }
}
