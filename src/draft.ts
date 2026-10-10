import { existsSync, mkdirSync, readFileSync, realpathSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { isAbsolute, join, normalize, posix, relative } from "node:path";
import { Schema } from "effect";
import { ManagerError, type Config } from "./domain";
import { hashText } from "./observe";
import { addImport, setOption, startList, unsetOption, type Value } from "./options";
import { addPackage, removePackage, packageBindings } from "./packages";

const Target = Schema.Literal("home", "system");
const Fields = {
  scope: Target,
  /** Path of the edited file inside the flake. */
  file: Schema.String.pipe(Schema.maxLength(256)),
  /** What the change belongs to, such as an experience title. */
  label: Schema.optional(Schema.String.pipe(Schema.maxLength(120))),
};
export const Change = Schema.Union(
  Schema.Struct({
    kind: Schema.Literal("add-package", "remove-package"),
    attr: Schema.String.pipe(Schema.maxLength(256)),
    ...Fields,
  }),
  Schema.Struct({
    kind: Schema.Literal("set-option"),
    option: Schema.String.pipe(Schema.maxLength(256)),
    value: Schema.Unknown,
    ...Fields,
  }),
  Schema.Struct({
    kind: Schema.Literal("unset-option"),
    option: Schema.String.pipe(Schema.maxLength(256)),
    ...Fields,
  }),
  /** Creates `file` as an empty module and imports it from an existing module. */
  Schema.Struct({
    kind: Schema.Literal("create-module"),
    importer: Schema.String.pipe(Schema.maxLength(256)),
    ...Fields,
  }),
);
export type Change = typeof Change.Type;
export const Changes = Schema.Array(Change).pipe(Schema.maxItems(200));

export interface DiffLine {
  kind: " " | "+" | "-";
  text: string;
}
export interface Hunk {
  /** First line of the hunk in the original file, starting at 1. */
  line: number;
  lines: DiffLine[];
}
export interface DraftFile {
  path: string;
  /** Hash of the file the edits start from; empty for a file the edits create. */
  baseHash: string;
  created?: boolean;
  hunks: Hunk[];
}
export interface DraftProblem {
  /** Index of the change in the draft. */
  index: number;
  message: string;
}
export interface RenderedDraft {
  changes: Change[];
  files: DraftFile[];
  problems: DraftProblem[];
  /** Identifies the change list, so a review can tell whether the draft moved on. */
  hash: string;
}

export const changeHash = (changes: readonly Change[]) => hashText(JSON.stringify(changes));

/** A change's subject, for messages: the attribute or option it edits. */
export const subject = (change: Change) =>
  "attr" in change ? change.attr : "option" in change ? change.option : change.file;

/** Resolves a flake-relative `.nix` path, refusing anything outside the flake. */
export function sourcePath(flake: string, file: string) {
  const clean = normalize(file);
  if (isAbsolute(clean) || clean.startsWith("..") || !clean.endsWith(".nix") || clean !== file)
    throw new ManagerError({ message: `Invalid file: ${file}` });
  const path = join(flake, clean);
  if (existsSync(path)) {
    const real = relative(realpathSync(flake), realpathSync(path));
    if (real.startsWith("..") || isAbsolute(real))
      throw new ManagerError({ message: `${file} is outside the flake.` });
  }
  return path;
}

/** Package list bindings each scope adds to; category modules use `packages`. */
export const scopeBindings = {
  home: packageBindings.filter((binding) => binding !== "environment.systemPackages"),
  system: packageBindings.filter((binding) => binding !== "home.packages"),
} as const;

/** The list a package goes into when a module has no package list yet. */
export const packagesOption = { home: "home.packages", system: "environment.systemPackages" } as const;

/** What a module created for the draft starts as. */
export const moduleTemplate = "{ pkgs, ... }:\n{\n}\n";

/** The path an importer writes for a file, such as `./fzf.nix` or `../programs/fzf.nix`. */
export function importPath(importer: string, file: string) {
  const path = posix.relative(posix.dirname(importer), file);
  return path.startsWith("../") ? path : `./${path}`;
}

const edit = (source: string, change: Exclude<Change, { kind: "create-module" }>) => {
  switch (change.kind) {
    case "add-package":
      return addPackage(source, change.attr, scopeBindings[change.scope]).catch((error) => {
        if (error instanceof ManagerError && error.message.startsWith("No editable package list"))
          return startList(source, packagesOption[change.scope], change.attr);
        throw error;
      });
    case "remove-package":
      return removePackage(source, change.attr);
    case "set-option":
      return setOption(source, change.option, change.value as Value);
    case "unset-option":
      return unsetOption(source, change.option);
  }
};

/** Line diff by longest common subsequence, grouped into hunks with context. */
export function diffLines(before: string, after: string, context = 2): Hunk[] {
  const a = before.split("\n");
  const b = after.split("\n");
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a.at(-1 - tail) === b.at(-1 - tail)) tail++;
  const x = a.slice(head, a.length - tail);
  const y = b.slice(head, b.length - tail);
  const table = Array.from({ length: x.length + 1 }, () => new Uint32Array(y.length + 1));
  for (let i = x.length - 1; i >= 0; i--)
    for (let j = y.length - 1; j >= 0; j--)
      table[i][j] = x[i] === y[j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);
  const lines: (DiffLine & { old: number })[] = [];
  for (let k = 0; k < head; k++) lines.push({ kind: " ", text: a[k], old: k });
  let i = 0;
  let j = 0;
  while (i < x.length || j < y.length) {
    if (i < x.length && j < y.length && x[i] === y[j]) {
      lines.push({ kind: " ", text: x[i], old: head + i });
      i++;
      j++;
    } else if (i < x.length && (j === y.length || table[i + 1][j] >= table[i][j + 1])) {
      lines.push({ kind: "-", text: x[i], old: head + i++ });
    } else lines.push({ kind: "+", text: y[j++], old: head + i });
  }
  for (let k = a.length - tail; k < a.length; k++) lines.push({ kind: " ", text: a[k], old: k });
  const hunks: Hunk[] = [];
  let current: { start: number; end: number } | undefined;
  const ranges: { start: number; end: number }[] = [];
  lines.forEach((line, index) => {
    if (line.kind === " ") return;
    const start = Math.max(0, index - context);
    const end = Math.min(lines.length, index + context + 1);
    if (current && start <= current.end) current.end = end;
    else ranges.push((current = { start, end }));
  });
  for (const range of ranges) {
    const slice = lines.slice(range.start, range.end);
    hunks.push({ line: slice[0].old + 1, lines: slice.map(({ kind, text }) => ({ kind, text })) });
  }
  return hunks;
}

