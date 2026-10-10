import { mkdir, readdir, readFile, rename, rm } from "node:fs/promises";
import { join, posix, relative } from "node:path";
import { Effect, Schema } from "effect";
import { ManagerError, resolveHost, type Config } from "./domain";
import { scopeBindings, type Change } from "./draft";
import { attr } from "./nix";
import { hashText } from "./observe";
import { moduleShape } from "./options";
import { packageLists, flakeSource } from "./packages";
import { command } from "./process";
import { parseNix, renderPath } from "./syntax";

export type Target = "home" | "system";

/** Attribute of a host's Home Manager or NixOS configuration. */
export const configurationAttr = (config: Config, host: string, scope: Target) => {
  const [, h] = resolveHost(config.hosts, host || config.hostKey);
  return scope === "home"
    ? `homeConfigurations.${attr(h.homeConfiguration)}`
    : `nixosConfigurations.${attr(h.nixosConfiguration)}`;
};

const nixString = (text: string) =>
  `"${text.replaceAll("\\", "\\\\").replaceAll('"', '\\"').replaceAll("${", "\\${")}"`;
const nixPaths = (paths: readonly (readonly string[])[]) =>
  `[ ${paths.map((path) => `[ ${path.map(nixString).join(" ")} ]`).join(" ")} ]`;

/** Names the module system may record for a flake file: `dir/default.nix` is also imported as `dir`. */
const moduleKeys = (source: string, file: string) => {
  const path = `${source}/${file}`;
  return file.endsWith("/default.nix") ? [path, path.slice(0, -"/default.nix".length)] : file === "default.nix" ? [path, source] : [path];
};

/** Evaluation helpers shared by every expression applied to a configuration. */
const prelude = `
  inherit (builtins) tryEval isAttrs isList isFunction concatMap elem;
  get = path: set:
    if path == [ ] then set
    else if isAttrs set && set ? \${builtins.head path} then get (builtins.tail path) set.\${builtins.head path}
    else null;
  str = v: if builtins.isString v then v else null;
  # JSON-safe data: packages become store paths, functions disappear.
  plain = depth: v:
    if depth > 8 || isFunction v then null
    else if isAttrs v then (if v ? outPath then toString v.outPath else builtins.mapAttrs (_: plain (depth + 1)) v)
    else if isList v then map (plain (depth + 1)) v
    else if builtins.isPath v then toString v
    else v;
`;

const packagesOption = (scope: Target) =>
  scope === "home" ? "home.packages" : "environment.systemPackages";

export interface PackageInfo {
  out: string;
  name: string;
  version?: string | null;
  description?: string | null;
}
export interface OptionValue {
  value?: unknown;
  /** Evaluation failed, or the option does not exist. */
  error?: boolean;
  exists: boolean;
  files: string[];
}
export interface ConfigurationState {
  /** Packages each file adds to the package option; flake files are relative. */
  packages: { file: string; items: PackageInfo[] }[];
  options: Record<string, OptionValue>;
  /** Output path of each requested `pkgs` attribute, or null. */
  resolved: (string | null)[];
  /** For each probe, whether the option at its path has a definition in its file. */
  used: boolean[];
}

/** A setting a flake file writes, to check whether the configuration imports that file. */
export interface Probe {
  file: string;
  option: string;
}

const StateSchema = Schema.Struct({
  packages: Schema.Array(
    Schema.Struct({
      file: Schema.String,
      items: Schema.Array(
        Schema.Struct({
          out: Schema.String,
          name: Schema.String,
          version: Schema.NullOr(Schema.String),
          description: Schema.NullOr(Schema.String),
        }),
      ),
    }),
  ),
  options: Schema.Array(
    Schema.Struct({
      value: Schema.optional(Schema.Unknown),
      error: Schema.optional(Schema.Boolean),
      exists: Schema.Boolean,
      files: Schema.Array(Schema.String),
    }),
  ),
  resolved: Schema.Array(Schema.NullOr(Schema.String)),
  used: Schema.Array(Schema.Boolean),
});

