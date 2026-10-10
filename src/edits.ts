import { existsSync, mkdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { isDeepStrictEqual } from "node:util";
import { Effect } from "effect";
import { ManagerError, reviewRoot, type Config, type Request } from "./domain";
import type { EditEffect, EditedPackage, ReviewEdits, UndoContents, UndoPoint } from "./contracts";
import { changeHash, renderDraft, sourcePath, subject, type Change, type EditedFile } from "./draft";
import { unreadOptions } from "./curated";
import { explain } from "./explain";
import { attr } from "./nix";
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
 * the copy. Each edited file must be visible to Nix and match the checkout;
 * a created file must not exist yet.
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
      if (file.created) {
        if (existsSync(join(original, file.path)) || existsSync(join(config.flake, file.path)))
          return yield* Effect.fail(new ManagerError({ message: `${file.path} already exists.` }));
        continue;
      }
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
      yield* attempt(`Write ${file.path}`, async () => {
        await mkdir(dirname(join(copy, file.path)), { recursive: true });
        await writeFile(join(copy, file.path), file.after);
      });
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

// lib.nixosSystem and homeManagerConfiguration attribute modules written inline
// in the flake to the input's own flake.nix.
const origin = (file: string) =>
  /^\/nix\/store\/[a-z0-9]{32}-source\/flake\.nix$/.test(file) ? "a module written directly in flake.nix" : file;
const joined = (items: readonly string[]) =>
  items.length <= 1 ? (items[0] ?? "") : `${items.slice(0, -1).join(", ")} and ${items.at(-1)}`;
const sentence = (text: string) => `${text.charAt(0).toUpperCase()}${text.slice(1)}`;

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
          // Package settings must not replace the package set the flake builds.
          const settings = options.filter((option) => option.startsWith("nixpkgs.config.")).map((option) => option.split(".")[2]);
          const nixpkgs = settings.length > 0;
          // Some options cannot be read before the feature they belong to is on.
          const unread = unreadOptions();
          const [before, after] = yield* Effect.all(
            [
              evaluateState(staged.original, attribute, scope, { options: options.filter((option) => !unread.has(option)), attrs, nixpkgs }),
              evaluateState(edited, attribute, scope, { options, attrs, nixpkgs }),
            ],
            { concurrency: 2 },
          );
          const added = minus(packages(after), packages(before));
          const removed = minus(packages(before), packages(after));
          const name = scope === "home" ? `${host}'s home configuration` : `${host}'s system`;
          if (before.nixpkgs && after.nixpkgs) {
            const keys = new Set([...Object.keys(before.nixpkgs.config), ...Object.keys(after.nixpkgs.config)]);
            const changed = [...keys].filter(
              (key) => !key.startsWith("_") && !settings.includes(key) && !isDeepStrictEqual(before.nixpkgs!.config[key], after.nixpkgs!.config[key]),
            );
            if (before.nixpkgs.names !== after.nixpkgs.names) changed.push("the packages the flake adds");
            if (changed.length)
              return yield* Effect.fail(
                new ManagerError({
                  message: `Setting nixpkgs.config in ${name} also changes ${changed.join(", ")}.`,
                  explanation: {
                    title: "This setting would change more than it says",
                    body: `In ${name}, nixpkgs.config in a module makes the configuration import nixpkgs again, which also changes ${changed.join(", ")}. Set ${settings.join(", ")} where the flake imports nixpkgs instead, for example import nixpkgs { config.${settings[0]} = true; } in flake.nix.`,
                  },
                }),
              );
          }
          // Settings are checked first: a setting without effect explains a
          // package that is still refused better than the refusal does.
          for (const change of own) {
            // A created file is checked through the settings and packages written into it.
            if ("attr" in change || change.kind === "create-module") continue;
            const now = after.options[change.option];
            // Removing an entry, such as a user from users.users, leaves no setting behind.
            if (change.kind === "unset-option" && !now?.exists && before.options[change.option]?.exists) continue;
            if (!now?.exists)
              return yield* Effect.fail(
                new ManagerError({ message: `${change.option} is not an option of ${name}.` }),
              );
            if (change.kind === "set-option") {
              if (now.error)
                return yield* Effect.fail(
                  new ManagerError({ message: `${change.option} no longer evaluates after the edit.` }),
                );
              const others = now.files.filter((file) => file !== change.file);
              if (!includes(now.value, change.value))
                return yield* Effect.fail(
                  new ManagerError({
                    message: `${change.option} is ${show(now.value)} instead of ${show(change.value)}. Another definition takes precedence, or ${change.file} is not part of ${name}.`,
                    explanation:
                      now.files.includes(change.file) && others.length
                        ? {
                            title: `${change.option} is also set elsewhere`,
                            body: `${sentence(joined(others.map(origin)))} ${others.length === 1 ? "sets" : "set"} ${change.option} too, and that value wins. Change it there instead of adding a new definition.`,
                          }
                        : undefined,
                  }),
                );
              if (!now.files.includes(change.file))
                return yield* Effect.fail(
                  new ManagerError({ message: `${change.file} is not part of ${name}, so ${change.option} is unchanged.` }),
                );
              if ("effective" in now && !includes(now.effective, change.value)) {
                const setting = change.option.slice("nixpkgs.config.".length);
                return yield* Effect.fail(
                  new ManagerError({
                    message: `${change.option} is set in ${change.file}, but ${name} takes its packages from a nixpkgs the flake imports itself.`,
                    explanation: {
                      title: "This setting does not reach your packages",
                      body: `${name} gets its packages from a nixpkgs that the flake imports itself, so nixpkgs.config in modules has no effect. Set ${setting} where nixpkgs is imported instead, for example import nixpkgs { config.${setting} = ${show(change.value)}; } in flake.nix.`,
                    },
                  }),
                );
              }
            } else if (now.files.includes(change.file))
              return yield* Effect.fail(
                new ManagerError({ message: `${change.option} is still set in ${change.file}.` }),
              );
          }
          let index = 0;
          for (const change of own) {
            if (!("attr" in change)) continue;
            const position = index++;
            const out = (change.kind === "add-package" ? after : before).resolved[position];
            if (!out) {
              // Evaluating it again without tryEval yields Nix's reason, such as an unfree license.
              const path = splitAttr(change.attr)!.slice(1).map(attr).join(".");
              const result = yield* command(
                "Evaluate package",
                ["nix", "eval", "--raw", `path:${change.kind === "add-package" ? edited : staged.original}#${attribute}.pkgs.${path}.outPath`],
                { quiet: true, allowFailure: true },
              );
              const found = explain(result.stderr);
              const explanation = found?.fix ? { ...found, fix: { ...found.fix, scope } } : found;
              return yield* Effect.fail(
                new ManagerError({
                  message: explanation
                    ? `${change.attr} cannot be added to ${name}: ${explanation.title}.`
                    : `${change.attr} does not exist in ${name}'s packages.`,
                  explanation,
                }),
              );
            }
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
      files: staged.files.map(({ path, baseHash, created, hunks }) => ({ path, baseHash, created, hunks })),
      effects,
    } satisfies ReviewEdits;
  });

