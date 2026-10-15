// The Software and Computer pages' state over the Window's SoftwareViewModel (ui/lib/software.svelte.ts): what the
// selected host installs, the package catalog and its search, option documentation, and staging edits in the
// draft. The draft itself is the Window's (`app.draft`), which the navigation and the Overview show without
// evaluating the inventory; a draft this page just saved is used until the Window's has caught up.

import type { AppEntry, InstalledPackage, RenderedDraft, SoftwareInventory } from "../generated/types.js";
import { app } from "./app.svelte";
import {
  acceptsUnfree,
  changesScope,
  dropChange,
  featureModule,
  freePath,
  mergeChanges,
  newFileProblem,
  oneStepProblem,
  rankImporters,
  rankModules,
  type Change,
  type Target,
} from "./changes";
import { experiences, type Experience } from "./curated";
import { m, stored } from "./i18n";

export type { Change, Target };
export type Inventory = SoftwareInventory;
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);
const remembered = (scope: Target) => localStorage.getItem(`nix-manager-file-${scope}`) || undefined;

const UNFREE = "nixpkgs.config.allowUnfree";

/** Select value standing for a module file the edits create. */
export const NEW_FILE = "\0new";

/** Files inside the flake are relative; module files from elsewhere are store paths. */
export const local = (file: string) => !file.startsWith("/");

/** A failure the Software page's commands declare, or an unexpected one. */
const failure = (outcome: { ok: boolean; failure?: { message: string } } | undefined) =>
  outcome && !outcome.ok ? new Error(outcome.failure?.message) : undefined;

class SoftwareState {
  saving = $state(false);
  /** Search restrictions, remembered between visits. */
  free = $state(localStorage.getItem("nix-manager-search-free") === "1");
  working = $state(localStorage.getItem("nix-manager-search-working") !== "0");
  /** The draft the last save returned, until the Window's draft shows the same edits. */
  private saved = $state.raw<RenderedDraft>();
  private key = "";
  private lock = "";

  state = $derived(app.softwareView?.state);
  private client = $derived(app.softwareView?.client);
  inventory = $derived(this.state?.inventory ?? undefined);
  loading = $derived(Boolean(this.state?.loading));
  error = $derived(this.state?.error ?? undefined);
  catalog = $derived(this.state?.catalog);
  appStatus = $derived(this.state?.appsStatus);
  results = $derived(this.state?.results ?? []);
  searched = $derived(this.state?.searched ?? "");
  /** Packages Flathub describes as apps, for browsing by category. */
  apps = $derived(app.appListView?.state?.entries ?? []);
  docs = $derived(this.state?.docs ?? {});
  draft = $derived(this.saved && this.saved.hash !== app.draft?.hash ? this.saved : app.draft);
  changes = $derived(this.draft?.changes ?? []);

  constructor() {
    $effect.root(() => {
      $effect(() => {
        if (this.saved && app.draft?.hash === this.saved.hash) this.saved = undefined;
      });
    });
  }

  /**
   * Passes the observed checkout to the ViewModel and reads what the selected host installs; the backend caches
   * the inventory by the checkout's state, so a repeated call for the same one is cheap.
   */
  async load(force = false) {
    const client = this.client;
    const repository = app.system?.repository;
    if (!client || !repository) return;
    if (this.lock !== repository.lockHash) {
      this.lock = repository.lockHash;
      await client.setLockHash(repository.lockHash);
    }
    // The app details preference is read when the ViewModel refreshes, so changing it reads the catalogue again.
    const key = `${app.host}\0${repository.fingerprint}\0${app.dashboard?.appDetails}`;
    if (!force && key === this.key) return;
    this.key = key;
    try {
      await client.setHost(app.host);
      await client.setFingerprint(repository.fingerprint);
      await client.refresh();
    } catch (error) {
      this.key = "";
      app.fail(error, m.software_could_not_read({ host: app.host }));
    }
  }

  /** Searches the catalog; the ViewModel answers in `results` with the query they answer in `searched`. */
  async search(query: string) {
    try {
      await this.client?.setQuery(query);
    } catch (error) {
      app.fail(error, m.software_search_failed());
    }
  }

  /** Lists the apps; starts downloading Flathub's catalogue when there is none or it is old. */
  async loadApps(refresh = false) {
    try {
      await this.client?.loadApps(refresh);
    } catch (error) {
      app.fail(error, m.software_could_not_read_apps());
    }
  }

