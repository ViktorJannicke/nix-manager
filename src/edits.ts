import { existsSync, readFileSync, statSync } from "node:fs";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { isDeepStrictEqual } from "node:util";
import { Effect } from "effect";
import { ManagerError, reviewRoot, type Config, type Request } from "./domain";
import type { EditEffect, EditedPackage, ReviewEdits } from "./contracts";
import { changeHash, renderDraft, sourcePath, subject, type Change, type EditedFile } from "./draft";
import { hashText } from "./observe";
import { flakeSource } from "./packages";
import { attempt, command } from "./process";
import { atomicWrite } from "./reviews";
import { configurationAttr, evaluateState, type ConfigurationState, type Target } from "./software";

/** Splits `pkgs.kdePackages."7zz"` into its names. */
export function splitAttr(text: string): string[] | undefined {
  const names: string[] = [];
  const pattern = /("(?:[^"\\]|\\.)*"|[A-Za-z_][A-Za-z0-9_'-]*)(\.|$)/y;
  let index = 0;
  while (index < text.length) {
    pattern.lastIndex = index;
    const match = pattern.exec(text);
    if (!match) return undefined;
    names.push(match[1].startsWith('"') ? JSON.parse(match[1]) : match[1]);
    index = pattern.lastIndex;
    if (!match[2] && index < text.length) return undefined;
  }
  return names.length ? names : undefined;
}

/** The flake snapshot an edited review was staged from, and the files it changed. */
export interface Staged {
  original: string;
  files: EditedFile[];
}

/**
 * Copies the flake's tracked files into the review and applies the edits to
 * the copy. Each edited file must be visible to Nix and match the checkout.
 */
export const stageSources = (config: Config, r: Request, changes: readonly Change[]) =>
  Effect.gen(function* () {
    if (!changes.length)
      return yield* Effect.fail(new ManagerError({ message: "There are no pending edits." }));
    for (const change of changes)
      if ("attr" in change && splitAttr(change.attr)?.[0] !== "pkgs")
        return yield* Effect.fail(
          new ManagerError({ message: `${change.attr} is not a package from pkgs.` }),
        );
    const { files, problems } = yield* Effect.promise(() => renderDraft(config.flake, changes));
    if (problems.length)
      return yield* Effect.fail(
        new ManagerError({
          message: `${subject(changes[problems[0].index])}: ${problems[0].message}${problems.length > 1 ? ` (and ${problems.length - 1} more problems)` : ""}`,
        }),
      );
    if (!files.length)
      return yield* Effect.fail(new ManagerError({ message: "The pending edits do not change any file." }));
    const original = yield* flakeSource(config.flake);
    for (const file of files) {
      const tracked = yield* Effect.promise(() =>
        readFile(join(original, file.path), "utf8").catch(() => undefined),
      );
      if (tracked === undefined)
        return yield* Effect.fail(
          new ManagerError({
            message: `${file.path} is not tracked by Git, so Nix cannot read it. Add it with git add first.`,
          }),
        );
      if (hashText(tracked) !== file.baseHash)
        return yield* Effect.fail(
          new ManagerError({ message: `${file.path} changed while the edits were being prepared.` }),
        );
    }
    const copy = join(reviewRoot(config, r.reviewId!), "source");
    yield* command("Copy flake source", ["cp", "-rT", "--no-preserve=mode", original, copy], { quiet: true });
    for (const file of files)
      yield* attempt(`Write ${file.path}`, () => writeFile(join(copy, file.path), file.after));
    return { original, files } satisfies Staged;
  });

const packages = (state: ConfigurationState) =>
  state.packages.flatMap(({ file, items }) => items.map((item): EditedPackage => ({ ...item, file })));

/** Packages in `a` that `b` lacks, counting duplicates. */
const minus = (a: EditedPackage[], b: EditedPackage[]) => {
  const rest = b.map((item) => `${item.file}\0${item.out}`);
  return a.filter((item) => {
    const index = rest.indexOf(`${item.file}\0${item.out}`);
    if (index < 0) return true;
    rest.splice(index, 1);
    return false;
  });
};

const includes = (after: unknown, wanted: unknown) =>
  isDeepStrictEqual(after, wanted) ||
  // List options merge every definition; the requested items must be present.
  (Array.isArray(after) && Array.isArray(wanted) && wanted.every((item) => after.some((x) => isDeepStrictEqual(x, item))));

const show = (value: unknown) => {
  const text = JSON.stringify(value);
  return text === undefined ? "nothing" : text.length > 80 ? `${text.slice(0, 77)}...` : text;
};

/**
 * Evaluates every edited configuration before and after the edits. Each change
 * must have its effect in the configuration that is being reviewed: a package
 * appears in (or leaves) its file's list, and an option takes the new value.
 */
