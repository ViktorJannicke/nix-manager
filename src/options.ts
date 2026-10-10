import { ManagerError } from "./domain";
import { attrNames, identifier, parseNix, staticPath, type Node } from "./syntax";
import { appendItem, apply, lineEnd, lineStart, type Edit } from "./packages";

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

/** The attribute set a module evaluates to, through `{ ... }:`, `let ... in` and parentheses. */
function moduleSet(root: Node): Node | undefined {
  let node: Node | null = root.namedChildren[0] ?? null;
  while (node) {
    if (node.type === "function_expression" || node.type === "let_expression")
      node = node.childForFieldName("body");
    else if (node.type === "parenthesized_expression") node = node.childForFieldName("expression");
    else break;
  }
  return node && attrsets.has(node.type) ? node : undefined;
}

/**
 * The attribute set a module's settings are written in: the body of
 * `{ ... }: { ... }`, through `let ... in`, or its `config = { ... }` when the
 * module also declares options.
 */
export function moduleBody(root: Node): Node | undefined {
  const node = moduleSet(root);
  if (!node) return undefined;
  const top = bindings(node);
  const config = top.find((binding) => names(binding)?.join(".") === "config");
  const value = config?.childForFieldName("expression");
  if (value) return attrsets.has(value.type) ? value : undefined;
  // Settings beside option declarations must be written under `config`.
  return top.some((binding) => names(binding)?.[0] === "options") ? undefined : node;
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
    return insertBinding(source, found.set, `${found.rest.join(".")} = ${text};`);
  } finally {
    tree.delete();
  }
}

/** Adds a binding as the last entry of a set, in the set's layout. */
function insertBinding(source: string, set: Node, binding: string): Edit {
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
}

/**
 * Starts a list option in a module that has none, such as
 * `home.packages = [ pkgs.fzf ];`. A file that already binds the option
 * is left alone: its list is not one Nix Manager can extend.
 */
export async function startList(source: string, option: string, item: string): Promise<Edit> {
  const path = checkPath(option);
  const { tree, body } = await parseModule(source);
  try {
    const found = locate(body, path);
    if (found.binding) throw new ManagerError({ message: `${option} is computed here. Edit it in the file.` });
    return insertBinding(source, found.set, `${found.rest.join(".")} = [ ${item} ];`);
  } finally {
    tree.delete();
  }
}

/** Adds a relative path such as `./fzf.nix` to a module's `imports`, starting the list if needed. */
export async function addImport(source: string, path: string): Promise<Edit> {
  if (!/^\.{1,2}\/[A-Za-z0-9._+\/-]+$/.test(path))
    throw new ManagerError({ message: `${path} cannot be written as a Nix path.` });
  const tree = await parseNix(source);
  try {
    if (tree.rootNode.hasError) throw new ManagerError({ message: "The file has syntax errors." });
    const set = moduleSet(tree.rootNode);
    if (!set) throw new ManagerError({ message: "This file is not a module Nix Manager can edit." });
    const binding = bindings(set).find((item) => names(item)?.join(".") === "imports");
    if (!binding) return insertBinding(source, set, `imports = [ ${path} ];`);
    const list = binding.childForFieldName("expression");
    if (list?.type !== "list_expression")
      throw new ManagerError({ message: "This file computes its imports. Add the import in the file." });
    const elements = list.childrenForFieldName("element").filter((element): element is Node => !!element);
    if (elements.some((element) => element.text === path))
      throw new ManagerError({ message: `The file already imports ${path}.` });
    const last = elements.at(-1);
    return appendItem(
      source,
      { start: list.startIndex, end: list.endIndex },
      last && { start: last.startIndex, end: last.endIndex },
      path,
    );
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

/** Names that structure a module rather than set options. */
const structural = new Set(["imports", "options", "disabledModules", "_file", "key", "meta", "_class"]);

/** Full paths of the settings below a set, following nested attribute sets. */
function leaves(set: Node, prefix: readonly string[] = [], limit = 64): string[][] {
  const found: string[][] = [];
  for (const binding of bindings(set)) {
    const bound = names(binding);
    if (!bound || (!prefix.length && structural.has(bound[0]))) continue;
    const path = [...prefix, ...bound];
    const value = binding.childForFieldName("expression");
    if (value?.type === "attrset_expression" && bindings(value).length) found.push(...leaves(value, path, limit));
    else found.push(path);
    if (found.length >= limit) break;
  }
  return found.slice(0, limit);
}

const importList = (set: Node | undefined) =>
  set && bindings(set).find((binding) => names(binding)?.join(".") === "imports")?.childForFieldName("expression");

/** Relative paths a module always imports: the plain path items of its `imports` list. */
function imports(root: Node): string[] {
  const list = importList(moduleSet(root));
  if (list?.type !== "list_expression") return [];
  return list
    .childrenForFieldName("element")
    .flatMap((element) => (element?.type === "path_expression" && /^\.{1,2}\//.test(element.text) ? [element.text] : []));
}

/** Whether `addImport` can add to a module: it has a plain list of imports, or none. */
function importable(root: Node) {
  const set = moduleSet(root);
  if (!set) return false;
  const list = importList(set);
  return !list || list.type === "list_expression";
}

/**
 * Whether a file is a module whose settings can be edited, the top-level
 * names it sets, the full paths of the settings it writes, the relative
 * paths it imports unconditionally, and whether an import can be added.
 */
export async function moduleShape(source: string) {
  const tree = await parseNix(source);
  try {
    const failed = tree.rootNode.hasError;
    const body = failed ? undefined : moduleBody(tree.rootNode);
    return {
      editable: !!body,
      names: body ? bindings(body).flatMap((binding) => (names(binding) ? [names(binding)!.join(".")] : [])) : [],
      settings: body ? leaves(body).map((path) => path.join(".")) : [],
      imports: failed ? [] : imports(tree.rootNode),
      importable: !failed && importable(tree.rootNode),
    };
  } finally {
    tree.delete();
  }
}