  async setFilter(name: "free" | "working", value: boolean) {
    this[name] = value;
    localStorage.setItem(`nix-manager-search-${name}`, value ? "1" : "0");
    await this.applyFilters();
  }

  /** Passes the remembered filters to the ViewModel. */
  async applyFilters() {
    const state = this.state;
    if (!state) return;
    if (state.freeOnly !== this.free) await this.client?.setFreeOnly(this.free);
    if (state.workingOnly !== this.working) await this.client?.setWorkingOnly(this.working);
  }

  async buildCatalog(rebuild = false) {
    try {
      const error = failure(await this.client?.buildCatalog(rebuild));
      if (error) throw error;
    } catch (error) {
      app.fail(error, m.software_could_not_build_index());
    }
  }

  /** Loads documentation for options not seen yet. */
  async describe(scope: Target, names: readonly string[]) {
    const missing = names.filter((name) => !this.docs[`${scope}:${name}`]);
    if (!missing.length) return;
    try {
      const error = failure(await this.client?.describe({ scope, options: missing }));
      if (error) throw error;
    } catch (error) {
      app.fail(error, m.software_could_not_read_docs());
    }
  }

  doc(scope: Target, name: string) {
    return this.docs[`${scope}:${name}`];
  }

  /** Catalog entries by attribute, with their app details. */
  async lookup(attrs: readonly string[]): Promise<Readonly<Record<string, AppEntry>> | undefined> {
    try {
      const state = await this.client?.lookupPackages(attrs);
      return state?.lookup && same(state.lookup.attrs, attrs.slice(0, 500)) ? state.lookup.entries : undefined;
    } catch (error) {
      app.fail(error, m.software_could_not_read_apps());
      return undefined;
    }
  }

  // --- Installed state ------------------------------------------------------

