import { Effect, Schema } from "effect";
import { ManagerError } from "./domain";
import { command } from "./process";
import {
  attrNames,
  descendants,
  identifier,
  parseNix,
  renderPath,
  staticPath,
  type Node,
} from "./syntax";

/** Bindings whose value is a list of packages, matched by their full attrpath. */
export const packageBindings = ["packages", "home.packages", "environment.systemPackages"];

export interface PackageItem {
  /** Resolved attribute path such as `pkgs.kdePackages.kate`; undefined when computed. */
  attr?: string;
  path?: string[];
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

const resolve = (element: Node, scope: readonly string[] | undefined) => {
  const path = staticPath(element);
  if (!path) return undefined;
  // A bare name inside `with pkgs;` refers to `pkgs.<name>`. Qualified names
  // such as `pkgs.foo` are written out in full.
  if (element.type === "variable_expression") return scope ? [...scope, ...path] : undefined;
  return path;
};

/** Lists that make up the value of a package binding, with how each item resolves. */
export function packageLists(root: Node): PackageList[] {
  const lists: PackageList[] = [];
  for (const binding of descendants(root)) {
    if (binding.type !== "binding") continue;
    const attrpath = binding.childForFieldName("attrpath");
    const names = attrpath && attrNames(attrpath);
    if (!names || !packageBindings.includes(names.join("."))) continue;
    const visit = (node: Node, scopePath: string[] | undefined, conditional: boolean) => {
      const scope = scopePath && renderPath(scopePath);
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
                      attr: resolve(element, scopePath) && renderPath(resolve(element, scopePath)!),
                      path: resolve(element, scopePath),
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
          if (body) visit(body, environment, conditional);
          return;
        }
        case "parenthesized_expression": {
          const inner = node.childForFieldName("expression");
          if (inner) visit(inner, scopePath, conditional);
          return;
        }
        case "binary_expression":
          if (node.childForFieldName("operator")?.text !== "++") return;
          for (const side of ["left", "right"]) {
            const child = node.childForFieldName(side);
            if (child) visit(child, scopePath, conditional);
          }
          return;
        case "apply_expression": {
          // `lib.optionals cond [ ... ]` and similar helpers wrap a list.
          const argument = node.childForFieldName("argument");
          if (argument) visit(argument, scopePath, true);
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

export const lineStart = (source: string, index: number) => source.lastIndexOf("\n", index - 1) + 1;
export const lineEnd = (source: string, index: number) => {
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

export const apply = (source: string, start: number, end: number, text: string): Edit => ({
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
export async function addPackage(
  source: string,
  attr: string,
  bindings: readonly string[] = packageBindings,
): Promise<Edit> {
  const tree = await parseNix(source);
  try {
    if (tree.rootNode.hasError)
      throw new ManagerError({ message: "The file has syntax errors." });
    const lists = packageLists(tree.rootNode).filter(
      (list) => bindings.includes(list.binding),
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

/** Store copy of the flake's tracked files, including uncommitted edits. */
export const flakeSource = (flake: string) =>
  command("Read flake source", ["nix", "flake", "metadata", "--json", flake], { quiet: true }).pipe(
    Effect.flatMap((result) =>
      Schema.decodeUnknown(Schema.parseJson(Metadata))(result.stdout).pipe(
        Effect.mapError((e) => new ManagerError({ message: `Invalid flake metadata: ${e}` })),
      ),
    ),
    Effect.map((metadata) => metadata.path),
  );
