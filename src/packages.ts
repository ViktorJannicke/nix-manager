import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { Effect, Schema } from "effect";
import { ManagerError } from "./domain";
import { attr as attrName } from "./nix";
import { attempt, command } from "./process";
import {
  attrNames,
  descendants,
  identifier,
  parseNix,
  renderPath,
  staticPath,
  type Node,
} from "./syntax";
import { temporary } from "./updates";

/** Bindings whose value is a list of packages, matched by their full attrpath. */
export const packageBindings = ["packages", "home.packages", "environment.systemPackages"];

export interface PackageItem {
  /** Resolved attribute path such as `pkgs.kdePackages.kate`; undefined when computed. */
  attr?: string;
  text: string;
  start: number;
  end: number;
}

export interface PackageList {
  binding: string;
  /** Innermost `with` environment, for example `pkgs`. */
  scope?: string;
  /** The list only applies under a condition such as `lib.optionals`. */
  conditional: boolean;
  start: number;
  end: number;
  items: PackageItem[];
}

const resolve = (element: Node, scope: string | undefined) => {
  const path = staticPath(element);
  if (!path) return undefined;
  // A bare name inside `with pkgs;` refers to `pkgs.<name>`. Qualified names
  // such as `pkgs.foo` are written out in full.
  if (element.type === "variable_expression") return scope ? `${scope}.${renderPath(path)}` : undefined;
  return renderPath(path);
};

/** Lists that make up the value of a package binding, with how each item resolves. */
export function packageLists(root: Node): PackageList[] {
  const lists: PackageList[] = [];
  for (const binding of descendants(root)) {
    if (binding.type !== "binding") continue;
    const attrpath = binding.childForFieldName("attrpath");
    const names = attrpath && attrNames(attrpath);
    if (!names || !packageBindings.includes(names.join("."))) continue;
    const visit = (node: Node, scope: string | undefined, conditional: boolean) => {
      switch (node.type) {
        case "list_expression":
          lists.push({
            binding: names.join("."),
            scope,
            conditional,
            start: node.startIndex,
            end: node.endIndex,
            items: node.childrenForFieldName("element").flatMap((element) =>
              element
                ? [
                    {
                      attr: resolve(element, scope),
                      text: element.text,
                      start: element.startIndex,
                      end: element.endIndex,
                    },
                  ]
                : [],
            ),
          });
          return;
        case "with_expression": {
          const environment = staticPath(node.childForFieldName("environment"));
          const body = node.childForFieldName("body");
          if (body) visit(body, environment ? renderPath(environment) : undefined, conditional);
          return;
        }
        case "parenthesized_expression": {
          const inner = node.childForFieldName("expression");
          if (inner) visit(inner, scope, conditional);
          return;
        }
        case "binary_expression":
          if (node.childForFieldName("operator")?.text !== "++") return;
          for (const side of ["left", "right"]) {
            const child = node.childForFieldName(side);
            if (child) visit(child, scope, conditional);
          }
          return;
        case "apply_expression": {
          // `lib.optionals cond [ ... ]` and similar helpers wrap a list.
          const argument = node.childForFieldName("argument");
          if (argument) visit(argument, scope, true);
          return;
        }
      }
    };
    const value = binding.childForFieldName("expression");
    if (value) visit(value, undefined, false);
  }
  return lists;
}

/** How `attr` is written in `list`: relative to its `with` scope when possible. */
const spelling = (list: PackageList, attr: string) => {
  if (list.scope && attr.startsWith(`${list.scope}.`)) {
    const rest = attr.slice(list.scope.length + 1);
    // A quoted first segment is a string, not a name, inside `with`.
    if (identifier(rest.split(".")[0])) return rest;
  }
  return attr;
};

const lineStart = (source: string, index: number) => source.lastIndexOf("\n", index - 1) + 1;
const lineEnd = (source: string, index: number) => {
  const end = source.indexOf("\n", index);
  return end < 0 ? source.length : end;
};

export interface Edit {
  source: string;
  /** Range of the original source that was replaced. */
  start: number;
  end: number;
  text: string;
}

const apply = (source: string, start: number, end: number, text: string): Edit => ({
  source: source.slice(0, start) + text + source.slice(end),
  start,
  end,
  text,
});

