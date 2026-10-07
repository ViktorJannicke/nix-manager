import { Args, Command, Options } from "@effect/cli";
import { BunContext } from "@effect/platform-bun";
import { Console, Effect, Option } from "effect";
import { ManagerError, request, validate, type Request } from "./domain";

export interface Cli {
  request: Request;
  flake?: string;
  host?: string;
  mode: "run" | "ui" | "tray" | "status" | "history";
  json: boolean;
  plain: boolean;
  verbose: boolean;
  browser: boolean;
  noOpen: boolean;
  interval: number;
}

const tokenValues = [
  "home",
  "system",
  "all",
  "update",
  "cleanup",
  "gc",
  "build",
  "switch",
  "boot",
  "rollback",
  "check",
  "t3code-update",
  "opencode-update",
  "pince-update",
  "ui",
  "tray",
  "status",
  "history",
] as const;

const described = <A>(option: Options.Options<A>, description: string) =>
  option.pipe(Options.withDescription(description));
const optionalText = (name: string, description: string) =>
  described(Options.text(name).pipe(Options.optional), description);

const cliCommand = Command.make("nix-manager", {
  flake: optionalText("flake", "Use a specific flake checkout"),
  host: optionalText("host", "Select a registered host configuration"),
  dryRun: described(Options.boolean("dry-run"), "Preview without making changes"),
  force: described(
    Options.boolean("force"),
    "Recalculate package hashes even when the pin is current",
  ),
  json: described(Options.boolean("json"), "Write machine-readable JSON"),
  plain: described(
    Options.boolean("plain"),
    "Disable dynamic terminal presentation",
  ),
  verbose: described(
    Options.boolean("verbose"),
    "Retain detailed command output on success",
  ),
  browser: described(
    Options.boolean("browser"),
    "Open the dashboard in the default browser",
  ),
  noOpen: described(
    Options.boolean("no-open"),
    "Serve the dashboard without opening a window",
  ),
  interval: described(
    Options.integer("interval").pipe(Options.withDefault(360)),
    "Tray update-check interval in minutes (5–10080)",
  ),
  tokens: Args.choice(
    tokenValues.map((value) => [value, value] as const),
    { name: "scope/modifier/action" },
  ).pipe(Args.repeated),
}).pipe(
  Command.withDescription(
    "Manage NixOS and Home Manager together. Modifiers may appear before the action, for example: nix-manager home update cleanup switch.\n\nAll selected configurations build before activation. Home activates first; system activation uses Polkit. Arguments after -- are forwarded verbatim to native tools.",
  ),
);

type Parsed =
  typeof cliCommand extends Command.Command<any, any, any, infer A> ? A : never;

function normalize(args: readonly string[]) {
  const marker = args.indexOf("--");
  const commandArgs = marker < 0 ? [...args] : args.slice(0, marker);
  const extra = marker < 0 ? [] : args.slice(marker + 1);
  const flags = new Set([
    "--dry-run",
    "--force",
    "--json",
    "--plain",
    "--verbose",
    "--browser",
    "--no-open",
    "-h",
    "--help",
    "--version",
    "--wizard",
  ]);
  const valued = new Set([
    "--flake",
    "--host",
    "--interval",
    "--completions",
    "--log-level",
  ]);
  const options: string[] = [];
  const tokens: string[] = [];
  for (let index = 0; index < commandArgs.length; index++) {
    const value = commandArgs[index];
    if (flags.has(value)) options.push(value);
    else if (valued.has(value)) {
      options.push(value);
      if (index + 1 < commandArgs.length) options.push(commandArgs[++index]);
    } else tokens.push(value);
  }
  return { args: [...options, ...tokens], extra };
}

function resolve(values: Parsed, extra: readonly string[]): Cli {
  const result: Cli = {
    request: request({ dryRun: values.dryRun, force: values.force, extra }),
    flake: Option.getOrUndefined(values.flake),
    host: Option.getOrUndefined(values.host),
    mode: "run",
    json: values.json,
    plain: values.plain,
    verbose: values.verbose,
    browser: values.browser,
    noOpen: values.noOpen,
    interval: values.interval,
  };
  if (result.host)
    result.request = { ...result.request, host: result.host };
  let action = false;
  let scope = false;
  let explicitMode = false;
  const fail = (message: string): never => {
    throw new ManagerError({ message });
  };
  for (const token of values.tokens as readonly (typeof tokenValues)[number][]) {
    if (["home", "system", "all"].includes(token)) {
      if (scope) fail("Specify one scope.");
      scope = true;
      result.request = {
        ...result.request,
        scope: token as Request["scope"],
      };
    } else if (["update", "cleanup", "gc"].includes(token)) {
      const key = token === "update" ? "update" : "cleanup";
      result.request = { ...result.request, [key]: true };
    } else if (["ui", "tray", "status", "history"].includes(token)) {
      if (action || explicitMode) fail("Specify one action.");
      explicitMode = true;
      result.mode = token as Cli["mode"];
    } else {
      if (action || explicitMode) fail("Specify one action.");
      action = true;
      result.request = {
        ...result.request,
        action: token as Request["action"],
      };
    }
  }
  if (result.interval < 5 || result.interval > 10_080)
    fail("--interval must be 5–10080 minutes.");
  if (result.mode === "run") {
    if (!action && result.request.cleanup)
      result.request = { ...result.request, action: "gc" };
    else if (!action) fail("Specify an action; see --help.");
    if (!scope && result.request.action === "boot")
      result.request = { ...result.request, scope: "system" };
    validate(result.request);
    if (result.browser || result.noOpen || result.interval !== 360)
      fail("Window and interval options require ui or tray.");
  } else if (
    scope ||
    result.request.update ||
    result.request.cleanup ||
    result.request.force ||
    result.request.dryRun ||
    result.request.extra.length
  ) {
    fail(
      "Action modifiers require a build, activation, maintenance, or package action.",
    );
  }
  return result;
}

export async function parseCli(args: readonly string[]): Promise<Cli | undefined> {
  const normalized = normalize(args);
  const plainHelp =
    process.env.NO_COLOR !== undefined ||
    !process.stdout.isTTY ||
    args.includes("--plain");
  let parsed: Parsed | undefined;
  const command = cliCommand.pipe(
    Command.withHandler((values) =>
      Effect.sync(() => {
        parsed = values as Parsed;
      }),
    ),
  );
  try {
    const execute = Command.run(command, {
      name: "Nix Manager",
      version: "1.0.0",
    })(["bun", "nix-manager", ...normalized.args]);
    const stripAnsi = (value: unknown) =>
      typeof value === "string"
        ? value.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, "")
        : value;
    const configured = plainHelp
      ? Console.consoleWith((output) =>
          execute.pipe(
            Console.withConsole({
              ...output,
              log: (...values) => output.log(...values.map(stripAnsi)),
              error: (...values) => output.error(...values.map(stripAnsi)),
              warn: (...values) => output.warn(...values.map(stripAnsi)),
            }),
          ),
        )
      : execute;
    await Effect.runPromise(
      configured.pipe(Effect.provide(BunContext.layer)),
    );
  } catch (error) {
    // @effect/cli already prints its generated validation document. Keep the
    // error typed so callers receive a non-zero exit without a second stack.
    throw new ManagerError({ message: "Command-line parsing failed." });
  }
  return parsed ? resolve(parsed, normalized.extra) : undefined;
}
