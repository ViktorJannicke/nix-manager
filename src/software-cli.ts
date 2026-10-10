// `nix-manager software ...`: the Software page's pending edits from a terminal.
// Edits go into the same draft the dashboard shows; prepare and apply run the
// same reviewed operations.
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { Args, Command, Options } from "@effect/cli";
import { BunContext } from "@effect/platform-bun";
import { Console, Effect, Option } from "effect";
import {
  acceptsUnfree,
  changesScope,
  describeChange,
  dropChange,
  featureModule,
  freePath,
  mergeChanges,
  newFileProblem,
  optionTopic,
  origin,
  rankImporters,
  rankModules,
  showValue,
} from "./changes";
import { ManagerError, request, type Config, type Request } from "./domain";
import { changeHash, draftPublic, readDraft, writeDraft, type Change } from "./draft";
import { splitAttr } from "./edits";
import { fixCommand } from "./explain";
import { hashText, readLock } from "./observe";
import { flakeSource } from "./packages";
import { runnerLayer, type Runner } from "./process";
import { listReviews } from "./reviews";
import {
  buildCatalog,
  catalogPath,
  configurationAttr,
  evaluateState,
  inventory,
  searchCatalog,
  type CatalogEntry,
  type ScopeInventory,
  type Target,
} from "./software";

type Edit =
  | { kind: "search"; query: string[]; limit: number; free: boolean; working: boolean }
  | { kind: "add"; packages: string[] }
  | { kind: "remove"; packages: string[] }
  | { kind: "set"; option: string; value: string }
  | { kind: "unset"; option: string }
  | { kind: "pending"; diff: boolean }
  | { kind: "drop"; index: number }
  | { kind: "clear" }
  | { kind: "prepare" }
  | { kind: "apply"; boot: boolean; dryRun: boolean };

export interface SoftwareCommand {
  edit: Edit;
  flake?: string;
  host?: string;
  json: boolean;
  plain: boolean;
  system: boolean;
  file?: string;
  newFile?: string;
  importFrom?: string;
}

const described = <A>(option: Options.Options<A>, description: string) => option.pipe(Options.withDescription(description));
const optionalText = (name: string, description: string) => described(Options.text(name).pipe(Options.optional), description);

const shared = {
  flake: optionalText("flake", "Use a specific flake checkout"),
  host: optionalText("host", "Select a registered host configuration"),
  json: described(Options.boolean("json"), "Write machine-readable JSON"),
  plain: described(Options.boolean("plain"), "Disable dynamic terminal presentation"),
};
const target = {
  system: described(Options.boolean("system"), "Edit the system configuration instead of your home configuration"),
  file: optionalText("file", "Write into this file of the flake"),
  newFile: optionalText("new-file", "Create this module file and write into it"),
  importFrom: optionalText("import-from", "Import the new file from this module (default: its folder's imported module)"),
};

const packages = Args.text({ name: "package" }).pipe(Args.atLeast(1));
const option = Args.text({ name: "option" });

const definitions = {
  search: Command.make("search", {
    ...shared,
    limit: described(Options.integer("limit").pipe(Options.withDefault(20)), "Show at most this many packages"),
    free: described(Options.boolean("free"), "Only packages with a free license"),
    working: described(Options.boolean("working"), "Leave out packages marked as broken"),
    query: Args.text({ name: "query" }).pipe(Args.atLeast(1)),
  }).pipe(
    Command.withDescription("Search the packages of the locked nixpkgs"),
  ),
  add: Command.make("add", { ...shared, ...target, packages }).pipe(Command.withDescription("Install packages")),
  remove: Command.make("remove", { ...shared, system: target.system, file: target.file, packages }).pipe(Command.withDescription("Remove packages from the file that lists them")),
  set: Command.make("set", { ...shared, ...target, option, value: Args.text({ name: "value" }) }).pipe(
    Command.withDescription("Set an option; the value is JSON, or text when it is not JSON"),
  ),
  unset: Command.make("unset", { ...shared, system: target.system, file: target.file, option }).pipe(Command.withDescription("Remove an option's setting so its default applies")),
  pending: Command.make("pending", { ...shared, diff: described(Options.boolean("diff"), "Show the edited lines") }).pipe(
    Command.withDescription("List the pending edits"),
  ),
  drop: Command.make("drop", { ...shared, index: Args.integer({ name: "number" }) }).pipe(Command.withDescription("Remove one pending edit by its number")),
  clear: Command.make("clear", shared).pipe(Command.withDescription("Remove every pending edit")),
  prepare: Command.make("prepare", shared).pipe(Command.withDescription("Build the pending edits into a review")),
  apply: Command.make("apply", {
    ...shared,
    boot: described(Options.boolean("boot"), "Activate the system on the next boot"),
    dryRun: described(Options.boolean("dry-run"), "Show the steps without running them"),
  }).pipe(Command.withDescription("Write and activate the review prepared from the pending edits")),
};