const decodeJson = <A, I>(schema: Schema.Schema<A, I>, label: string) => (text: string) =>
  Schema.decodeUnknown(Schema.parseJson(schema))(text).pipe(
    Effect.mapError((error) => new ManagerError({ message: `${label}: ${error}` })),
  );

/**
 * One evaluation of a configuration: the packages each file contributes, the
 * values and definition files of options, and the output paths of package
 * attributes. `source` is a store path of the flake's files.
 */
export const evaluateState = (
  source: string,
  configuration: string,
  scope: Target,
  request: {
    options?: readonly string[];
    attrs?: readonly (readonly string[])[];
    probes?: readonly Probe[];
  } = {},
) => {
  const options = [...new Set(request.options ?? [])];
  const expression = `c:
let
  ${prelude}
  info = p:
    let v = {
      out = p.outPath;
      name = (p.pname or (builtins.parseDrvName (p.name or "unknown")).name);
      version = str (p.version or null);
      description = str (p.meta.description or null);
    }; r = tryEval (builtins.deepSeq v v);
    in if r.success then [ r.value ] else [ ];
  # List items can carry their own mkIf, mkOverride or mkOrder.
  items = p:
    if !isAttrs p then [ ]
    else if p ? outPath then info p
    else if (p._type or "") == "if" then (if p.condition then items p.content else [ ])
    else if elem (p._type or "") [ "override" "order" ] then items p.content
    else [ ];
  value = path:
    let
      option = get path c.options;
      exists = isAttrs option && (option._type or "") == "option";
      v = tryEval (let x = plain 0 (get path c.config); in builtins.deepSeq x x);
      files = tryEval (let x = if exists then map toString option.files else [ ]; in builtins.deepSeq x x);
    in
      { inherit exists; files = if files.success then files.value else [ ]; }
      // (if exists && v.success then { value = v.value; } else { error = true; });
  resolve = path:
    let r = tryEval (let v = get path c.pkgs; in if isAttrs v && v ? outPath then v.outPath else null);
    in if r.success then r.value else null;
  # The option a setting path belongs to: freeform settings sit below one.
  optionAt = path: o:
    if isAttrs o && (o._type or "") == "option" then o
    else if path == [ ] || !(isAttrs o) || !(o ? \${builtins.head path}) then null
    else optionAt (builtins.tail path) o.\${builtins.head path};
  used = probe:
    let
      o = optionAt probe.path c.options;
      r = tryEval (o != null && builtins.any (file: elem (toString file) probe.files) o.files);
    in r.success && r.value;
in {
  packages = map (d: { file = d.file; items = concatMap items d.value; })
    c.options.${packagesOption(scope)}.definitionsWithLocations;
  options = map value ${nixPaths(options.map((option) => option.split(".")))};
  resolved = map resolve ${nixPaths(request.attrs ?? [])};
  used = map used [ ${(request.probes ?? [])
    .map((probe) => `{ files = [ ${moduleKeys(source, probe.file).map(nixString).join(" ")} ]; path = [ ${probe.option.split(".").map(nixString).join(" ")} ]; }`)
    .join(" ")} ];
}`;
  return command(
    "Evaluate configuration",
    ["nix", "eval", "--json", `path:${source}#${configuration}`, "--apply", expression],
    { quiet: true },
  ).pipe(
    Effect.flatMap((result) => decodeJson(StateSchema, "Invalid configuration evaluation")(result.stdout)),
    Effect.map((state): ConfigurationState => {
      const local = (file: string) => {
        if (!file.startsWith(`${source}/`)) return file;
        const path = file.slice(source.length + 1);
        // A directory import names the directory; its module is default.nix.
        return path.endsWith(".nix") ? path : `${path}/default.nix`;
      };
      const packages = new Map<string, PackageInfo[]>();
      for (const { file, items } of state.packages) {
        const key = local(file);
        packages.set(key, [...(packages.get(key) ?? []), ...items]);
      }
      return {
        packages: [...packages].map(([file, items]) => ({ file, items })),
        options: Object.fromEntries(
          options.map((option, index) => {
            const { files, ...rest } = state.options[index];
            return [option, { ...rest, files: files.map(local) }];
          }),
        ),
        resolved: [...state.resolved],
        used: [...state.used],
      };
    }),
  );
};

