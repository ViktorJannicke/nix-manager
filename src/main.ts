import { Cause, Effect, Exit, Option } from "effect";
import { existsSync, realpathSync } from "node:fs";
import { parseCli, type Cli } from "./cli";
import { loadConfig } from "./config";
import { ManagerError, type Config } from "./domain";
import { runnerLayer, runProcess } from "./process";
import { Manager } from "./manager";
import { parseSoftware, runSoftware, software } from "./software-cli";
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
  const run = (argv: string[], env?: Record<string, string>) =>
    Effect.runPromise(
      runProcess({ label: "Privileged operation", argv, env }, (e) => {
        if (e.stream !== "progress") process[e.stream].write(e.text);
      }),
    );
  // Like nixos-rebuild, run switch-to-configuration in its own transient
  // unit so a restarting display manager or session cannot interrupt it.
  const switchTo = async (output: string, mode: string) => {
    const systemdRun = "/run/current-system/sw/bin/systemd-run";
    const command = [`${output}/bin/switch-to-configuration`, mode];
    if (existsSync(systemdRun) && existsSync("/run/systemd/system"))
      await run([
        systemdRun,
        "-E",
        "LOCALE_ARCHIVE",
        "-E",
        "NIXOS_INSTALL_BOOTLOADER",
        "--collect",
        "--no-ask-password",
        "--pipe",
        "--quiet",
        "--service-type=exec",
        "--unit=nix-manager-switch-to-configuration",
        ...command,
      ]);
    else await run(command);
  };
  const profile = "/nix/var/nix/profiles/system";
  if (action === "rollback" && extra.length === 0) {
    await run(["nix-env", "--profile", profile, "--rollback"]);
    await switchTo(realpathSync(profile), "switch");
  } else if (action === "activate-output" && extra.length === 2) {
    const [mode, requested] = extra;
    if (mode !== "switch" && mode !== "boot")
      throw new ManagerError({ message: "Invalid activation mode." });
    // The caller may pass a build out-link; only its store path is trusted.
    const output = existsSync(requested) ? realpathSync(requested) : "";
    if (
      !/^\/nix\/store\/[a-z0-9]{32}-nixos-system-[^/]+$/.test(output) ||
      !existsSync(`${output}/bin/switch-to-configuration`)
    )
      throw new ManagerError({ message: "Invalid NixOS system output." });
    await run(["nix-env", "--profile", profile, "--set", output]);
    await switchTo(output, mode);
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
/** Runs one operation in the terminal and sets the exit code from its result. */
async function execute(cli: Cli, config: Config, manager: Manager, terminal: TerminalRenderer) {
  const r = { ...cli.request, host: cli.request.host || config.hostKey };
  // A normal command preview is entirely read-only, including operation state.
  if (r.dryRun && !r.action.endsWith("-update")) {
    const p = manager.plan(r);
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
}
async function main(args: string[]) {
  if (args[0] === "--privileged") {
    await privileged(args.slice(1));
    return;
  }
  const softwareArgs = software(args);
  if (softwareArgs) {
    const command = await parseSoftware(softwareArgs);
    if (!command) return;
    const config = await Effect.runPromise(
      loadConfig(command.flake, command.host).pipe(Effect.provide(runnerLayer())),
    );
    const next = await runSoftware(config, command);
    if (!next) return;
    const cli: Cli = {
      request: next,
      mode: "run",
      json: command.json,
      plain: command.plain,
      verbose: false,
      browser: false,
      noOpen: false,
      interval: 360,
    };
    const terminal = new TerminalRenderer(cli);
    await execute(cli, config, new Manager(config, terminal.output), terminal);
    if (next.action === "review-build" && !next.dryRun && !cli.json && process.exitCode === 0)
      console.error(
        "Review the changes with nix-manager ui, or write and activate them with nix-manager software apply (--boot for the next boot).",
      );
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
    await execute(cli, config, manager, terminal);
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
      if (failure.value.explanation)
        console.error(`\n${failure.value.explanation.title}.\n${failure.value.explanation.body}`);
    } else console.error(Cause.pretty(exit.cause));
    process.exitCode = 1;
  }
}
