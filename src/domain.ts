import { Data, Schema } from "effect";

export class ManagerError extends Data.TaggedError("ManagerError")<{
  message: string;
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
  fingerprint: Schema.optional(Schema.String),
  retention: Schema.optional(Schema.Number),
  generation: Schema.optional(Schema.Number),
  homeOutput: Schema.optional(Schema.String),
  systemOutput: Schema.optional(Schema.String),
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
export const isPackage = (action: string) => action.endsWith("-update");
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
  const retention = r.retention ?? 3;
  if (!Number.isInteger(retention) || retention < 1 || retention > 100)
    fail("Retention must be between 1 and 100 generations.");
  if (["review-build", "review-apply"].includes(r.action)) {
    if (!r.reviewId || !/^[a-f0-9-]{8,64}$/.test(r.reviewId))
      fail("A valid review ID is required.");
    if (!r.fingerprint) fail("A repository fingerprint is required.");
    if (r.update || r.cleanup || r.extra.length)
      fail("Reviewed workflows cannot combine with modifiers or extra arguments.");
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
export interface Command {
  label: string;
  argv: readonly string[];
  cwd?: string;
  env?: Record<string, string>;
  allowFailure?: boolean;
  quiet?: boolean;
}
export interface Plan {
  steps: Command[];
  notes: string[];
}
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
export function plan(config: Config, r: Request): Plan {
  validate(r);
  const [, h] = resolveHost(config.hosts, r.host || config.hostKey);
  const steps: Command[] = [];
  const notes: string[] = [];
  const add = (label: string, ...argv: string[]) =>
    steps.push({ label, argv, cwd: config.flake });
  const privileged = (label: string, ...args: string[]) =>
    add(label, ...config.privileged, ...args);
  const home = r.scope !== "system",
    system = r.scope !== "home";
  if (r.action === "review-build") {
    const root = `${config.stateDir}/reviews/${r.reviewId}`;
    if (home)
      add(
        "Build reviewed home output",
        "nix",
        "build",
        "--no-write-lock-file",
        "--out-link",
        `${root}/home`,
        "--print-out-paths",
        `${config.flake}#homeConfigurations.${h.homeConfiguration}.activationPackage`,
      );
    if (system)
      add(
        "Build reviewed system output",
        "nix",
        "build",
        "--no-write-lock-file",
        "--out-link",
        `${root}/system`,
        "--print-out-paths",
        `${config.flake}#nixosConfigurations.${h.nixosConfiguration}.config.system.build.toplevel`,
      );
    notes.push("The resulting store paths are retained by GC roots until this review is removed.");
    return { steps, notes };
  }
  if (r.action === "review-apply") {
    if (home && r.homeOutput) {
      add("Select reviewed home generation", "nix-env", "--profile", config.homeProfile, "--set", r.homeOutput);
      add("Activate reviewed home output", `${r.homeOutput}/activate`, "-b", "hm-backup");
    }
    if (system && r.systemOutput)
      privileged("Activate reviewed system output", "activate-output", "switch", r.systemOutput);
    if (!steps.length)
      throw new ManagerError({ message: "This review does not contain the selected outputs." });
    notes.push("These exact retained outputs will be activated without reevaluating the checkout.");
    return { steps, notes };
  }
  if (r.update)
    add(
      "Update flake inputs",
      "nix",
      "flake",
      "update",
      "--flake",
      config.flake,
    );
  if (r.action === "check") {
    notes.push(
      "Resolve inputs in a temporary copy and compare stable package releases. The checkout is unchanged.",
    );
  } else if (isPackage(r.action)) {
    notes.push(
      "Compare stable release and inherited recipes, discover hashes, then build before atomically replacing the pin.",
    );
  } else {
    if (["build", "switch", "boot"].includes(r.action)) {
      if (home)
        add(
          "Build home configuration",
          "home-manager",
          "build",
          "--flake",
          `${config.flake}#${h.homeConfiguration}`,
          "--no-out-link",
          ...r.extra,
        );
      if (system)
        add(
          "Build system configuration",
          "nixos-rebuild",
          "build",
          "--flake",
          `${config.flake}#${h.nixosConfiguration}`,
          "--no-link",
          ...r.extra,
        );
    }
    if (r.action === "switch" || r.action === "boot") {
      if (home)
        add(
          "Activate home configuration",
          "home-manager",
          "switch",
          "--flake",
          `${config.flake}#${h.homeConfiguration}`,
          "-b",
          "hm-backup",
          ...r.extra,
        );
      if (system)
        privileged(
          r.action === "boot"
            ? "Activate system on next boot"
            : "Activate system configuration",
          r.action,
          "--flake",
          `${config.flake}#${h.nixosConfiguration}`,
          ...r.extra,
        );
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
        add(
          "Roll back home configuration",
          "home-manager",
          "switch",
          "--rollback",
          ...r.extra,
        );
      if (system)
        privileged("Roll back system configuration", "rollback", ...r.extra);
    }
    if (r.cleanup || r.action === "gc") {
      if (home && config.homeProfileExists)
        add(
          "Retain three home generations",
          "nix-env",
          "--profile",
          config.homeProfile,
          "--delete-generations",
          `+${r.retention ?? 3}`,
        );
      else if (home)
        notes.push(
          `No Home Manager profile at ${config.homeProfile}; generation deletion is skipped.`,
        );
      if (system)
        privileged(
          `Retain ${r.retention ?? 3} system generations and collect garbage`,
          "gc",
          String(r.retention ?? 3),
        );
      else add("Collect unreferenced store paths", "nix-store", "--gc");
      notes.push(
        "Garbage collection deletes unreferenced store paths and older generations; these cannot be rolled back afterward.",
      );
    }
  }
  return { steps, notes };
}
export const formatCommand = (c: Command) =>
  c.argv
    .map((a) =>
      /^[\w@%+=:,./#-]+$/.test(a) ? a : `'${a.replaceAll("'", "'\\''")}'`,
    )
    .join(" ");
