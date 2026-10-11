import { mkdir, readdir, readFile, rename, rm } from "node:fs/promises";
import { join, posix, relative } from "node:path";
import { Effect, Schema } from "effect";
import { ManagerError, resolveHost, type Config } from "./domain";
import { scopeBindings, type Change } from "./draft";
import { attr } from "./nix";
import { hashText } from "./observe";
import { moduleShape } from "./options";
import type { AppInfo } from "./apps";
import type { UserAccount } from "./computer";
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

/** The package set's configuration and a hash of its attribute names, to notice an edit that replaces it. */
const nixpkgsFingerprint = `{
    config = builtins.mapAttrs (_: v: let r = tryEval (let x = plain 0 v; in builtins.deepSeq x x); in if r.success then r.value else "<error>") c.pkgs.config;
    names = builtins.hashString "sha256" (builtins.concatStringsSep "," (builtins.attrNames c.pkgs));
  }`;

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
  /** For a `nixpkgs.config` setting, the value the configuration's package set uses. */
  effective?: unknown;
}
export interface ConfigurationState {
  /** Packages each file adds to the package option; flake files are relative. */
  packages: { file: string; items: PackageInfo[] }[];
  options: Record<string, OptionValue>;
  /** Output path of each requested `pkgs` attribute, or null. */
  resolved: (string | null)[];
  /** For each probe, whether the option at its path has a definition in its file. */
  used: boolean[];
  /** With `nixpkgs` requested: the package set's configuration and a hash of its attribute names. */
  nixpkgs?: { config: Record<string, unknown>; names: string };
  /** Which non-free packages the configuration's package set accepts, when that evaluates. */
  unfree?: UnfreePolicy;
  /** With `users` requested: the accounts that can log in. */
  users?: UserAccount[];
}

export interface UnfreePolicy {
  /** `allowUnfree`. */
  all: boolean;
  /** `allowUnfreePackages`: package names allowed one by one. */
  packages: string[];
  /** An `allowUnfreePredicate` decides, which cannot be asked without the package. */
  predicate: boolean;
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
      effective: Schema.optional(Schema.Unknown),
    }),
  ),
  resolved: Schema.Array(Schema.NullOr(Schema.String)),
  used: Schema.Array(Schema.Boolean),
  nixpkgs: Schema.optional(
    Schema.NullOr(Schema.Struct({ config: Schema.Record({ key: Schema.String, value: Schema.Unknown }), names: Schema.String })),
  ),
  unfree: Schema.optional(
    Schema.NullOr(Schema.Struct({ all: Schema.Boolean, packages: Schema.Array(Schema.String), predicate: Schema.Boolean })),
  ),
  users: Schema.optional(
    Schema.NullOr(
      Schema.Array(
        Schema.Struct({
          name: Schema.String,
          description: Schema.NullOr(Schema.String),
          groups: Schema.Array(Schema.String),
          files: Schema.Array(Schema.String),
        }),
      ),
    ),
  ),
});

const decodeJson = <A, I>(schema: Schema.Schema<A, I>, label: string) => (text: string) =>
  Schema.decodeUnknown(Schema.parseJson(schema))(text).pipe(
    Effect.mapError((error) => new ManagerError({ message: `${label}: ${error}` })),
  );

