import { ManagerError } from "./domain";
import { attrNames, identifier, parseNix, staticPath, type Node } from "./syntax";
import { apply, lineEnd, lineStart, type Edit } from "./packages";

/** JSON values an option can be set to from a form. */
export type Value = null | boolean | number | string | Value[] | { [key: string]: Value };

const escape = (text: string) =>
  text.replaceAll("\\", "\\\\").replaceAll('"', '\\"').replaceAll("${", "\\${").replaceAll("\n", "\\n").replaceAll("\t", "\\t").replaceAll("\r", "\\r");

/** Nix source for a JSON value, on one line. */
export function renderValue(value: Value): string {
  if (value === null) return "null";
  if (typeof value === "boolean") return String(value);
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new ManagerError({ message: "Numbers must be finite." });
    const text = Number.isInteger(value) ? String(value) : String(value).includes("e") ? value.toFixed(12) : String(value);
    return value < 0 ? `(${text})` : text;
  }
  if (typeof value === "string") return `"${escape(value)}"`;
  if (Array.isArray(value)) return value.length ? `[ ${value.map(renderValue).join(" ")} ]` : "[ ]";
  const entries = Object.entries(value);
  if (!entries.length) return "{ }";
  return `{ ${entries.map(([key, item]) => `${identifier(key) ? key : `"${escape(key)}"`} = ${renderValue(item)};`).join(" ")} }`;
}

const attrsets = new Set(["attrset_expression", "rec_attrset_expression"]);

/**
 * The attribute set a module's settings are written in: the body of
 * `{ ... }: { ... }`, through `let ... in`, or its `config = { ... }` when the
 * module also declares options.
 */
export function moduleBody(root: Node): Node | undefined {
  let node: Node | null = root.namedChildren[0] ?? null;
  while (node) {
    if (node.type === "function_expression" || node.type === "let_expression")
      node = node.childForFieldName("body");
    else if (node.type === "parenthesized_expression") node = node.childForFieldName("expression");
    else break;
  }
  if (!node || !attrsets.has(node.type)) return undefined;
  const config = bindings(node).find((binding) => names(binding)?.join(".") === "config");
  const value = config?.childForFieldName("expression");
  return value && attrsets.has(value.type) ? value : node;
}

const bindings = (set: Node) =>
  (set.namedChildren.find((child) => child?.type === "binding_set")?.namedChildren ?? []).filter(
    (child): child is Node => child?.type === "binding",
  );

const names = (binding: Node) => {
  const attrpath = binding.childForFieldName("attrpath");
  return attrpath ? attrNames(attrpath) : undefined;
};

const startsWith = (path: readonly string[], prefix: readonly string[]) =>
  prefix.length <= path.length && prefix.every((name, index) => path[index] === name);

interface Found {
  /** Binding of exactly the option path. */
  binding?: Node;
  /** Deepest attribute set on the way, and the path remaining below it. */
  set: Node;
  rest: string[];
}

function locate(set: Node, path: readonly string[]): Found {
  let best: Found = { set, rest: [...path] };
  for (const binding of bindings(set)) {
    const bound = names(binding);
    if (!bound) continue;
    if (bound.length === path.length && startsWith(path, bound)) return { binding, set, rest: [] };
    if (startsWith(bound, path))
      throw new ManagerError({
        message: `${path.join(".")} is written as separate parts here. Edit it in the file.`,
      });
    const value = binding.childForFieldName("expression");
    if (bound.length < path.length && startsWith(path, bound) && value && attrsets.has(value.type)) {
      const inner = locate(value, path.slice(bound.length));
      if (inner.binding) return inner;
      if (inner.rest.length < best.rest.length) best = inner;
    }
  }
  return best;
}

/** Values Nix Manager can replace without losing meaning: plain data and package references. */
export function literal(node: Node | null): boolean {
  if (!node) return false;
  switch (node.type) {
    case "integer_expression":
    case "float_expression":
    case "path_expression":
      return true;
    case "unary_expression":
      return literal(node.childForFieldName("argument"));
    case "string_expression":
    case "indented_string_expression":
      return node.namedChildren.every((part) => part?.type !== "interpolation");
    case "variable_expression":
      return ["true", "false", "null"].includes(node.text);
    case "select_expression":
      return staticPath(node)?.[0] === "pkgs";
    case "parenthesized_expression":
      return literal(node.childForFieldName("expression"));
    case "list_expression":
      return node.childrenForFieldName("element").every((element) => literal(element));
    case "attrset_expression":
      return bindings(node).every((binding) => !!names(binding) && literal(binding.childForFieldName("expression")));
    default:
      return false;
  }
}