/**
 * Writes a review's edited files into the checkout unless any changed since.
 * Created files are added to Git's index as intended, so the flake sees them.
 */
export const writeSources = (config: Config, reviewId: string, edits: ReviewEdits) =>
  Effect.gen(function* () {
    yield* Effect.try({
      try: () => {
        const root = join(reviewRoot(config, reviewId), "source");
        const writes = edits.files.map((file) => {
          const path = sourcePath(config.flake, file.path);
          const current = existsSync(path) ? readFileSync(path, "utf8") : undefined;
          if (file.created ? current !== undefined : current === undefined || hashText(current) !== file.baseHash)
            throw new ManagerError({
              message: file.created
                ? `${file.path} was created after this review was prepared. Prepare the edits again.`
                : `${file.path} changed after this review was prepared. Prepare the edits again.`,
            });
          return {
            path,
            data: readFileSync(join(root, file.path)),
            mode: file.created ? 0o644 : statSync(path).mode & 0o777,
          };
        });
        for (const write of writes) {
          mkdirSync(dirname(write.path), { recursive: true });
          atomicWrite(write.path, write.data, write.mode);
        }
      },
      catch: (error) =>
        error instanceof ManagerError ? error : new ManagerError({ message: `Write edited files: ${error}` }),
    });
    const created = edits.files.filter((file) => file.created).map((file) => file.path);
    if (created.length && existsSync(join(config.flake, ".git")))
      yield* command("Add created files to Git", ["git", "-C", config.flake, "add", "--intent-to-add", "--", ...created], {
        quiet: true,
      });
  });

