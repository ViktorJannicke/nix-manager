import { Effect } from "effect";
import type { Config } from "./domain";
import { git } from "./observe";
import { command } from "./process";

/** Marks the commits Nix Manager makes, so the history can tell them apart. */
const trailer = "Applied-with";

/** A commit message from what a change did: the first part as subject, every part in the body. */
export function changeMessage(changes: readonly string[]) {
  const subject = changes.length > 1 ? `${changes[0]} and ${changes.length - 1} more` : (changes[0] ?? "Apply the configuration");
  return changes.length > 1 ? `${subject}\n\n${changes.map((change) => `- ${change}`).join("\n")}` : subject;
}

const lastLine = (text: string) => text.trim().split("\n").at(-1) ?? "";

/**
 * Commits exactly the files an apply wrote, with a message saying what it did,
 * and pushes when `backup` is on and the branch has an upstream. Never fails:
 * the change is applied already, so it returns what happened, in words, or
 * nothing when the files did not change.
 */
export const recordChange = (config: Config, paths: readonly string[], changes: readonly string[], backup: boolean) =>
  Effect.gen(function* () {
    const run = (label: string, args: string[], env?: Record<string, string>) =>
      command(label, ["git", "-C", config.flake, ...args], { allowFailure: true, env });
    if ((yield* run("Find the Git repository", ["rev-parse", "--is-inside-work-tree"])).code !== 0)
      return "The configuration is not in a Git repository, so this change is not kept in a history.";
    const added = yield* run("Stage the changed files", ["add", "--all", "--", ...paths]);
    if (added.code !== 0) return `The change is applied, but not kept in the history: ${lastLine(added.stderr)}`;
    const head = (yield* run("Find the last commit", ["rev-parse", "--verify", "--quiet", "HEAD"])).code === 0;
    if (head && (yield* run("Look for changes", ["diff", "--cached", "--quiet", "HEAD", "--", ...paths])).code === 0)
      return undefined;
    // Without a Git identity, commits are made in Nix Manager's name.
    const identity = (yield* run("Read the Git identity", ["config", "user.email"])).stdout.trim()
      ? []
      : ["-c", "user.name=Nix Manager", "-c", "user.email=nix-manager@localhost"];
    const message = changeMessage(changes);
    const committed = yield* run("Keep the change in the history", [
      ...identity,
      "commit",
      "--quiet",
      "--only",
      "--message",
      message,
      "--trailer",
      `${trailer}: Nix Manager`,
      "--",
      ...paths,
    ]);
    if (committed.code !== 0) return `The change is applied, but not kept in the history: ${lastLine(committed.stderr || committed.stdout)}`;
    const recorded = `Kept "${message.split("\n")[0]}" in the history.`;
    if (!backup) return recorded;
    const upstream = yield* run("Find the backup", ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"]);
    if (upstream.code !== 0) return `${recorded} It is not backed up, because the branch has no remote.`;
    const pushed = yield* run("Back up the configuration", ["push", "--quiet"], { GIT_TERMINAL_PROMPT: "0" });
    return pushed.code === 0
      ? `${recorded} Backed up to ${upstream.stdout.trim()}.`
      : `${recorded} The backup failed: ${lastLine(pushed.stderr)}`;
  });

export interface HistoryEntry {
  revision: string;
  subject: string;
  /** The rest of the message, such as every part of a change. */
  body: string;
  date: string;
  author: string;
  /** Made by Nix Manager after applying a change. */
  applied: boolean;
}

/** The newest commits of the configuration, or none outside a Git repository. */
export async function readHistory(flake: string, limit = 50): Promise<HistoryEntry[]> {
  const format = ["%H", "%s", "%cI", "%an", `%(trailers:key=${trailer},valueonly,separator=%x20)`, "%b"].join("%x00");
  const text = await git(flake, "log", `--max-count=${limit}`, `--format=${format}%x1e`);
  return (text ?? "")
    .split("\x1e")
    .map((record) => record.replace(/^\n/, ""))
    .filter(Boolean)
    .map((record) => {
      const [revision, subject, date, author, applied, body] = record.split("\0");
      return {
        revision,
        subject,
        body: (body ?? "").replace(new RegExp(`^${trailer}:.*$`, "m"), "").trim(),
        date,
        author,
        applied: Boolean(applied?.trim()),
      };
    });
}