/** Options with a value, for moving options before the positional arguments. */
const valued = new Set(["--flake", "--host", "--file", "--new-file", "--import-from", "--limit"]);

/**
 * The arguments of a software command with options given before `software`
 * moved behind it, or undefined for other commands.
 */
export function software(args: readonly string[]) {
  let index = 0;
  while (index < args.length && args[index].startsWith("--")) index += valued.has(args[index]) ? 2 : 1;
  if (args[index] !== "software") return undefined;
  const [sub, ...rest] = args.slice(index + 1);
  return ["software", ...(sub === undefined ? [] : [sub]), ...args.slice(0, index), ...rest];
}

/** Parses `software <command> ...`; undefined when only help or the version was shown. */
export async function parseSoftware(args: readonly string[]): Promise<SoftwareCommand | undefined> {
  let parsed: SoftwareCommand | undefined;
  const finish = (edit: Edit) => (values: Record<string, unknown>) =>
    Effect.sync(() => {
      const text = (key: string) => {
        const value = values[key] as Option.Option<string> | undefined;
        return value && Option.getOrUndefined(value);
      };
      parsed = {
        edit,
        flake: text("flake"),
        host: text("host"),
        json: Boolean(values.json),
        plain: Boolean(values.plain),
        system: Boolean(values.system),
        file: text("file"),
        newFile: text("newFile"),
        importFrom: text("importFrom"),
      };
    });
  const d = definitions;
  const software = Command.make("software").pipe(
    Command.withDescription("Install packages and change options through pending edits, like the Software page"),
    Command.withSubcommands([
      d.search.pipe(Command.withHandler((v) => finish({ kind: "search", query: [...v.query], limit: v.limit, free: v.free, working: v.working })(v))),
      d.add.pipe(Command.withHandler((v) => finish({ kind: "add", packages: [...v.packages] })(v))),
      d.remove.pipe(Command.withHandler((v) => finish({ kind: "remove", packages: [...v.packages] })(v))),
      d.set.pipe(Command.withHandler((v) => finish({ kind: "set", option: v.option, value: v.value })(v))),
      d.unset.pipe(Command.withHandler((v) => finish({ kind: "unset", option: v.option })(v))),
      d.pending.pipe(Command.withHandler((v) => finish({ kind: "pending", diff: v.diff })(v))),
      d.drop.pipe(Command.withHandler((v) => finish({ kind: "drop", index: v.index })(v))),
      d.clear.pipe(Command.withHandler((v) => finish({ kind: "clear" })(v))),
      d.prepare.pipe(Command.withHandler((v) => finish({ kind: "prepare" })(v))),
      d.apply.pipe(Command.withHandler((v) => finish({ kind: "apply", boot: v.boot, dryRun: v.dryRun })(v))),
    ]),
  );
  const root = Command.make("nix-manager").pipe(Command.withSubcommands([software]));
  // Options may follow the arguments; the parser expects them first.
  const [name, sub, ...rest] = args;
  const options: string[] = [];
  const positional: string[] = [];
  for (let index = 0; index < rest.length; index++) {
    const value = rest[index];
    if (value === "--") {
      positional.push(...rest.slice(index + 1));
      break;
    }
    if (value.startsWith("--") || value === "-h") {
      options.push(value);
      if (valued.has(value) && index + 1 < rest.length) options.push(rest[++index]);
    } else positional.push(value);
  }
  const argv = [name, ...(sub === undefined ? [] : [sub]), ...options, ...(positional.length ? ["--", ...positional] : [])];
  const execute = Command.run(root, { name: "Nix Manager", version: "1.0.0" })(["bun", "nix-manager", ...argv]);
  const strip = (value: unknown) => (typeof value === "string" ? value.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, "") : value);
  const plain = process.env.NO_COLOR !== undefined || !process.stdout.isTTY || args.includes("--plain");
  try {
    await Effect.runPromise(
      (plain
        ? Console.consoleWith((output) =>
            execute.pipe(
              Console.withConsole({
                ...output,
                log: (...values) => output.log(...values.map(strip)),
                error: (...values) => output.error(...values.map(strip)),
              }),
            ),
          )
        : execute
      ).pipe(Effect.provide(BunContext.layer)),
    );
  } catch {
    throw new ManagerError({ message: "Command-line parsing failed." });
  }
  return parsed;
}