export interface EditedFile extends DraftFile {
  before: string;
  after: string;
}

/**
 * Applies the changes to the checkout's files in memory. A change that cannot
 * be applied is reported and skipped; the others still apply.
 */
export async function renderDraft(flake: string, changes: readonly Change[]) {
  const files = new Map<string, EditedFile>();
  const problems: DraftProblem[] = [];
  const open = async (name: string) => {
    const known = files.get(name);
    if (known) return known;
    const path = sourcePath(flake, name);
    const before = await readFile(path, "utf8").catch(() => {
      throw new ManagerError({ message: `${name} does not exist.` });
    });
    return { path: name, baseHash: hashText(before), before, after: before, hunks: [] } satisfies EditedFile;
  };
  for (const [index, change] of changes.entries()) {
    try {
      if (change.kind === "create-module") {
        if (!/^[A-Za-z0-9._+\/-]+$/.test(change.file))
          throw new ManagerError({ message: `${change.file} cannot be written as a Nix path.` });
        if (files.has(change.file) || existsSync(sourcePath(flake, change.file)))
          throw new ManagerError({ message: `${change.file} already exists.` });
        if (change.importer === change.file) throw new ManagerError({ message: "A file cannot import itself." });
        const importer = await open(change.importer);
        importer.after = (await addImport(importer.after, importPath(change.importer, change.file))).source;
        files.set(change.importer, importer);
        files.set(change.file, {
          path: change.file,
          baseHash: "",
          created: true,
          before: "",
          after: moduleTemplate,
          hunks: [],
        });
        continue;
      }
      const file = await open(change.file);
      file.after = (await edit(file.after, change)).source;
      files.set(change.file, file);
    } catch (error) {
      problems.push({ index, message: error instanceof Error ? error.message : String(error) });
    }
  }
  for (const file of files.values()) file.hunks = diffLines(file.before, file.after);
  return {
    files: [...files.values()].filter((file) => file.created || file.before !== file.after),
    problems,
  };
}

export const draftPublic = async (flake: string, changes: readonly Change[]): Promise<RenderedDraft> => {
  const { files, problems } = await renderDraft(flake, changes);
  return {
    changes: [...changes],
    files: files.map(({ path, baseHash, created, hunks }) => ({ path, baseHash, created, hunks })),
    problems,
    hash: changeHash(changes),
  };
};

const draftFile = (config: Config) => join(config.stateDir, "draft.json");

export function readDraft(config: Config): Change[] {
  try {
    const saved = JSON.parse(readFileSync(draftFile(config), "utf8"));
    if (saved.flake !== config.flake) return [];
    return [...Schema.decodeUnknownSync(Changes)(saved.changes)];
  } catch {
    return [];
  }
}

export function writeDraft(config: Config, changes: readonly Change[]) {
  mkdirSync(config.stateDir, { recursive: true, mode: 0o700 });
  const path = draftFile(config);
  const temporary = `${path}.${crypto.randomUUID()}.tmp`;
  try {
    writeFileSync(temporary, JSON.stringify({ flake: config.flake, changes }, null, 2) + "\n", { mode: 0o600 });
    renameSync(temporary, path);
  } finally {
    rmSync(temporary, { force: true });
  }
}