const parseModule = async (source: string) => {
  const tree = await parseNix(source);
  if (tree.rootNode.hasError) {
    tree.delete();
    throw new ManagerError({ message: "The file has syntax errors." });
  }
  const body = moduleBody(tree.rootNode);
  if (!body) {
    tree.delete();
    throw new ManagerError({ message: "This file is not a module Nix Manager can edit." });
  }
  return { tree, body };
};

const checkPath = (option: string) => {
  const path = option.split(".");
  if (!path.every(identifier))
    throw new ManagerError({ message: `Unsupported option path: ${option}` });
  return path;
};

/**
 * Sets an option in a module. An existing plain value is replaced in place;
 * otherwise the binding is added to the deepest attribute set that already
 * holds part of its path, so `programs.git = { ... }` gains `lfs.enable`.
 */
export async function setOption(source: string, option: string, value: Value): Promise<Edit> {
  const path = checkPath(option);
  const { tree, body } = await parseModule(source);
  try {
    const found = locate(body, path);
    const text = renderValue(value);
    if (found.binding) {
      const expression = found.binding.childForFieldName("expression")!;
      if (!literal(expression))
        throw new ManagerError({
          message: `${option} is computed here (${expression.text.split("\n")[0].slice(0, 60)}). Edit it in the file.`,
        });
      return apply(source, expression.startIndex, expression.endIndex, text);
    }
    const binding = `${found.rest.join(".")} = ${text};`;
    const set = found.set;
    // Inherits and comments count as content: insert after whatever comes last.
    const last = set.namedChildren.find((child) => child?.type === "binding_set")?.lastNamedChild;
    const close = set.endIndex - 1;
    const multiline = source.slice(set.startIndex, set.endIndex).includes("\n");
    if (!multiline) {
      if (last) return apply(source, last.endIndex, last.endIndex, ` ${binding}`);
      return apply(source, set.startIndex, set.endIndex, `{ ${binding} }`);
    }
    if (last) {
      const at = lineEnd(source, last.endIndex);
      if (!/^[ \t]*(#.*)?$/.test(source.slice(last.endIndex, at)))
        return apply(source, last.endIndex, last.endIndex, ` ${binding}`);
      const indent = source.slice(lineStart(source, last.startIndex)).match(/^[ \t]*/)![0];
      return apply(source, at, at, `\n${indent}${binding}`);
    }
    const closing = lineStart(source, close);
    const indent = source.slice(closing, close).match(/^\s*/)![0];
    if (/\S/.test(source.slice(closing, close)))
      return apply(source, close, close, ` ${binding} `);
    return apply(source, closing, closing, `${indent}  ${binding}\n`);
  } finally {
    tree.delete();
  }
}

/** Removes an option's binding so its default applies again. */
export async function unsetOption(source: string, option: string): Promise<Edit> {
  const path = checkPath(option);
  const { tree, body } = await parseModule(source);
  try {
    const { binding } = locate(body, path);
    if (!binding) throw new ManagerError({ message: `${option} is not set in this file.` });
    const start = lineStart(source, binding.startIndex);
    const end = lineEnd(source, binding.endIndex);
    if (/^\s*$/.test(source.slice(start, binding.startIndex)) && /^\s*(#.*)?$/.test(source.slice(binding.endIndex, end)))
      return apply(source, start, Math.min(end + 1, source.length), "");
    const space = source.slice(binding.endIndex).match(/^[ \t]*/)![0].length;
    return apply(source, binding.startIndex, binding.endIndex + space, "");
  } finally {
    tree.delete();
  }
}

/** Whether a file is a module whose settings can be edited, and the top-level names it sets. */
export async function moduleShape(source: string) {
  const tree = await parseNix(source);
  try {
    const body = tree.rootNode.hasError ? undefined : moduleBody(tree.rootNode);
    return {
      editable: !!body,
      names: body ? bindings(body).flatMap((binding) => (names(binding) ? [names(binding)!.join(".")] : [])) : [],
    };
  } finally {
    tree.delete();
  }
}