/** `.nix` files of a flake source, relative to it. */
export async function nixFiles(source: string) {
  const found: string[] = [];
  const walk = async (directory: string) => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) await walk(path);
      else if (entry.name.endsWith(".nix")) found.push(relative(source, path));
    }
  };
  await walk(source);
  return found.sort();
}

export interface SourceList {
  file: string;
  binding: string;
  attr?: string;
  path?: string[];
  conditional: boolean;
}

/** Package list items of every file, for mapping evaluated packages back to source. */
export async function sourceLists(source: string) {
  const items: SourceList[] = [];
  const modules: {
    file: string;
    editable: boolean;
    names: string[];
    settings: string[];
    imports: string[];
    lists: string[];
  }[] = [];
  for (const file of await nixFiles(source)) {
    const text = await readFile(join(source, file), "utf8");
    const tree = await parseNix(text);
    try {
      const lists = packageLists(tree.rootNode);
      for (const list of lists)
        for (const item of list.items)
          items.push({ file, binding: list.binding, attr: item.attr, path: item.path, conditional: list.conditional });
      modules.push({
        file,
        ...(await moduleShape(text)),
        lists: [...new Set(lists.filter((list) => !list.conditional).map((list) => list.binding))],
      });
    } finally {
      tree.delete();
    }
  }
  return { items, modules };
}

/**
 * Adds the files that used files import unconditionally, transitively: a
 * module that only imports others shows no settings of its own.
 */
export function importedFiles(
  modules: readonly { file: string; imports: readonly string[] }[],
  used: Iterable<string>,
) {
  const known = new Map(modules.map((module) => [module.file, module.imports]));
  const result = new Set(used);
  const queue = [...result];
  while (queue.length) {
    const file = queue.pop()!;
    for (const target of known.get(file) ?? []) {
      const path = posix.normalize(posix.join(posix.dirname(file), target));
      const child = path.endsWith(".nix") ? path : `${path}/default.nix`;
      if (known.has(child) && !result.has(child)) {
        result.add(child);
        queue.push(child);
      }
    }
  }
  return result;
}

export interface InstalledPackage extends PackageInfo {
  /** Flake-relative file, or a module path outside the flake. */
  file: string;
  /** Source attribute such as `pkgs.krita` when the item can be removed by editing. */
  attr?: string;
  /** Added by a module (for example `programs.git.enable`), not a package list. */
  external: boolean;
}

export interface PackageFile {
  file: string;
  bindings: string[];
  /** The file contributes packages to this host's configuration. */
  active: boolean;
  count: number;
}

export interface ModuleFile {
  file: string;
  names: string[];
  /**
   * The host's configuration imports this file: one of its settings is defined
   * there. Null when the file sets nothing that could show it (only imports).
   */
  active: boolean | null;
}

export interface ScopeInventory {
  scope: Target;
  packages: InstalledPackage[];
  /** Files with a package list this scope could add to. */
  files: PackageFile[];
  /** Module files whose settings can be edited. */
  modules: ModuleFile[];
  options: Record<string, OptionValue>;
}