/** A module file of the checkout, or its flake.lock. */
const checkoutPath = (config: Config, path: string) =>
  path === "flake.lock" ? join(config.flake, path) : sourcePath(config.flake, path);

/** Checkout files as they are now, keyed by flake-relative path; absent files are undefined. */
export const readSources = (config: Config, paths: readonly string[]) =>
  new Map(
    paths.map((path) => {
      const file = checkoutPath(config, path);
      return [path, existsSync(file) ? readFileSync(file, "utf8") : undefined] as const;
    }),
  );

/** What writing the files in `before` replaced: their earlier content, and a hash of each file now. */
export const undoFiles = (config: Config, before: Map<string, string | undefined>) => {
  const contents: UndoContents = {};
  const files = [...readSources(config, [...before.keys()])].map(([path, now]): UndoPoint["files"][number] => {
    const earlier = before.get(path);
    if (earlier !== undefined) contents[path] = earlier;
    return { path, created: earlier === undefined, written: now === undefined ? null : hashText(now) };
  });
  return { files, contents };
};

/**
 * Puts back the files an applied change wrote, unless any changed since.
 * Files the change created are deleted and leave Git's index.
 */
export const restoreSources = (config: Config, point: UndoPoint, contents: UndoContents) =>
  Effect.gen(function* () {
    yield* Effect.try({
      try: () => {
        const now = readSources(config, point.files.map((file) => file.path));
        for (const file of point.files) {
          if (!file.created && contents[file.path] === undefined)
            throw new ManagerError({ message: `The earlier content of ${file.path} is missing.` });
          const current = now.get(file.path);
          if (current === undefined ? file.written !== null : hashText(current) !== file.written)
            throw new ManagerError({
              message: `${file.path} changed after the change was applied, so it cannot be put back automatically.`,
              explanation: {
                title: `${file.path} was edited since`,
                body: "Undo puts back the files exactly as they were and would lose those edits. Go back on the Generations page instead, which leaves your files as they are.",
              },
            });
        }
        for (const file of point.files) {
          const path = checkoutPath(config, file.path);
          if (file.created) rmSync(path, { force: true });
          else {
            mkdirSync(dirname(path), { recursive: true });
            atomicWrite(path, contents[file.path], existsSync(path) ? statSync(path).mode & 0o777 : 0o644);
          }
        }
      },
      catch: (error) =>
        error instanceof ManagerError ? error : new ManagerError({ message: `Restore files: ${error}` }),
    });
    // Git's index follows, so the flake sees exactly the files it saw before.
    const removed = point.files.filter((file) => file.created).map((file) => file.path);
    const recreated = point.files.filter((file) => !file.created && file.written === null).map((file) => file.path);
    if (existsSync(join(config.flake, ".git"))) {
      if (removed.length)
        yield* command("Remove created files from Git", ["git", "-C", config.flake, "rm", "--cached", "--quiet", "--ignore-unmatch", "--", ...removed], {
          quiet: true,
        });
      if (recreated.length)
        yield* command("Add restored files to Git", ["git", "-C", config.flake, "add", "--intent-to-add", "--", ...recreated], { quiet: true });
    }
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
