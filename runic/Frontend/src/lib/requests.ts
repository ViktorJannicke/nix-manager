import type { Command, Operation, Request } from "../generated/types.js";

/** The actions the C# backend runs; review, software and pin updates follow with the remaining backend. */
export type Action = "build" | "switch" | "boot" | "rollback" | "gc" | "check";
export type Scope = "home" | "system" | "all";

/** A request with the defaults of `request()` in `src/domain.ts`; the Bridge sends every member. */
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
  reviewId: null,
  generation: null,
  inputs: null,
  activation: null,
  edits: null,
  undo: null,
  then: null,
  ...overrides,
});

/** Whether two requests ask for the same thing, so a dry run answers the confirmation that asked for it. */
export const sameRequest = (a: Request, b: Request) => {
  const text = (r: Request) => JSON.stringify(Object.entries(r).sort(([x], [y]) => x.localeCompare(y)));
  return text(a) === text(b);
};

/** Actions that review edits first, and package pin updates: their backend is not ported yet. */
export const unported = (action: string) => action.startsWith("review-") || action.endsWith("-update");

/** A command line as a shell would read it, or a note for a step Nix Manager performs itself. */
export const formatCommand = (command: Command, internal: string) =>
  command.internal
    ? internal
    : command.argv.map((a) => (/^[\w@%+=:,./#-]+$/.test(a) ? a : `'${a.replaceAll("'", "'\\''")}'`)).join(" ");

const sameKind = (a: Request, b: Request) =>
  a.action === b.action && a.scope === b.scope && a.update === b.update && !!a.edits === !!b.edits && !a.dryRun;

/** Typical seconds for `request`: the median of the last five earlier runs of the same kind, if there were two. */
export function usualSeconds(operations: readonly Operation[], request: Request) {
  const seconds = operations
    .filter((operation) => operation.status === "succeeded" && operation.finishedAt && sameKind(operation.request, request))
    .sort((a, b) => b.startedAt.localeCompare(a.startedAt))
    .slice(0, 5)
    .map((operation) => (Date.parse(operation.finishedAt!) - Date.parse(operation.startedAt)) / 1000)
    .sort((a, b) => a - b);
  if (seconds.length < 2) return undefined;
  const middle = Math.floor(seconds.length / 2);
  return seconds.length % 2 ? seconds[middle] : (seconds[middle - 1] + seconds[middle]) / 2;
}