/** What a host's configuration installs, mapped back to the lines that install it. */
export const inventory = (
  config: Config,
  host: string,
  scope: Target,
  options: readonly string[] = [],
) =>
  Effect.gen(function* () {
    const source = yield* flakeSource(config.flake);
    const { items, modules } = yield* Effect.promise(() => sourceLists(source));
    const resolvable = items.filter((item) => item.path?.[0] === "pkgs" && item.path.length > 1);
    const editable = modules.filter((module) => module.editable && module.file !== "flake.nix");
    const probes = editable.flatMap((module) =>
      module.settings.slice(0, 4).map((option): Probe => ({ file: module.file, option })),
    );
    const state = yield* evaluateState(source, configurationAttr(config, host, scope), scope, {
      options,
      attrs: resolvable.map((item) => item.path!.slice(1)),
      probes,
    });
    const counts = new Map(state.packages.map(({ file, items }) => [file, items.length]));
    const used = importedFiles(modules, [
      ...probes.filter((_, index) => state.used[index]).map((probe) => probe.file),
      ...counts.keys(),
    ]);
    const bySource = new Map<string, string>();
    resolvable.forEach((item, index) => {
      const out = state.resolved[index];
      if (out) bySource.set(`${item.file}\0${out}`, item.attr!);
    });
    const files = new Set(modules.map((module) => module.file));
    const packages = state.packages.flatMap(({ file, items }) =>
      items.map((item): InstalledPackage => ({
        ...item,
        file,
        attr: bySource.get(`${file}\0${item.out}`),
        external: !files.has(file),
      })),
    );
    const bindings = scopeBindings[scope] as readonly string[];
    return {
      scope,
      packages: packages.sort((a, b) => a.name.localeCompare(b.name)),
      files: modules
        .filter((module) => module.lists.some((binding) => bindings.includes(binding)))
        .map((module) => ({
          file: module.file,
          bindings: module.lists,
          active: used.has(module.file),
          count: counts.get(module.file) ?? 0,
        }))
        .sort((a, b) => Number(b.active) - Number(a.active) || a.file.localeCompare(b.file)),
      modules: editable.map(({ file, names, settings }): ModuleFile => ({
        file,
        names,
        active: used.has(file) ? true : settings.length ? false : null,
      })),
      options: state.options,
    } satisfies ScopeInventory;
  });

export interface CatalogEntry {
  /** Attribute path below `pkgs`. */
  a: string;
  n: string | null;
  v: string | null;
  d: string | null;
  h: string | null;
  /** Main program. */
  p: string | null;
  l: string[];
  /** Free license. */
  f: boolean;
  b: boolean;
}

/** Walks `pkgs` (and package sets that ask to be recursed into) for searchable metadata. */
const catalogExpression = `pkgs:
let
  inherit (builtins) tryEval isAttrs attrNames concatMap;
  str = v: if builtins.isString v then v else null;
  entry = path: v:
    let
      m = v.meta or { };
      license = m.license or null;
      licenses = if builtins.isList license then license else if license == null then [ ] else [ license ];
    in {
      a = builtins.concatStringsSep "." path;
      n = str (v.pname or null);
      v = str (v.version or null);
      d = str (m.description or null);
      h = let x = m.homepage or null; in if builtins.isList x then str (builtins.head x) else str x;
      p = str (m.mainProgram or null);
      l = map (x: if isAttrs x then (x.spdxId or x.shortName or "unknown") else toString x) licenses;
      f = builtins.all (x: !isAttrs x || (x.free or true)) licenses;
      b = m.broken or false;
    };
  walk = depth: path: set:
    concatMap (name:
      let r = tryEval set.\${name}; v = r.value; in
      if !r.success then [ ]
      else if isAttrs v && (v.type or null) == "derivation" then
        let e = tryEval (let x = entry (path ++ [ name ]) v; in builtins.deepSeq x x); in if e.success then [ e.value ] else [ ]
      else if depth < 2 && isAttrs v && (v.recurseForDerivations or false) then walk (depth + 1) (path ++ [ name ]) v
      else [ ]
    ) (attrNames set);
in walk 0 [ ] pkgs`;

/**
 * Writes the package catalog of a configuration's `pkgs`, including overlays.
 * The evaluation visits about a hundred thousand packages; its output goes
 * straight to a file instead of the bounded command capture.
 */
export const buildCatalog = (config: Config, host: string, target: string) =>
  Effect.tryPromise({
    try: async (signal) => {
      await mkdir(join(target, ".."), { recursive: true, mode: 0o700 });
      const temporary = `${target}.${crypto.randomUUID()}.tmp`;
      try {
        const child = Bun.spawn(
          [
            "nix",
            "eval",
            "--json",
            `${config.flake}#${configurationAttr(config, host, "home")}.pkgs`,
            "--apply",
            catalogExpression,
          ],
          { stdin: "ignore", stdout: Bun.file(temporary), stderr: "pipe", signal },
        );
        const [code, stderr] = await Promise.all([child.exited, new Response(child.stderr).text()]);
        if (code !== 0)
          throw new ManagerError({ message: `Package catalog evaluation failed. ${stderr.slice(-1200).trim()}` });
        await rename(temporary, target);
      } finally {
        await rm(temporary, { force: true });
      }
    },
    catch: (error) =>
      error instanceof ManagerError ? error : new ManagerError({ message: `Build package catalog: ${error}` }),
  });