const run = <A>(effect: Effect.Effect<A, ManagerError, Runner>) => Effect.runPromise(effect.pipe(Effect.provide(runnerLayer())));
const note = (text: string) => process.stderr.write(`${text}\n`);
const local = (file: string) => !file.startsWith("/");
const attrOf = (name: string) => {
  const attr = name.startsWith("pkgs.") ? name : `pkgs.${name}`;
  if (splitAttr(attr)?.[0] !== "pkgs" || splitAttr(attr)!.length < 2)
    throw new ManagerError({ message: `${name} is not a package attribute.` });
  return attr;
};

/** The package index of the checkout's lock file, built when missing. */
async function catalog(config: Config, build: boolean): Promise<CatalogEntry[] | undefined> {
  const path = catalogPath(config, hashText(await readLock(config.flake)));
  if (!existsSync(path)) {
    if (!build) return undefined;
    note("Building the package index for this lock file. This takes a minute the first time.");
    await run(buildCatalog(config, config.hostKey, path));
  }
  return JSON.parse(await readFile(path, "utf8")) as CatalogEntry[];
}

/** Saves the draft unless one of the new edits cannot be applied to the checkout. */
async function save(config: Config, before: readonly Change[], changes: Change[], json: boolean) {
  const draft = await draftPublic(config.flake, changes);
  const added = new Set(changes.map((change, index) => (before.includes(change) ? -1 : index)));
  const problem = draft.problems.find((item) => added.has(item.index));
  if (problem) throw new ManagerError({ message: `${describeChange(changes[problem.index]).title}: ${problem.message}` });
  writeDraft(config, changes);
  if (json) console.log(JSON.stringify(draft, null, 2));
  else {
    for (const change of changes.filter((_, index) => added.has(index))) {
      const text = describeChange(change);
      console.log(`+ ${text.title} (${text.detail})`);
    }
    console.log(`${changes.length} pending ${changes.length === 1 ? "edit" : "edits"}. Run nix-manager software prepare to build ${changes.length === 1 ? "it" : "them"}.`);
  }
}

function printPending(draft: Awaited<ReturnType<typeof draftPublic>>, diff: boolean) {
  if (!draft.changes.length) {
    console.log("No pending edits.");
    return;
  }
  const problems = new Map(draft.problems.map((problem) => [problem.index, problem.message]));
  draft.changes.forEach((change, index) => {
    const text = describeChange(change);
    console.log(`${String(index + 1).padStart(3)}  ${text.title}\n     ${text.detail}`);
    if (problems.has(index)) console.log(`     Cannot apply: ${problems.get(index)}`);
  });
  if (!diff) return;
  for (const file of draft.files) {
    console.log(`\n--- ${file.created ? "/dev/null" : `a/${file.path}`}\n+++ b/${file.path}`);
    for (const hunk of file.hunks) {
      console.log(`@@ line ${hunk.line} @@`);
      for (const line of hunk.lines) console.log(`${line.kind}${line.text}`);
    }
  }
}

/**
 * Runs a software command. Commands that edit the draft or read the checkout
 * finish here; prepare and apply return the operation to run.
 */
