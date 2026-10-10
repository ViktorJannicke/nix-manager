import type { Change } from "../../src/draft";
import { acceptsUnfree, changesScope, dropChange, featureModule, freePath, mergeChanges, newFileProblem, rankImporters, rankModules } from "../../src/changes";
import { experiences, type Experience } from "../../src/curated";
import type {
  CatalogEntry,
  InstalledPackage,
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

const UNFREE = "nixpkgs.config.allowUnfree";

/** Select value standing for a module file the edits create. */
export const NEW_FILE = "\0new";

/** Files inside the flake are relative; module files from elsewhere are store paths. */
export const local = (file: string) => !file.startsWith("/");


class SoftwareState {
  inventory = $state.raw<Inventory>();
  loading = $state(false);
  error = $state<string>();
  results = $state.raw<CatalogEntry[]>([]);
  searched = $state("");
  /** Option documentation by `scope:name`. */
  docs = $state.raw<Record<string, OptionDoc>>({});
  saving = $state(false);
  /** Search restrictions, remembered between visits. */
  free = $state(localStorage.getItem("nix-manager-search-free") === "1");
  working = $state(localStorage.getItem("nix-manager-search-working") !== "0");
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
      const results = await api<CatalogEntry[]>("software/search", { query, free: this.free, working: this.working });
      if (token === this.searchToken) {
        this.results = results;
        this.searched = query;
      }
    } catch (error) {
      app.fail(error, "Search failed");
    }
  }

  setFilter(name: "free" | "working", value: boolean) {
    this[name] = value;
    localStorage.setItem(`nix-manager-search-${name}`, value ? "1" : "0");
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

  /** Installing `entry` in `scope` would be refused as non-free software, even with the pending edits. */
  refusesUnfree(scope: Target, entry: CatalogEntry) {
    if (entry.f || same(this.value(scope, UNFREE).value, true)) return false;
    return !acceptsUnfree(this.inventory?.[scope].unfree, entry.n);
  }

  /**
   * Edits that allow non-free software in a scope, written where the Non-free
   * software feature would write them. Undefined when a new file would have
   * nothing to import it.
   */
  allowUnfree(scope: Target): Change[] | undefined {
    const experience = experiences.find((item) => item.scope === scope && UNFREE in item.enable)!;
    const label = experience.title;
    const target = this.definedIn(scope, UNFREE) ?? this.featureFile(scope, UNFREE, experience.id);
    if (target !== NEW_FILE) return [{ kind: "set-option", scope, file: target, option: UNFREE, value: true, label }];
    const near = this.moduleFiles(scope, UNFREE).find((module) => module.active !== false)?.file;
    const file = this.suggestFile(scope, experience.id, near);
    const importer = this.importers(scope, file)[0];
    if (!importer) return undefined;
    return [
      { kind: "create-module", scope, file, importer, label },
      { kind: "set-option", scope, file, option: UNFREE, value: true, label },
    ];
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

  /** Module files that could hold an option, best first; see `rankModules`. */
  moduleFiles(scope: Target, option: string) {
    return rankModules(this.inventory?.[scope].modules ?? [], scope, option, remembered(scope));
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

  /** The file a curated experience writes to, or `NEW_FILE` for one beside similar modules. */
  featureFile(scope: Target, option: string, name: string) {
    return featureModule(this.moduleFiles(scope, option), name) ?? NEW_FILE;
  }

  /** A free path for a new module named `name`, next to the file new settings would go to. */
  suggestFile(scope: Target, name: string, near?: string) {
    return freePath(this.known(scope), name, near, scope === "home" ? "modules/home" : "modules/nixos");
  }

  newFileProblem(scope: Target, file: string) {
    return newFileProblem(this.known(scope), file);
  }

  importers(scope: Target, file: string) {
    return rankImporters(this.inventory?.[scope].importers ?? [], file, this.inventory?.[scope].imports);
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

  /** Adds edits to the draft; setting an option back to its current value only drops the pending edit. */
  stage(next: Change[]) {
    return this.save(
      mergeChanges(this.changes, next, (change) => {
        if (change.kind !== "set-option") return false;
        const current = this.inventory?.[change.scope].options[change.option];
        return Boolean(current && !current.error && same(current.value, change.value));
      }),
    );
  }

  unstage(index: number) {
    return this.save(dropChange(this.changes, index));
  }

  clear() {
    return this.save([]);
  }

  /** The scope a review must build to include every pending edit. */
  scope = $derived<Scope>(changesScope(this.changes));

  prepare() {
    return app.prepare(this.scope, { edits: true });
  }
}

export const software = new SoftwareState();

export { describeChange, origin, showValue } from "../../src/changes";