export interface OptionDoc {
  /** Option path. */
  n: string;
  /** Type description, such as "boolean" or "null or string". */
  t: string | null;
  d: string | null;
  /** Default as Nix text. */
  df: string | null;
  r: boolean;
  /** Declaring files. */
  decl: string[];
}

const optionsExpression = `c:
let
  lib = c.pkgs.lib;
  visible = builtins.filter (o: (o.visible or true) && !(o.internal or false)) (lib.optionAttrSetToDocList c.options);
in map (o: {
  n = o.name;
  t = o.type or null;
  d = if builtins.isString (o.description or null) then o.description else null;
  df = if o ? default then (o.default.text or (builtins.toJSON o.default)) else null;
  r = o.readOnly or false;
  decl = map toString (o.declarations or [ ]);
}) visible`;

/** Documented options of a configuration, as `optionAttrSetToDocList` describes them. */
export const optionDocs = (config: Config, host: string, scope: Target) =>
  command(
    "Read option documentation",
    ["nix", "eval", "--json", `${config.flake}#${configurationAttr(config, host, scope)}`, "--apply", optionsExpression],
    { quiet: true },
  ).pipe(Effect.map((result) => JSON.parse(result.stdout) as OptionDoc[]));

const words = (text: string) => text.toLowerCase().split(/[^a-z0-9+]+/).filter(Boolean);

/** Ranks catalog entries: exact names first, then prefixes, then descriptions. */
export function searchCatalog(entries: readonly CatalogEntry[], query: string, limit = 60) {
  const terms = words(query);
  if (!terms.length) return [];
  const scored: { entry: CatalogEntry; score: number }[] = [];
  for (const entry of entries) {
    const name = (entry.n ?? entry.a).toLowerCase();
    const attribute = entry.a.toLowerCase();
    const leaf = attribute.slice(attribute.lastIndexOf(".") + 1);
    const program = entry.p?.toLowerCase();
    const description = entry.d?.toLowerCase() ?? "";
    let score = 0;
    for (const term of terms) {
      let best = 0;
      if (name === term || leaf === term || program === term) best = 100;
      else if (name.startsWith(term) || leaf.startsWith(term)) best = 60;
      else if (attribute.includes(term) || name.includes(term)) best = 35;
      else if (program?.includes(term)) best = 30;
      else if (new RegExp(`\\b${term.replace(/[+]/g, "\\+")}`).test(description)) best = 12;
      if (!best) {
        score = 0;
        break;
      }
      score += best;
    }
    if (!score) continue;
    // Prefer top-level packages, working builds and shorter, canonical names.
    if (!entry.a.includes(".")) score += 6;
    if (entry.b) score -= 40;
    if (!entry.d) score -= 5;
    score -= Math.min(10, entry.a.length / 6);
    scored.push({ entry, score });
  }
  return scored
    .sort((a, b) => b.score - a.score || a.entry.a.localeCompare(b.entry.a))
    .slice(0, limit)
    .map(({ entry }) => entry);
}

/** Cached results keyed by the checkout state they were computed from. */
export class Memo<A> {
  private entries = new Map<string, { key: string; value: Promise<A> }>();
  get(slot: string, key: string, compute: () => Promise<A>) {
    const hit = this.entries.get(slot);
    if (hit?.key === key) return hit.value;
    const value = compute();
    this.entries.set(slot, { key, value });
    // Failures are not cached.
    value.catch(() => {
      if (this.entries.get(slot)?.value === value) this.entries.delete(slot);
    });
    return value;
  }
  clear() {
    this.entries.clear();
  }
}

export const catalogPath = (config: Config, lockHash: string) =>
  join(config.stateDir, "catalog", `${lockHash.slice(0, 32)}.json`);

export const hashKey = (...parts: string[]) => hashText(parts.join("\0"));

/** Changes in a draft that edit a scope, grouped by kind. */
export const scopeChanges = (changes: readonly Change[], scope: Target) =>
  changes.filter((change) => change.scope === scope);

export { renderPath };