export async function runSoftware(config: Config, command: SoftwareCommand): Promise<Request | undefined> {
  const { edit, json } = command;
  const host = command.host || config.hostKey;
  const scope: Target = command.system ? "system" : "home";
  const name = scope === "home" ? `${host}'s home configuration` : `${host}'s system`;
  const changes = readDraft(config);
  let read: ScopeInventory | undefined;
  const inventoryOf = async () => {
    if (read) return read;
    note(`Reading ${name}...`);
    return (read = await run(inventory(config, host, scope)));
  };
  const known = (state: ScopeInventory) =>
    new Set([
      ...state.modules.map((module) => module.file),
      ...state.files.map((file) => file.file),
      ...state.importers,
      ...changes.filter((change) => change.kind === "create-module").map((change) => change.file),
    ]);
  /** The file named by --file or --new-file; a new file comes with the edit creating it. */
  const chosen = async (): Promise<{ file: string; create: Change[] } | undefined> => {
    if (command.file && command.newFile) throw new ManagerError({ message: "Use either --file or --new-file." });
    if (command.file) return { file: command.file, create: [] };
    if (!command.newFile) return undefined;
    const state = await inventoryOf();
    const problem = newFileProblem(known(state), command.newFile);
    if (problem) throw new ManagerError({ message: `${command.newFile}: ${problem}` });
    const importer = command.importFrom ?? rankImporters(state.importers, command.newFile, state.imports)[0];
    if (!importer)
      throw new ManagerError({ message: `No file ${host} imports has an imports list to add ${command.newFile} to. Use --import-from.` });
    return { file: command.newFile, create: [{ kind: "create-module", scope, file: command.newFile, importer }] };
  };
  /** Current value and defining flake files of an option. */
  const optionState = async (option: string) => {
    note(`Reading ${option} in ${name}...`);
    const state = await run(
      Effect.flatMap(flakeSource(config.flake), (source) =>
        evaluateState(source, configurationAttr(config, host, scope), scope, { options: [option] }),
      ),
    );
    const value = state.options[option];
    if (!value?.exists) throw new ManagerError({ message: `${option} is not an option of ${name}.` });
    return { ...value, files: value.files.filter(local) };
  };

  switch (edit.kind) {
    case "search": {
      const entries = (await catalog(config, true))!;
      const results = searchCatalog(entries, edit.query.join(" "), Math.max(1, Math.min(edit.limit, 200)), edit);
      if (json) console.log(JSON.stringify(results, null, 2));
      else if (!results.length) console.log(`No packages match "${edit.query.join(" ")}".`);
      else
        for (const entry of results)
          console.log(
            `${entry.a}${entry.v ? ` ${entry.v}` : ""}${entry.b ? " (broken)" : ""}${entry.f ? "" : " (unfree)"}\n    ${entry.d ?? "No description."}`,
          );
      return;
    }
    case "add": {
      const attrs = edit.packages.map(attrOf);
      const index = await catalog(config, false);
      if (index) {
        const names = new Set(index.map((entry) => `pkgs.${entry.a}`));
        const missing = attrs.find((attr) => !names.has(attr));
        if (missing)
          throw new ManagerError({ message: `${missing} is not in the locked nixpkgs. Find packages with nix-manager software search.` });
      }
      const state = await inventoryOf();
      for (const attr of attrs) {
        const installed = state.packages.find((item) => item.attr === attr || (!item.attr && `pkgs.${item.name}` === attr));
        if (installed)
          throw new ManagerError({
            message: installed.attr ? `${attr} is already listed in ${installed.file}.` : `${attr} is already installed by ${origin(installed.file)}.`,
          });
      }
      // By default packages join the used list holding the most packages.
      const best = [...state.files].sort(
        (a, b) => Number(b.active) - Number(a.active) || b.count - a.count || a.file.localeCompare(b.file),
      )[0];
      const target = (await chosen()) ?? (best?.active ? { file: best.file, create: [] } : undefined);
      if (!target)
        throw new ManagerError({ message: `No file of ${name} has a package list. Choose one with --file, or create one with --new-file.` });
      const next = [...target.create, ...attrs.map((attr): Change => ({ kind: "add-package", scope, file: target.file, attr }))];
      await save(config, changes, mergeChanges(changes, next), json);
      // Prepare would refuse these; say so now, with the edit that allows them.
      const allowed = changes.some(
        (change) => change.kind === "set-option" && change.scope === scope && change.option === "nixpkgs.config.allowUnfree" && change.value === true,
      );
      const refused = allowed
        ? []
        : (index ?? []).filter((entry) => attrs.includes(`pkgs.${entry.a}`) && !entry.f && !acceptsUnfree(state.unfree, entry.n));
      if (refused.length)
        note(
          `${refused.map((entry) => entry.n ?? entry.a).join(", ")} ${refused.length === 1 ? "is" : "are"} not free software, which ${name} does not allow. To allow it: ${fixCommand({ kind: "allow-unfree", packages: [], scope })}`,
        );
      return;
    }
    case "remove": {
      const state = await inventoryOf();
      const next = edit.packages.map(attrOf).map((attr): Change => {
        const listed = state.packages.find((item) => item.attr === attr && (!command.file || item.file === command.file));
        if (listed) return { kind: "remove-package", scope, file: listed.file, attr };
        const added = state.packages.find((item) => `pkgs.${item.name}` === attr && item.external);
        throw new ManagerError({
          message: added
            ? `${attr} is added by ${origin(added.file)}, not a package list. Turn off the setting that adds it instead.`
            : `${attr} is not listed in a file of ${name}.${command.system ? "" : " For system packages, add --system."}`,
        });
      });
      return void (await save(config, changes, mergeChanges(changes, next), json));
    }
    case "set":
    case "unset": {
      const current = await optionState(edit.option);
      let target = await chosen();
      // A setting nothing sets yet goes where the dashboard would put it.
      if (!target && edit.kind === "set" && !current.files.length) {
        const state = await inventoryOf();
        const ranked = rankModules(state.modules, scope, edit.option);
        const topic = optionTopic(edit.option);
        const existing = featureModule(ranked, topic);
        if (existing) target = { file: existing, create: [] };
        else {
          const file = freePath(known(state), topic, ranked.find((module) => module.active !== false)?.file, scope === "home" ? "modules/home" : "modules/nixos");
          const importer = rankImporters(state.importers, file, state.imports)[0];
          if (importer) target = { file, create: [{ kind: "create-module", scope, file, importer }] };
        }
        if (target) note(`Writing to ${target.file}${target.create.length ? ` (new, imported from ${(target.create[0] as { importer: string }).importer})` : ""}.`);
      }
      const file = target?.file ?? (current.files.length === 1 ? current.files[0] : undefined);
      if (!file)
        throw new ManagerError({
          message: current.files.length
            ? `${edit.option} is set in ${current.files.join(", ")}. Choose one with --file.`
            : `${edit.option} is not set in the flake yet. Choose where to write it with --file <path> or --new-file <path>.`,
        });
      let next: Change[];
      if (edit.kind === "set") {
        let value: unknown;
        try {
          value = JSON.parse(edit.value);
        } catch {
          value = edit.value;
        }
        if (!current.error && JSON.stringify(current.value) === JSON.stringify(value) && current.files.includes(file))
          throw new ManagerError({ message: `${edit.option} is already ${showValue(value)}.` });
        next = [...(target?.create ?? []), { kind: "set-option", scope, file, option: edit.option, value }];
      } else {
        if (!current.files.includes(file)) throw new ManagerError({ message: `${edit.option} is not set in ${file}.` });
        next = [{ kind: "unset-option", scope, file, option: edit.option }];
      }
      return void (await save(config, changes, mergeChanges(changes, next), json));
    }
    case "pending": {
      const draft = await draftPublic(config.flake, changes);
      if (json) console.log(JSON.stringify(draft, null, 2));
      else printPending(draft, edit.diff);
      return;
    }
    case "drop": {
      if (!Number.isInteger(edit.index) || edit.index < 1 || edit.index > changes.length)
        throw new ManagerError({ message: changes.length ? `Choose an edit from 1 to ${changes.length}.` : "There are no pending edits." });
      const removed = describeChange(changes[edit.index - 1]);
      const rest = dropChange(changes, edit.index - 1);
      writeDraft(config, rest);
      if (json) console.log(JSON.stringify(await draftPublic(config.flake, rest), null, 2));
      else console.log(`Dropped: ${removed.title}. ${rest.length} pending ${rest.length === 1 ? "edit" : "edits"} left.`);
      return;
    }
    case "clear": {
      writeDraft(config, []);
      if (!json) console.log(changes.length ? `Dropped ${changes.length} pending ${changes.length === 1 ? "edit" : "edits"}.` : "No pending edits.");
      else console.log(JSON.stringify({ changes: [] }));
      return;
    }
    case "prepare": {
      if (!changes.length) throw new ManagerError({ message: "There are no pending edits." });
      return request({ action: "review-build", scope: changesScope(changes), host, edits: true });
    }
    case "apply": {
      const hash = changeHash(changes);
      const review = (await listReviews(config)).find(
        (item) => item.edits?.hash === hash && (item.host || config.hostKey) === host,
      );
      if (!changes.length || !review)
        throw new ManagerError({
          message: "No prepared review matches the pending edits. Run nix-manager software prepare first.",
        });
      return request({
        action: "review-apply",
        reviewId: review.id,
        scope: review.scope,
        host,
        activation: edit.boot ? "boot" : "switch",
        dryRun: edit.dryRun,
      });
    }
  }
}