export const verifyEdits = (config: Config, r: Request, staged: Staged, changes: readonly Change[]) =>
  Effect.gen(function* () {
    const edited = yield* flakeSource(`path:${join(reviewRoot(config, r.reviewId!), "source")}`);
    const scopes = (["home", "system"] as const).filter((scope) =>
      changes.some((change) => change.scope === scope),
    );
    if (scopes.some((scope) => scope !== r.scope && r.scope !== "all"))
      return yield* Effect.fail(
        new ManagerError({ message: "The pending edits change a configuration this review does not build." }),
      );
    const host = r.host || config.hostKey;
    const effects = yield* Effect.forEach(
      scopes,
      (scope: Target) =>
        Effect.gen(function* () {
          const own = changes.filter((change) => change.scope === scope);
          const options = own.flatMap((change) => ("option" in change ? [change.option] : []));
          const attrs = own.flatMap((change) => ("attr" in change ? [splitAttr(change.attr)!.slice(1)] : []));
          const attribute = configurationAttr(config, host, scope);
          const [before, after] = yield* Effect.all(
            [
              evaluateState(staged.original, attribute, scope, { options, attrs }),
              evaluateState(edited, attribute, scope, { options, attrs }),
            ],
            { concurrency: 2 },
          );
          const added = minus(packages(after), packages(before));
          const removed = minus(packages(before), packages(after));
          const name = scope === "home" ? `${host}'s home configuration` : `${host}'s system`;
          let index = 0;
          for (const change of own) {
            if ("attr" in change) {
              const position = index++;
              const out = (change.kind === "add-package" ? after : before).resolved[position];
              if (!out)
                return yield* Effect.fail(
                  new ManagerError({ message: `${change.attr} does not exist in ${name}'s packages.` }),
                );
              const list = change.kind === "add-package" ? added : removed;
              if (!list.some((item) => item.out === out && item.file === change.file))
                return yield* Effect.fail(
                  new ManagerError({
                    message:
                      change.kind === "add-package"
                        ? `${change.attr} was added to ${change.file}, but ${name} does not include it. Is the file part of this configuration?`
                        : `Removing ${change.attr} from ${change.file} does not remove it from ${name}.`,
                  }),
                );
              continue;
            }
            const now = after.options[change.option];
            if (!now?.exists)
              return yield* Effect.fail(
                new ManagerError({ message: `${change.option} is not an option of ${name}.` }),
              );
            if (change.kind === "set-option") {
              if (now.error || !includes(now.value, change.value))
                return yield* Effect.fail(
                  new ManagerError({
                    message: now.error
                      ? `${change.option} no longer evaluates after the edit.`
                      : `${change.option} is ${show(now.value)} instead of ${show(change.value)}. Another definition takes precedence, or ${change.file} is not part of ${name}.`,
                  }),
                );
              if (!now.files.includes(change.file))
                return yield* Effect.fail(
                  new ManagerError({ message: `${change.file} is not part of ${name}, so ${change.option} is unchanged.` }),
                );
            } else if (now.files.includes(change.file))
              return yield* Effect.fail(
                new ManagerError({ message: `${change.option} is still set in ${change.file}.` }),
              );
          }
          return {
            scope,
            added,
            removed,
            options: options.map((option) => ({
              option,
              before: before.options[option]?.value,
              after: after.options[option]?.value,
            })),
          } satisfies EditEffect;
        }),
      { concurrency: 2 },
    );
    return {
      changes: [...changes],
      hash: changeHash(changes),
      files: staged.files.map(({ path, baseHash, hunks }) => ({ path, baseHash, hunks })),
      effects,
    } satisfies ReviewEdits;
  });

/** Writes a review's edited files into the checkout unless any changed since. */
export const writeSources = (config: Config, reviewId: string, edits: ReviewEdits) =>
  Effect.try({
    try: () => {
      const root = join(reviewRoot(config, reviewId), "source");
      const writes = edits.files.map((file) => {
        const path = sourcePath(config.flake, file.path);
        const current = existsSync(path) ? readFileSync(path, "utf8") : undefined;
        if (current === undefined || hashText(current) !== file.baseHash)
          throw new ManagerError({
            message: `${file.path} changed after this review was prepared. Prepare the edits again.`,
          });
        return { path, data: readFileSync(join(root, file.path)), mode: statSync(path).mode & 0o777 };
      });
      for (const write of writes) atomicWrite(write.path, write.data, write.mode);
    },
    catch: (error) =>
      error instanceof ManagerError ? error : new ManagerError({ message: `Write edited files: ${error}` }),
  });

/** The draft without the changes a review applied. */
export const remaining = (draft: readonly Change[], applied: readonly Change[]) => {
  const rest = applied.map((change) => JSON.stringify(change));
  return draft.filter((change) => {
    const index = rest.indexOf(JSON.stringify(change));
    if (index < 0) return true;
    rest.splice(index, 1);
    return false;
  });
};