/**
 * Adds a package to the unconditional list that already uses its scope. The
 * longest matching `with` scope wins, so `pkgs.kdePackages.kate` goes into
 * `with pkgs.kdePackages; [ ... ]` when the file has such a list.
 */
export async function addPackage(source: string, attr: string, binding?: string): Promise<Edit> {
  const tree = await parseNix(source);
  try {
    if (tree.rootNode.hasError)
      throw new ManagerError({ message: "The file has syntax errors." });
    const lists = packageLists(tree.rootNode).filter(
      (list) => !binding || list.binding === binding,
    );
    if (lists.some((list) => list.items.some((item) => item.attr === attr)))
      throw new ManagerError({ message: `${attr} is already listed in this file.` });
    const candidates = lists
      .filter((list) => !list.conditional)
      .map((list) => ({
        list,
        score: list.scope && attr.startsWith(`${list.scope}.`) ? list.scope.length : -1,
      }))
      .sort((a, b) => b.score - a.score);
    const target = candidates[0]?.list;
    if (!target) throw new ManagerError({ message: "No editable package list was found." });
    const text = spelling(target, attr);
    const last = target.items.at(-1);
    const close = target.end - 1;
    const multiline = source.slice(target.start, target.end).includes("\n");
    if (!multiline) {
      // `[ a b ]` becomes `[ a b c ]`; `[ ]` and `[]` become `[ c ]`.
      if (last) return apply(source, last.end, last.end, ` ${text}`);
      return apply(source, target.start, target.end, `[ ${text} ]`);
    }
    if (last) {
      const at = lineEnd(source, last.end);
      // `c ];` on one line: continue that line instead of splitting the bracket.
      if (!/^[ \t]*(#.*)?$/.test(source.slice(last.end, at)))
        return apply(source, last.end, last.end, ` ${text}`);
      // Keep a trailing comment on the last item's line attached to it.
      const indent = source.slice(lineStart(source, last.start)).match(/^[ \t]*/)![0];
      return apply(source, at, at, `\n${indent}${text}`);
    }
    const closing = lineStart(source, close);
    const indent = source.slice(closing, close).match(/^\s*/)![0];
    return apply(source, closing, closing, `${indent}  ${text}\n`);
  } finally {
    tree.delete();
  }
}

/** Removes every listing of `attr`, dropping the whole line when the item stands alone. */
export async function removePackage(source: string, attr: string): Promise<Edit> {
  const tree = await parseNix(source);
  try {
    const items = packageLists(tree.rootNode)
      .flatMap((list) => list.items)
      .filter((item) => item.attr === attr);
    if (!items.length) throw new ManagerError({ message: `${attr} is not listed in this file.` });
    if (items.length > 1)
      throw new ManagerError({ message: `${attr} is listed ${items.length} times in this file.` });
    const [item] = items;
    const start = lineStart(source, item.start);
    const end = lineEnd(source, item.end);
    const before = source.slice(start, item.start);
    const after = source.slice(item.end, end);
    if (/^\s*$/.test(before) && /^\s*(#.*)?$/.test(after))
      return apply(source, start, Math.min(end + 1, source.length), "");
    // Inline item: remove it with the whitespace that separates it.
    const space = source.slice(item.end).match(/^[ \t]*/)![0].length;
    return apply(source, item.start, item.end + space, "");
  } finally {
    tree.delete();
  }
}

const Metadata = Schema.Struct({ path: Schema.String });
const Definitions = Schema.Array(
  Schema.Struct({ file: Schema.String, outs: Schema.Array(Schema.String) }),
);
const decode = <A, I>(schema: Schema.Schema<A, I>, label: string) => (text: string) =>
  Schema.decodeUnknown(Schema.parseJson(schema))(text).pipe(
    Effect.mapError((e) => new ManagerError({ message: `${label}: ${e}` })),
  );

/** Store copy of the flake's tracked files, including uncommitted edits. */
export const flakeSource = (flake: string) =>
  command("Read flake source", ["nix", "flake", "metadata", "--json", flake], { quiet: true }).pipe(
    Effect.flatMap((result) => decode(Metadata, "Invalid flake metadata")(result.stdout)),
    Effect.map((metadata) => metadata.path),
  );

/** Output paths that each file contributes to `home.packages`, keyed by path inside the flake. */
export const homePackageSources = (source: string, homeConfiguration: string) =>
  command(
    "Evaluate home packages",
    [
      "nix",
      "eval",
      "--json",
      `path:${source}#homeConfigurations.${attrName(homeConfiguration)}.options.home.packages.definitionsWithLocations`,
      "--apply",
      // List items can carry their own `mkIf`, `mkOverride` or `mkOrder`.
      `let
        outs = p:
          if !builtins.isAttrs p then [ ("<" + builtins.typeOf p + ">") ]
          else if p ? outPath then [ p.outPath ]
          else if (p._type or "") == "if" then (if p.condition then outs p.content else [ ])
          else if builtins.elem (p._type or "") [ "override" "order" ] then outs p.content
          else [ "<unrecognised>" ];
      in map (d: { file = d.file; outs = builtins.concatMap outs d.value; })`,
    ],
    { quiet: true },
  ).pipe(
    Effect.flatMap((result) => decode(Definitions, "Invalid package evaluation")(result.stdout)),
    Effect.map((definitions) => {
      // Files inside the flake are reported under its own store path; other
      // modules (Home Manager's) keep their absolute path.
      const prefix = /^\/nix\/store\/[a-z0-9]{32}-source\//;
      const sources = new Map<string, string[]>();
      for (const { file, outs } of definitions) {
        const key = file.startsWith(`${source}/`) ? file.slice(source.length + 1) : file.replace(prefix, "<external>/");
        sources.set(key, [...(sources.get(key) ?? []), ...outs].sort());
      }
      return sources;
    }),
  );

export interface Verification {
  ok: boolean;
  /** The package's output path in the evaluated configuration. */
  output: string;
  changes: { file: string; added: string[]; removed: string[] }[];
}

const difference = (a: readonly string[], b: readonly string[]) => {
  const rest = [...b];
  return a.filter((item) => {
    const index = rest.indexOf(item);
    if (index < 0) return true;
    rest.splice(index, 1);
    return false;
  });
};

/**
 * Evaluates the configuration before and after an edit, in a temporary copy of
 * the flake's tracked files. The edit is accepted only when exactly the edited
 * file gains (or loses) exactly the requested package.
 */
export const verifyEdit = (options: {
  flake: string;
  homeConfiguration: string;
  file: string;
  attr: string;
  kind: "add" | "remove";
  source: string;
}) =>
  Effect.scoped(
    Effect.gen(function* () {
      const original = yield* flakeSource(options.flake);
      const work = yield* temporary;
      const copy = join(work, "flake");
      yield* command("Copy flake source", ["cp", "-rT", "--no-preserve=mode", original, copy], {
        quiet: true,
      });
      yield* attempt(`Write ${options.file}`, () => writeFile(join(copy, options.file), options.source));
      const edited = yield* flakeSource(`path:${copy}`);
      const [before, after, output] = yield* Effect.all(
        [
          homePackageSources(original, options.homeConfiguration),
          homePackageSources(edited, options.homeConfiguration),
          command(
            "Evaluate package",
            [
              "nix",
              "eval",
              "--raw",
              `path:${original}#homeConfigurations.${attrName(options.homeConfiguration)}.${options.attr}.outPath`,
            ],
            { quiet: true },
          ).pipe(Effect.map((result) => result.stdout.trim())),
        ],
        { concurrency: "unbounded" },
      );
      const files = [...new Set([...before.keys(), ...after.keys()])].sort();
      const changes = files.flatMap((file) => {
        const was = before.get(file) ?? [];
        const now = after.get(file) ?? [];
        const added = difference(now, was);
        const removed = difference(was, now);
        return added.length || removed.length ? [{ file, added, removed }] : [];
      });
      const expected = { file: options.file, added: [] as string[], removed: [] as string[] };
      expected[options.kind === "add" ? "added" : "removed"].push(output);
      return {
        ok: JSON.stringify(changes) === JSON.stringify([expected]),
        output,
        changes,
      } satisfies Verification;
    }),
  );
