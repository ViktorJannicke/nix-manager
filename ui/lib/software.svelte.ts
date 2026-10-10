import type { Change } from "../../src/draft";
import type { Experience } from "../../src/curated";
import type {
  CatalogEntry,
  InstalledPackage,
  ModuleFile,
  OptionDoc,
  ScopeInventory,
  Target,
} from "../../src/software";
import type { Scope } from "../../src/domain";
import { api } from "./api";
import { app } from "./app.svelte";

export type Inventory = Record<Target, ScopeInventory>;
const message = (error: unknown) => (error instanceof Error ? error.message : String(error));
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);
const remembered = (scope: Target) => localStorage.getItem(`nix-manager-file-${scope}`) || undefined;

/** Select value standing for a module file the edits create. */
export const NEW_FILE = "\0new";

const directory = (file: string) => file.split("/").slice(0, -1).join("/");

/** Files inside the flake are relative; module files from elsewhere are store paths. */
export const local = (file: string) => !file.startsWith("/");

/** "Home Manager: programs/git.nix" style names for module files outside the flake. */
export function origin(file: string) {
  const inner = file.replace(/^\/nix\/store\/[a-z0-9]{32}-[^/]+\//, "");
  if (inner === file) return file;
  return inner.startsWith("nixos/modules/") ? `NixOS: ${inner.slice(14)}` : `Home Manager: ${inner.replace(/^modules\//, "")}`;
}

class SoftwareState {
  inventory = $state.raw<Inventory>();
  loading = $state(false);
  error = $state<string>();
  results = $state.raw<CatalogEntry[]>([]);
  searched = $state("");
  /** Option documentation by `scope:name`. */
  docs = $state.raw<Record<string, OptionDoc>>({});
  saving = $state(false);
  private key = "";
  private searchToken = 0;

  draft = $derived(app.snapshot?.draft);
  changes = $derived(this.draft?.changes ?? []);
  catalog = $derived(app.snapshot?.catalog);

  /** Reads what the selected host installs; repeated calls reuse the result for the same checkout. */
  async load(force = false) {
    const key = `${app.host}\0${app.system?.repository.fingerprint ?? ""}`;
    if (!force && (key === this.key || !app.system)) return;
    this.key = key;
    this.loading = true;
    this.error = undefined;
    try {
      const inventory = await api<Inventory>("software/inventory", { host: app.host }, 240_000);
      if (this.key === key) this.inventory = inventory;
    } catch (error) {
      if (this.key === key) {
        this.error = message(error);
        this.key = "";
      }
    } finally {
      if (this.key === key || !this.key) this.loading = false;
    }
  }

  async search(query: string) {
    const token = ++this.searchToken;
    if (!query.trim()) {
      this.results = [];
      this.searched = "";
      return;
    }
    try {
      const results = await api<CatalogEntry[]>("software/search", { query });
      if (token === this.searchToken) {
        this.results = results;
        this.searched = query;
      }
    } catch (error) {
      app.fail(error, "Search failed");
    }
  }

  async buildCatalog(rebuild = false) {
    try {
      await api("software/catalog", { rebuild });
    } catch (error) {
      app.fail(error, "Could not build the package index");
    }
  }

  /** Loads documentation for options not seen yet. */
  async describe(scope: Target, names: string[]) {
    const missing = names.filter((name) => !this.docs[`${scope}:${name}`]);
    if (!missing.length) return;
    try {
      const docs = await api<OptionDoc[]>("software/describe", { host: app.host, scope, options: missing }, 120_000);
      this.docs = { ...this.docs, ...Object.fromEntries(docs.map((doc) => [`${scope}:${doc.n}`, doc])) };
    } catch (error) {
      app.fail(error, "Could not read option documentation");
    }
  }

  doc(scope: Target, name: string) {
    return this.docs[`${scope}:${name}`];
  }

  // --- Installed state ------------------------------------------------------

  /** The installed package an entry of the catalog refers to, in either scope. */
  installed(entry: CatalogEntry): (InstalledPackage & { scope: Target }) | undefined {
    for (const scope of ["home", "system"] as const) {
      const packages = this.inventory?.[scope].packages ?? [];
      const found =
        packages.find((item) => item.attr === `pkgs.${entry.a}`) ??
        (entry.n ? packages.find((item) => item.name === entry.n && !item.attr) : undefined);
      if (found) return { ...found, scope };
    }
    return undefined;
  }

  pendingPackage(attr: string) {
    return this.changes.find((change) => "attr" in change && change.attr === attr);
  }

  /** Current value of an option, with a pending edit taking precedence. */
  value(scope: Target, option: string) {
    const pending = [...this.changes]
      .reverse()
      .find((change) => "option" in change && change.scope === scope && change.option === option);
    if (pending?.kind === "set-option") return { value: pending.value, pending: true };
    if (pending?.kind === "unset-option") return { value: undefined, pending: true };
    return { value: this.inventory?.[scope].options[option]?.value, pending: false };
  }

  available(experience: Experience) {
    const options = this.inventory?.[experience.scope].options;
    return Object.keys(experience.enable).every((option) => options?.[option]?.exists);
  }

  enabled(experience: Experience) {
    return Object.entries(experience.enable).every(([option, wanted]) =>
      same(this.value(experience.scope, option).value, wanted),
    );
  }

  /** Flake file that defines an option now, if exactly one does. */
  definedIn(scope: Target, option: string) {
    const files = (this.inventory?.[scope].options[option]?.files ?? []).filter(local);
    return files.length === 1 ? files[0] : undefined;
  }

  /**
   * Module files that could hold an option, best first: files this host
   * imports, the last one used, files of the same kind of configuration, then
   * files that already set options under the same name or live in a folder of
   * that name. Packages, overlays, data, tests and docs are left out.
   */
  moduleFiles(scope: Target, option: string) {
    const head = option.split(".")[0];
    const modules = (this.inventory?.[scope].modules ?? []).filter(
      (module) => !/(^|\/)(pkgs|overlays?|data|tests?|docs)\//.test(module.file),
    );
    const own = scope === "home" ? /home/ : /nixos|system/;
    const other = scope === "home" ? /nixos|system/ : /home/;
    const score = (module: ModuleFile) =>
      (module.active === true ? 200 : module.active === false ? -200 : 0) +
      (module.file === remembered(scope) ? 100 : 0) +
      (own.test(module.file) ? 20 : 0) -
      (other.test(module.file) ? 40 : 0) +
      (module.names.some((name) => name.split(".")[0] === head) ? 6 : 0) +
      (module.file.split("/").includes(head) ? 4 : 0) -
      (/hosts\//.test(module.file) ? 3 : 0);
    return [...modules].sort((a, b) => score(b) - score(a) || a.file.localeCompare(b.file));
  }

  /** Files with a package list for a scope: the last one used, then active ones holding the most packages. */
  packageFiles(scope: Target) {
    const files = this.inventory?.[scope].files ?? [];
    const last = remembered(scope);
    return [...files].sort(
      (a, b) =>
        Number(b.file === last) - Number(a.file === last) ||
        Number(b.active) - Number(a.active) ||
        b.count - a.count ||
        a.file.localeCompare(b.file),
    );
  }

  /** Every flake file the inventory or the draft knows of, for refusing a new file over an existing one. */
  private known(scope: Target) {
    const inventory = this.inventory?.[scope];
    return new Set([
      ...(inventory?.modules.map((module) => module.file) ?? []),
      ...(inventory?.files.map((file) => file.file) ?? []),
      ...(inventory?.importers ?? []),
      ...this.changes.filter((change) => change.kind === "create-module").map((change) => change.file),
    ]);
  }

  /** A free path for a new module named `name`, next to the file new settings would go to. */
  suggestFile(scope: Target, name: string, near?: string) {
    const folder = near ? directory(near) : scope === "home" ? "modules/home" : "modules/nixos";
    const slug = name.toLowerCase().replace(/[^a-z0-9+-]+/g, "-").replace(/^-|-$/g, "") || "module";
    const known = this.known(scope);
    for (let n = 1; ; n++) {
      const file = `${folder ? `${folder}/` : ""}${slug}${n > 1 ? `-${n}` : ""}.nix`;
      if (!known.has(file)) return file;
    }
  }

  /** Why a path cannot be a new module file, if it cannot. */
  newFileProblem(scope: Target, file: string) {
    if (!/^([A-Za-z0-9_+-][A-Za-z0-9._+-]*\/)*[A-Za-z0-9_+-][A-Za-z0-9._+-]*\.nix$/.test(file))
      return "Use a path inside the flake ending in .nix, with letters, digits, dots, dashes and slashes.";
    if (this.known(scope).has(file)) return "This file exists already.";
    return undefined;
  }

  /**
   * The imported file a new module is best imported from: the folder's
   * default.nix, a file in the same folder, then the closest folder above.
   */
  importers(scope: Target, file: string) {
    const folder = directory(file);
    const score = (candidate: string) => {
      const at = directory(candidate);
      const inside = folder === at || folder.startsWith(`${at}/`) || !at;
      const depth = at ? at.split("/").length : 0;
      return (inside ? 100 + depth * 10 : 0) + (candidate.endsWith("/default.nix") || candidate === "default.nix" ? 5 : 0);
    };
    return [...(this.inventory?.[scope].importers ?? [])].sort((a, b) => score(b) - score(a) || a.localeCompare(b));
  }

  remember(scope: Target, file: string) {
    localStorage.setItem(`nix-manager-file-${scope}`, file);
  }

  // --- Draft ----------------------------------------------------------------

  async save(changes: Change[]) {
    this.saving = true;
    try {
      await api("draft", { changes });
    } catch (error) {
      app.fail(error, "Could not save the edit");
    } finally {
      this.saving = false;
    }
  }

  /**
   * Adds edits to the draft. An edit replaces a pending edit of the same
   * option, and cancels a pending opposite package edit instead of stacking.
   */
  stage(next: Change[]) {
    let changes = [...this.changes];
    for (const change of next) {
      if (change.kind === "create-module") {
        if (!changes.some((item) => item.kind === "create-module" && item.file === change.file)) changes.push(change);
      } else if ("attr" in change) {
        const opposite = changes.findIndex(
          (item) => "attr" in item && item.attr === change.attr && item.file === change.file && item.kind !== change.kind,
        );
        if (opposite >= 0) {
          changes.splice(opposite, 1);
          continue;
        }
        if (changes.some((item) => same(item, change))) continue;
        changes.push(change);
      } else {
        changes = changes.filter(
          (item) => !("option" in item && item.option === change.option && item.scope === change.scope),
        );
        // Setting an option back to its current value needs no edit.
        const current = this.inventory?.[change.scope].options[change.option];
        if (change.kind === "set-option" && current && !current.error && same(current.value, change.value)) continue;
        changes.push(change);
      }
    }
    return this.save(changes);
  }

  /** Removes an edit; removing a created file also removes the edits written into it. */
  unstage(index: number) {
    const removed = this.changes[index];
    return this.save(
      this.changes.filter(
        (change, position) => position !== index && !(removed?.kind === "create-module" && change.file === removed.file),
      ),
    );
  }

  clear() {
    return this.save([]);
  }

  /** The scope a review must build to include every pending edit. */
  scope = $derived.by((): Scope => {
    const scopes = new Set(this.changes.map((change) => change.scope));
    return scopes.size > 1 ? "all" : scopes.has("system") ? "system" : "home";
  });

  prepare() {
    return app.prepare(this.scope, { edits: true });
  }
}

export const software = new SoftwareState();

const show = (value: unknown) => {
  if (value === true) return "on";
  if (value === false) return "off";
  if (value === null || value === undefined) return "not set";
  if (Array.isArray(value)) return value.length ? value.join(", ") : "empty";
  return typeof value === "string" ? `"${value}"` : JSON.stringify(value);
};
export { show as showValue };

/** A pending edit in words: what it does, and where. */
export function describeChange(change: Change) {
  switch (change.kind) {
    case "add-package":
      return { title: `Install ${change.attr.replace(/^pkgs\./, "")}`, detail: change.file };
    case "remove-package":
      return { title: `Remove ${change.attr.replace(/^pkgs\./, "")}`, detail: change.file };
    case "set-option":
      return { title: `${change.label ? `${change.label}: ` : ""}${change.option} ${show(change.value)}`, detail: change.file };
    case "unset-option":
      return { title: `Reset ${change.option} to its default`, detail: change.file };
    case "create-module":
      return { title: `Create ${change.file.split("/").at(-1)}`, detail: `${change.file}, imported from ${change.importer}` };
  }
}
