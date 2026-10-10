import { Data, Schema } from "effect";
import type { BuildReview } from "./contracts";
import type { Explanation } from "./explain";
import { attr } from "./nix";

export class ManagerError extends Data.TaggedError("ManagerError")<{
  message: string;
  /** The failure in plain words, when it is a known one. */
  explanation?: Explanation;
}> {}

export const Host = Schema.Struct({
  system: Schema.String,
  userName: Schema.optional(Schema.String),
  nixosConfiguration: Schema.String,
  nixosHostName: Schema.String,
  homeConfiguration: Schema.String,
});
export const Hosts = Schema.Record({ key: Schema.String, value: Host });
export type Host = typeof Host.Type;
export type Hosts = typeof Hosts.Type;
export const Action = Schema.Literal(
  "build",
  "switch",
  "boot",
  "rollback",
  "gc",
  "t3code-update",
  "opencode-update",
  "pince-update",
  "check",
  "review-build",
  "review-apply",
);
export type Action = typeof Action.Type;
export const Scope = Schema.Literal("all", "home", "system");
export type Scope = typeof Scope.Type;
export const Request = Schema.Struct({
  action: Action,
  scope: Scope,
  host: Schema.String,
  update: Schema.Boolean,
  cleanup: Schema.Boolean,
  force: Schema.Boolean,
  dryRun: Schema.Boolean,
  extra: Schema.Array(Schema.String),
  reviewId: Schema.optional(Schema.String),
  retention: Schema.optional(Schema.Number),
  /** Existing generation to retain for review (rollback). */
  generation: Schema.optional(Schema.Number),
  /** Flake inputs to update into a proposed lock file for review. */
  inputs: Schema.optional(Schema.Array(Schema.String)),
  activation: Schema.optional(Schema.Literal("switch", "boot")),
  /** Build the pending source edits into the review. */
  edits: Schema.optional(Schema.Boolean),
});
export type Request = typeof Request.Type;
export const request = (overrides: Partial<Request> = {}): Request => ({
  action: "build",
  scope: "all",
  host: "",
  update: false,
  cleanup: false,
  force: false,
  dryRun: false,
  extra: [],
  retention: 3,
  ...overrides,
});
export const actionLabels: Record<Action, string> = {
  build: "Build configuration",
  switch: "Build and switch",
  boot: "Apply on next boot",
  rollback: "Roll back configuration",
  gc: "Clean up generations",
  check: "Check for updates",
  "t3code-update": "Update T3 Code",
  "opencode-update": "Update OpenCode",
  "pince-update": "Update PINCE",
  "review-build": "Prepare changes",
  "review-apply": "Apply changes",
};
export const isPackage = (action: string) => action.endsWith("-update");
export const isReview = (action: string) =>
  action === "review-build" || action === "review-apply";
export const validId = (id: string | undefined): id is string =>
  !!id && /^[a-f0-9-]{8,64}$/.test(id);
export function validate(r: Request): void {
  const fail = (message: string): never => {
    throw new ManagerError({ message });
  };
  if (r.action === "boot" && r.scope !== "system")
    fail("boot requires system scope.");
  if (isPackage(r.action)) {
    if (r.scope === "system") fail("Package updates apply to home packages.");
    if (r.update || r.cleanup || r.extra.length)
      fail(
        "Package updates cannot combine with update, cleanup or extra arguments.",
      );
  } else if (r.force) fail("--force applies to package updates.");
  if (["rollback", "gc", "check"].includes(r.action) && r.update)
    fail(`Cannot update inputs during ${r.action}.`);
  if (["rollback", "build", "check"].includes(r.action) && r.cleanup)
    fail("Cleanup requires successful activation or the gc action.");
  if (["gc", "check"].includes(r.action) && r.extra.length)
    fail(`${r.action} takes no extra arguments.`);
  if (r.action === "rollback" && r.scope !== "home" && r.extra.length)
    fail("System rollback takes no extra arguments.");
  const retention = r.retention ?? 3;
  if (!Number.isInteger(retention) || retention < 1 || retention > 100)
    fail("Retention must be between 1 and 100 generations.");
  if (r.inputs && r.action !== "review-build")
    fail("Input selection applies to reviewed builds.");
  if (r.inputs?.some((name) => !/^[A-Za-z0-9_-]+$/.test(name)))
    fail("Invalid flake input name.");
  if (r.activation && r.action !== "review-apply")
    fail("The activation mode applies to reviewed changes.");
  if (r.edits && r.action !== "review-build")
    fail("Pending edits are built into reviews.");
  if (r.edits && r.generation !== undefined)
    fail("A generation review cannot include edits.");
  if (r.generation !== undefined && r.action !== "review-build")
    fail("A generation can only be selected for review.");
  if (isReview(r.action)) {
    if (r.reviewId !== undefined && !validId(r.reviewId))
      fail("A valid review ID is required.");
    if (r.action === "review-apply" && !r.reviewId)
      fail("A valid review ID is required.");
    if (r.cleanup || r.extra.length)
      fail("Reviewed workflows cannot combine with cleanup or extra arguments.");
    if (r.action === "review-apply" && r.update)
      fail("Applying a review cannot update inputs.");
    if (r.generation !== undefined) {
      if (!Number.isInteger(r.generation) || r.generation < 1)
        fail("Invalid generation number.");
      if (r.scope === "all")
        fail("Select either the home or the system generation.");
      if (r.update || r.inputs?.length)
        fail("A generation review cannot update inputs.");
    }
  }
}

