import { Language, Parser, type Node, type Tree } from "web-tree-sitter";
import { ManagerError } from "./domain";

export type { Node, Tree };

let parser: Promise<Parser> | undefined;

/**
 * The tree-sitter Nix grammar, built as WebAssembly by nixpkgs. Its concrete
 * syntax tree keeps every byte, so edits replace exact source ranges and leave
 * comments and formatting untouched.
 */
export const nixParser = () =>
  (parser ??= (async () => {
    const grammar = process.env.NIX_MANAGER_NIX_GRAMMAR;
    if (!grammar)
      throw new ManagerError({ message: "NIX_MANAGER_NIX_GRAMMAR is not set." });
    await Parser.init();
    const result = new Parser();
    result.setLanguage(await Language.load(grammar));
    return result;
  })());

export const parseNix = async (source: string) => {
  const tree = (await nixParser()).parse(source);
  if (!tree) throw new ManagerError({ message: "The Nix parser returned no tree." });
  return tree;
};

/** Depth-first walk over named nodes. */
export function* descendants(node: Node): Generator<Node> {
  for (const child of node.namedChildren) {
    if (!child) continue;
    yield child;
    yield* descendants(child);
  }
}

export const identifier = (name: string) => /^[A-Za-z_][A-Za-z0-9_'-]*$/.test(name);

/** Attribute names of a static attrpath, or undefined for interpolated names. */
export const attrNames = (attrpath: Node): string[] | undefined => {
  const names: string[] = [];
  for (const attr of attrpath.childrenForFieldName("attr")) {
    if (!attr) return undefined;
    if (attr.type === "identifier") names.push(attr.text);
    else if (
      attr.type === "string_expression" &&
      attr.namedChildren.every((part) => part?.type === "string_fragment")
    )
      names.push(attr.namedChildren.map((part) => part!.text).join(""));
    else return undefined;
  }
  return names;
};

/** Dotted path of `a.b.c` style expressions, or undefined for anything computed. */
export const staticPath = (node: Node | null): string[] | undefined => {
  if (!node) return undefined;
  if (node.type === "variable_expression") return [node.childForFieldName("name")!.text];
  if (node.type === "select_expression" && !node.childForFieldName("default")) {
    const base = staticPath(node.childForFieldName("expression"));
    const rest = node.childForFieldName("attrpath");
    const names = rest && attrNames(rest);
    return base && names ? [...base, ...names] : undefined;
  }
  if (node.type === "parenthesized_expression")
    return staticPath(node.childForFieldName("expression"));
  return undefined;
};

export const renderPath = (path: readonly string[]) =>
  path.map((name) => (identifier(name) ? name : JSON.stringify(name))).join(".");