// Normal users and the files whose `users.users` definitions name them.
const usersExpression = `
    let r = tryEval (let
      users = c.config.users.users;
      defs = c.options.users.users.definitionsWithLocations;
      names = builtins.filter (n: users.\${n}.isNormalUser or false) (builtins.attrNames users);
      mentions = n: d: let t = tryEval (isAttrs d.value && d.value ? \${n}); in t.success && t.value;
      x = map (n: {
        name = n;
        description = let d = users.\${n}.description or ""; in if builtins.isString d && d != "" then d else null;
        groups = builtins.filter builtins.isString (users.\${n}.extraGroups or [ ]);
        files = map (d: toString d.file) (builtins.filter (mentions n) defs);
      }) names;
    in builtins.deepSeq x x);
    in if r.success then r.value else null`;

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
    /** Describe the package set, to notice an edit that replaces it. */
    nixpkgs?: boolean;
    /** List the user accounts that can log in. */
    users?: boolean;
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
  # A setting below an option, such as nixpkgs.config.allowUnfree, exists when
  # the option's value is a set; its files are those whose definition has it.
  value = path:
    let
      at = optionAt path c.options;
      below = at != null && at.rest != [ ];
      parent = tryEval (isAttrs (get (builtins.genList (builtins.elemAt path) (builtins.length path - 1)) c.config));
      exists = at != null && (!below || (parent.success && parent.value));
      v = tryEval (let x = plain 0 (get path c.config); in builtins.deepSeq x x);
      defines = d: let r = tryEval (isAttrs d.value && get at.rest d.value != null); in r.success && r.value;
      files = tryEval (let x =
        if !exists then [ ]
        else if below then map (d: toString d.file) (builtins.filter defines at.option.definitionsWithLocations)
        else map toString at.option.files; in builtins.deepSeq x x);
      nixpkgsConfig = builtins.length path > 2 && builtins.head path == "nixpkgs" && builtins.elemAt path 1 == "config";
      effective = tryEval (let x = plain 0 (get (builtins.tail (builtins.tail path)) c.pkgs.config); in builtins.deepSeq x x);
    in
      { inherit exists; files = if files.success then files.value else [ ]; }
      // (if exists && v.success then { value = v.value; } else { error = true; })
      // (if nixpkgsConfig && effective.success then { effective = effective.value; } else { });
  resolve = path:
    let r = tryEval (let v = get path c.pkgs; in if isAttrs v && v ? outPath then v.outPath else null);
    in if r.success then r.value else null;
  # The option a setting path belongs to and the path below it: freeform settings sit below one.
  optionAt = path: o:
    if isAttrs o && (o._type or "") == "option" then { option = o; rest = path; }
    else if path == [ ] || !(isAttrs o) || !(o ? \${builtins.head path}) then null
    else optionAt (builtins.tail path) o.\${builtins.head path};
  used = probe:
    let
      at = optionAt probe.path c.options;
      o = if at == null then null else at.option;
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
  nixpkgs = ${request.nixpkgs ? nixpkgsFingerprint : "null"};
  unfree =
    let r = tryEval (let k = c.pkgs.config; x = {
      all = k.allowUnfree or false == true;
      packages = builtins.filter builtins.isString (k.allowUnfreePackages or [ ]);
      predicate = builtins.isFunction (k.allowUnfreePredicate or null);
    }; in builtins.deepSeq x x);
    in if r.success then r.value else null;
  users = ${request.users ? usersExpression : "null"};
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
            // Definitions without a file, such as Home Manager's own, cannot be edited.
            return [option, { ...rest, files: files.map(local).filter((file) => file !== "<unknown-file>") }];
          }),
        ),
        resolved: [...state.resolved],
        used: [...state.used],
        ...(state.nixpkgs ? { nixpkgs: state.nixpkgs } : {}),
        ...(state.unfree ? { unfree: { ...state.unfree, packages: [...state.unfree.packages] } } : {}),
        ...(state.users
          ? {
              users: state.users.map((user) => ({
                ...user,
                groups: [...user.groups],
                files: user.files.map(local).filter((file) => file !== "<unknown-file>"),
              })),
            }
          : {}),
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
    importable: boolean;
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
  /**
   * Version in the generation running now when the checkout builds another
   * one, or null when the running generation lacks the package. Absent when
   * nothing changes or the running generation is not this configuration's.
   */
  previous?: string | null;
}

/** Store path names of the generation a scope runs now, when the host is this computer. */
const runningGeneration = (config: Config, host: string, scope: Target) => {
  if (host !== config.hostKey || (scope === "home" && !config.homeProfileExists)) return Effect.succeed(undefined);
  const profile = scope === "home" ? config.homeProfile : "/run/current-system";
  return command("Read the running generation", ["nix-store", "--query", "--requisites", profile], {
    quiet: true,
    allowFailure: true,
  }).pipe(
    Effect.map((result) => {
      const paths = result.code === 0 ? result.stdout.split("\n").filter(Boolean) : [];
      return paths.length ? paths : undefined;
    }),
    Effect.orElseSucceed(() => undefined),
  );
};