export interface Config {
  flake: string;
  hosts: Hosts;
  hostKey: string;
  stateDir: string;
  homeProfile: string;
  homeProfileExists: boolean;
  privileged: readonly string[];
}
/** Steps the manager performs itself instead of starting a process. */
export type InternalStep =
  | "compare"
  | "write-lock"
  | "stage-sources"
  | "verify-edits"
  | "write-sources";
export interface Command {
  label: string;
  argv: readonly string[];
  cwd?: string;
  env?: Record<string, string>;
  allowFailure?: boolean;
  quiet?: boolean;
  /** Keep all of stdout instead of its last 8 MiB, for results such as large JSON documents. */
  fullOutput?: boolean;
  /** Parse Nix's structured log for progress; argv must request it. */
  progress?: boolean;
  internal?: InternalStep;
}
export interface Plan {
  steps: Command[];
  notes: string[];
}
export const systemProfile = "/nix/var/nix/profiles/system";
export const reviewRoot = (config: Config, id: string) =>
  `${config.stateDir}/reviews/${id}`;
export const buildRoot = (config: Config) => `${config.stateDir}/build`;
export function resolveHost(hosts: Hosts, name: string): [string, Host] {
  const matches = Object.entries(hosts).filter(
    ([key, h]) => key === name || h.nixosHostName === name,
  );
  if (matches.length !== 1)
    throw new ManagerError({
      message: `Unknown or ambiguous host: ${name}. Available: ${Object.keys(hosts).join(", ")}`,
    });
  return matches[0];
}
const nixFlags = ["--log-format", "internal-json", "--print-out-paths"];
export function plan(config: Config, r: Request, review?: BuildReview): Plan {
  validate(r);
  const [, h] = resolveHost(config.hosts, r.host || config.hostKey);
  const steps: Command[] = [];
  const notes: string[] = [];
  const add = (label: string, argv: string[], options: Partial<Command> = {}) =>
    steps.push({ label, argv, cwd: config.flake, ...options });
  const privileged = (label: string, ...args: string[]) =>
    add(label, [...config.privileged, ...args]);
  const home = r.scope !== "system",
    system = r.scope !== "home";
  // Edited reviews build a copy of the flake's files with the edits applied.
  const flake =
    r.action === "review-build" && r.edits
      ? `path:${reviewRoot(config, r.reviewId ?? "<new>")}/source`
      : config.flake;
  const homeAttr = `${flake}#homeConfigurations.${attr(h.homeConfiguration)}.activationPackage`;
  const systemAttr = `${flake}#nixosConfigurations.${attr(h.nixosConfiguration)}.config.system.build.toplevel`;
  const build = (
    label: string,
    installable: string,
    link: string | undefined,
    extra: readonly string[] = [],
  ) =>
    add(
      label,
      [
        "nix",
        "build",
        ...(link ? ["--out-link", link] : ["--no-link"]),
        ...nixFlags,
        ...extra,
        installable,
        ...r.extra,
      ],
      { progress: true },
    );
  const activateHome = (output: string) => {
    add("Select Home Manager generation", [
      "nix-env",
      "--profile",
      config.homeProfile,
      "--set",
      output,
    ]);
    add("Activate Home Manager", [`${output}/activate`, "--driver-version", "1"], {
      env: { HOME_MANAGER_BACKUP_EXT: "hm-backup" },
    });
  };
  const activateSystem = (output: string, mode: "switch" | "boot") =>
    privileged(
      mode === "boot" ? "Activate system on next boot" : "Activate system",
      "activate-output",
      mode,
      output,
    );
  const cleanup = () => {
    const retention = r.retention ?? 3;
    if (home && config.homeProfileExists)
      add(`Keep ${retention} home generations`, [
        "nix-env",
        "--profile",
        config.homeProfile,
        "--delete-generations",
        `+${retention}`,
      ]);
    else if (home)
      notes.push(
        `No Home Manager profile at ${config.homeProfile}; generation deletion is skipped.`,
      );
    if (system)
      privileged(
        `Keep ${retention} system generations and collect garbage`,
        "gc",
        String(retention),
      );
    else add("Collect unreferenced store paths", ["nix-store", "--gc"]);
    notes.push(
      "Garbage collection deletes unreferenced store paths and older generations; these cannot be rolled back afterward.",
    );
  };

  if (r.action === "review-build") {
    const root = reviewRoot(config, r.reviewId ?? "<new>");
    if (r.generation !== undefined) {
      const profile = home ? config.homeProfile : systemProfile;
      build(
        `Retain ${home ? "home" : "system"} generation ${r.generation}`,
        `${profile}-${r.generation}-link`,
        `${root}/${r.scope}`,
      );
    } else {
      if (r.edits) {
        add("Stage the edited files", [], { internal: "stage-sources" });
        add("Check the edits", [], { internal: "verify-edits" });
      }
      const lock = r.update || r.inputs?.length ? `${root}/flake.lock` : undefined;
      if (lock)
        add(
          r.inputs?.length
            ? `Resolve ${r.inputs.length === 1 ? r.inputs[0] : `${r.inputs.length} inputs`}`
            : "Resolve all flake inputs",
          [
            "nix",
            "flake",
            "update",
            ...(r.inputs ?? []),
            "--flake",
            config.flake,
            "--output-lock-file",
            lock,
            "--log-format",
            "internal-json",
          ],
          { progress: true },
        );
      const lockFlags = lock
        ? ["--reference-lock-file", lock, "--no-write-lock-file"]
        : ["--no-write-lock-file"];
      if (home) build("Build home configuration", homeAttr, `${root}/home`, lockFlags);
      if (system)
        build("Build system configuration", systemAttr, `${root}/system`, lockFlags);
      if (lock)
        notes.push(
          "Inputs are resolved into a separate lock file. The checkout's flake.lock changes only when you apply this review.",
        );
      if (r.edits)
        notes.push("Edits are built from a copy of the checkout. Your files change only when you apply this review.");
    }
    add("Compare with the active configuration", [], { internal: "compare" });
    notes.push("Nothing is activated. The build is kept until you apply or discard it.");
    return { steps, notes };
  }
  if (r.action === "review-apply") {
    if (!review || review.id !== r.reviewId)
      throw new ManagerError({ message: "The selected review is unavailable." });
    const mode = r.activation ?? "switch";
    if (review.edits)
      add(
        `Write ${review.edits.files.length === 1 ? review.edits.files[0].path : `${review.edits.files.length} edited files`}`,
        [],
        { internal: "write-sources" },
      );
    if (review.inputs)
      add("Write the reviewed flake.lock", [], { internal: "write-lock" });
    if (home && review.home && mode === "switch") activateHome(review.home.path);
    if (system && review.system) activateSystem(review.system.path, mode);
    if (!steps.some((step) => !step.internal))
      throw new ManagerError({
        message:
          mode === "boot"
            ? "Only a system configuration can be applied on next boot."
            : "This review does not contain the selected configuration.",
      });
    if (steps.some((s) => s.label.startsWith("Activate Home")) && steps.some((s) => s.label.startsWith("Activate system")))
      notes.push("Home activates before system. If system activation fails, home remains active.");
    notes.push("The exact reviewed outputs are activated; the checkout is not evaluated again.");
    return { steps, notes };
  }
  if (r.update)
    add("Update flake inputs", [
      "nix",
      "flake",
      "update",
      "--flake",
      config.flake,
    ]);
  if (r.action === "check") {
    notes.push(
      "Resolve inputs in a temporary copy and compare stable package releases. The checkout is unchanged.",
    );
  } else if (isPackage(r.action)) {
    notes.push(
      "Compare stable release and inherited recipes, discover hashes, then build before atomically replacing the pin.",
    );
  } else {
    if (r.action === "build") {
      if (home) build("Build home configuration", homeAttr, undefined);
      if (system) build("Build system configuration", systemAttr, undefined);
    }
    if (r.action === "switch" || r.action === "boot") {
      const root = buildRoot(config);
      if (home) build("Build home configuration", homeAttr, `${root}/home`);
      if (system) build("Build system configuration", systemAttr, `${root}/system`);
      if (home) activateHome(`${root}/home`);
      if (system) activateSystem(`${root}/system`, r.action);
      if (home && system)
        notes.push(
          "Home activates before system. If system activation fails, home may already be active.",
        );
      if (system)
        notes.push(
          "Once the privileged step starts, cancellation waits for it to finish before stopping subsequent steps.",
        );
    } else if (r.action === "rollback") {
      if (home)
        add("Roll back home configuration", [
          "home-manager",
          "switch",
          "--rollback",
          ...r.extra,
        ]);
      if (system) privileged("Roll back system configuration", "rollback");
    }
    if (r.cleanup || r.action === "gc") cleanup();
  }
  return { steps, notes };
}
export const formatCommand = (c: Command) =>
  c.internal
    ? "(performed by Nix Manager)"
    : c.argv
        .map((a) =>
          /^[\w@%+=:,./#-]+$/.test(a) ? a : `'${a.replaceAll("'", "'\\''")}'`,
        )
        .join(" ");