  /** The installed package an entry of the catalog refers to, in either scope. */
  installed(entry: AppEntry): (InstalledPackage & { scope: Target }) | undefined {
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
  refusesUnfree(scope: Target, entry: AppEntry) {
    return !entry.f && this.refusesUnfreeName(scope, entry.n);
  }

  /** A non-free package called `name` would be refused in `scope`, even with the pending edits. */
  refusesUnfreeName(scope: Target, name: string | null) {
    if (same(this.value(scope, UNFREE).value, true)) return false;
    return !acceptsUnfree(this.inventory?.[scope].unfree, name);
  }

  /**
   * Edits that allow non-free software in a scope, written where the Non-free software feature would write
   * them. Undefined when a new file would have nothing to import it.
   */
  allowUnfree(scope: Target): Change[] | undefined {
    const experience = experiences.find((item) => item.scope === scope && UNFREE in item.enable)!;
    const label = experience.title;
    const target = this.editableIn(scope, UNFREE) ?? this.featureFile(scope, UNFREE, experience.id);
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

  /**
   * Edits that turn on `features` and install `entries` for the current user, each where it would go on its
   * own. What is on or installed already is left alone; what would need a new file with nothing to import it is
   * named in `skipped` instead.
   */
  bundleChanges(features: readonly Experience[], entries: readonly AppEntry[]) {
    const changes: Change[] = [];
    const skipped: string[] = [];
    const allowed = new Map<Target, boolean>();
    // Non-free software is allowed once per scope, like adding one non-free app.
    const allow = (scope: Target) => {
      if (!allowed.has(scope)) {
        const edits = this.allowUnfree(scope);
        allowed.set(scope, Boolean(edits));
        changes.push(...(edits ?? []));
      }
      return allowed.get(scope)!;
    };
    for (const experience of features) {
      if (this.enabled(experience) || !this.available(experience)) continue;
      const { scope, id, title: label } = experience;
      const first = Object.keys(experience.enable)[0];
      let file = this.editableIn(scope, first) ?? this.featureFile(scope, first, id);
      const creating = file === NEW_FILE;
      const edits: Change[] = [];
      if (creating) {
        file = this.suggestFile(scope, id, this.moduleFiles(scope, first).find((module) => module.active !== false)?.file);
        const importer = this.importers(scope, file)[0];
        if (!importer) {
          skipped.push(stored(label));
          continue;
        }
        edits.push({ kind: "create-module", scope, file, importer, label });
      }
      if (experience.unfree?.some((name) => this.refusesUnfreeName(scope, name)) && !allow(scope)) {
        skipped.push(stored(label));
        continue;
      }
      for (const [option, value] of Object.entries({ ...experience.enable, ...experience.alsoSet }))
        edits.push({ kind: "set-option", scope, file: this.editableIn(scope, option) ?? file, option, value, label });
      const packages = creating ? file : this.packageFiles(scope)[0]?.file;
      for (const attr of experience.packages ?? [])
        if (packages) edits.push({ kind: "add-package", scope, file: packages, attr: `pkgs.${attr}`, label });
      changes.push(...edits);
    }
    const list = this.packageFiles("home")[0]?.file;
    for (const entry of entries) {
      if (this.installed(entry) || this.pendingPackage(`pkgs.${entry.a}`)) continue;
      if (!list || (this.refusesUnfree("home", entry) && !allow("home"))) {
        skipped.push(entry.app?.name ?? entry.n ?? entry.a);
        continue;
      }
      changes.push({ kind: "add-package", scope: "home", file: list, attr: `pkgs.${entry.a}` });
    }
    return { changes, skipped };
  }

  pendingPackage(attr: string) {
    return this.changes.find((change) => "attr" in change && change.attr === attr);
  }

  /** Current value of an option, with a pending edit taking precedence. */
  value(scope: Target, option: string): { value: unknown; pending: boolean } {
    const pending = [...this.changes].reverse().find((change) => "option" in change && change.scope === scope && change.option === option);
    if (pending?.kind === "set-option") return { value: pending.value, pending: true };
    if (pending?.kind === "unset-option") return { value: undefined, pending: true };
    return { value: this.inventory?.[scope].options[option]?.value ?? undefined, pending: false };
  }

  available(experience: Experience) {
    const options = this.inventory?.[experience.scope].options;
    return Object.keys(experience.enable).every((option) => options?.[option]?.exists);
  }

  /**
   * Every option that turns the experience on has its value; a list needs only the wanted items. Options also
   * offered as settings may differ, unless only such options turn it on.
   */
  enabled(experience: Experience) {
    const entries = Object.entries(experience.enable);
    const core = entries.filter(([option]) => !experience.settings.some((setting) => setting.option === option));
    return (core.length ? core : entries).every(([option, wanted]) => {
      const { value } = this.value(experience.scope, option);
      return Array.isArray(wanted) && Array.isArray(value) ? wanted.every((item) => value.some((have) => same(have, item))) : same(value, wanted);
    });
  }

  /** The file that defines an option now, if it is a module Nix Manager can edit. */
  editableIn(scope: Target, option: string) {
    const file = this.definedIn(scope, option);
    return file && this.inventory?.[scope].modules.some((module) => module.file === file) ? file : undefined;
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
        Number(b.file === last) - Number(a.file === last) || Number(b.active) - Number(a.active) || b.count - a.count || a.file.localeCompare(b.file),
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

  /** Why `file` cannot be a new module, translated, if it cannot. */
  newFileProblem(scope: Target, file: string) {
    const problem = newFileProblem(this.known(scope), file);
    return problem && stored(problem);
  }

  importers(scope: Target, file: string) {
    return rankImporters(this.inventory?.[scope].importers ?? [], file, this.inventory?.[scope].imports);
  }

  remember(scope: Target, file: string) {
    localStorage.setItem(`nix-manager-file-${scope}`, file);
  }

  // --- Draft ----------------------------------------------------------------

  /** Replaces the draft; false when it could not be saved. */
  async save(changes: Change[]) {
    const client = this.client;
    if (!client) return false;
    this.saving = true;
    try {
      const outcome = await client.setDraft(changes);
      if (!outcome.ok) throw new Error(outcome.failure.message);
      this.saved = outcome.value.draft ?? undefined;
      return true;
    } catch (error) {
      app.fail(error, m.software_could_not_save());
      return false;
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
  scope = $derived(changesScope(this.changes));

  /**
   * Whether `changes` can be installed in one step from here: they qualify (see `oneStepProblem`), nothing else
   * is pending to be swept along, and the dashboard can apply to this computer now.
   */
  canInstallNow(changes: Change[]) {
    return !this.changes.length && !oneStepProblem(changes) && !app.foreignHost && !app.busy && app.connected;
  }

  /** Stages `changes`, then prepares and applies them in one step. */
  async installNow(changes: Change[], title: string, done: string) {
    if (await this.stage(changes)) await app.installNow(title, done);
  }
}

export const software = new SoftwareState();