/** What the running generation has of a package: nothing to report, another version, or nothing. */
export function previousVersion(paths: ReadonlySet<string>, names: readonly string[], item: PackageInfo) {
  if (paths.has(item.out)) return undefined;
  const prefix = `${item.name}-`;
  const versions = new Set(
    names.flatMap((name) => (name.startsWith(prefix) && /^\d/.test(name.slice(prefix.length)) ? [name.slice(prefix.length)] : [])),
  );
  // The same version built again, for example after a dependency changed.
  if (item.version && [...versions].some((version) => version === item.version || version.startsWith(`${item.version}-`)))
    return undefined;
  return versions.size ? [...versions].sort()[0] : null;
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
  /** Files the host imports that import others, which a new module file can be imported from. */
  importers: string[];
  /** Flake paths each importer imports. */
  imports: Record<string, string[]>;
  options: Record<string, OptionValue>;
  /** Which non-free packages it accepts; absent when that does not evaluate. */
  unfree?: UnfreePolicy;
  /** System scope: the accounts that can log in, when they evaluate. */
  users?: UserAccount[];
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
    const [state, running] = yield* Effect.all(
      [
        evaluateState(source, configurationAttr(config, host, scope), scope, {
          options,
          attrs: resolvable.map((item) => item.path!.slice(1)),
          probes,
          users: scope === "system",
        }),
        runningGeneration(config, host, scope),
      ],
      { concurrency: 2 },
    );
    const paths = new Set(running ?? []);
    const names = (running ?? []).map((path) => path.replace(/^\/nix\/store\/[a-z0-9]{32}-/, ""));
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
        ...(running ? { previous: previousVersion(paths, names, item) } : {}),
      })),
    );
    const bindings = scopeBindings[scope] as readonly string[];
    const importers = modules.filter((module) => module.importable && module.imports.length > 0 && used.has(module.file));
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
      importers: importers.map((module) => module.file),
      imports: Object.fromEntries(
        importers.map((module) => [
          module.file,
          module.imports.map((path) => posix.normalize(posix.join(posix.dirname(module.file), path))),
        ]),
      ),
      options: state.options,
      ...(state.unfree ? { unfree: state.unfree } : {}),
      ...(state.users ? { users: state.users } : {}),
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
  /** Built like a graphical app: with GTK or Qt wrapping or desktop entries. */
  g?: boolean;
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
      g = let
        hooks = [ "wrap-gapps-hook" "wrap-gapps-hook3" "wrap-gapps-hook4" "wrap-qt5-apps-hook" "wrap-qt6-apps-hook" "copy-desktop-items-hook" "desktop-file-utils" ];
        hook = x: isAttrs x && builtins.elem (x.pname or x.name or "") hooks;
      in (v ? desktopItems) || (v ? desktopItem) || builtins.any hook (v.nativeBuildInputs or [ ]);
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
        // Catalogs of earlier lock files or versions are not read again.
        const directory = join(target, "..");
        for (const name of await readdir(directory))
          if (name.endsWith(".json") && join(directory, name) !== target) await rm(join(directory, name), { force: true });
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
    // NixOS documents tens of thousands of options: more than the usual capture.
    { quiet: true, fullOutput: true },
  ).pipe(
    Effect.flatMap((result) =>
      Effect.try({
        try: () => JSON.parse(result.stdout) as OptionDoc[],
        catch: (error) => new ManagerError({ message: `Invalid option documentation: ${error}` }),
      }),
    ),
  );

const words = (text: string) => text.toLowerCase().split(/[^a-z0-9+]+/).filter(Boolean);

/** Search restrictions: only free licenses, only packages not marked broken. */
export interface CatalogFilter {
  free?: boolean;
  working?: boolean;
}

/**
 * Ranks catalog entries: exact names first, then prefixes, then descriptions.
 * Apps are also found by their shown name, summary and keywords, and come
 * before other packages that match as well.
 */
export function searchCatalog(
  entries: readonly CatalogEntry[],
  query: string,
  limit = 60,
  filter: CatalogFilter = {},
  apps: ReadonlyMap<string, AppInfo> = new Map(),
) {
  const terms = words(query);
  if (!terms.length) return [];
  const scored: { entry: CatalogEntry; score: number }[] = [];
  for (const entry of entries) {
    if ((filter.free && !entry.f) || (filter.working && entry.b)) continue;
    const name = (entry.n ?? entry.a).toLowerCase();
    const attribute = entry.a.toLowerCase();
    const leaf = attribute.slice(attribute.lastIndexOf(".") + 1);
    const program = entry.p?.toLowerCase();
    const app = apps.get(entry.a);
    const shown = app?.name.toLowerCase();
    const description = `${entry.d ?? ""} ${app ? `${app.summary ?? ""} ${app.keywords.join(" ")}` : ""}`.toLowerCase();
    let score = 0;
    for (const term of terms) {
      let best = 0;
      if (name === term || leaf === term || program === term || shown === term) best = 100;
      else if (name.startsWith(term) || leaf.startsWith(term) || shown?.split(/\s+/).some((word) => word.startsWith(term))) best = 60;
      else if (attribute.includes(term) || name.includes(term) || shown?.includes(term)) best = 35;
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
    if (app) score += 15;
    else if (entry.g) score += 5;
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

/** Raised when catalog entries gain fields, so older files are built again. */
const catalogVersion = 2;
export const catalogPath = (config: Config, lockHash: string) =>
  join(config.stateDir, "catalog", `${lockHash.slice(0, 32)}.v${catalogVersion}.json`);

export const hashKey = (...parts: string[]) => hashText(parts.join("\0"));

/** Changes in a draft that edit a scope, grouped by kind. */
export const scopeChanges = (changes: readonly Change[], scope: Target) =>
  changes.filter((change) => change.scope === scope);

export { renderPath };
