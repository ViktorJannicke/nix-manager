import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { Effect, Schema } from "effect";
import { ManagerError, type Config } from "./domain";
import type { AppsStatus, CatalogStatus } from "./contracts";
import { appIcon, fetchAppStream, matchApps, readAppStream, type AppEntry, type AppInfo, type AppStream } from "./apps";
import { computerOptions } from "./computer";
import { experiences } from "./curated";
import { Changes, changeHash, draftPublic, readDraft, writeDraft, type Change, type RenderedDraft } from "./draft";
import { runnerLayer } from "./process";
import {
  buildCatalog,
  catalogPath,
  inventory,
  Memo,
  optionDocs,
  searchCatalog,
  evaluateState,
  configurationAttr,
  type CatalogEntry,
  type CatalogFilter,
  type OptionDoc,
  type ScopeInventory,
  type Target,
} from "./software";
import { flakeSource } from "./packages";

const run = <A>(effect: Effect.Effect<A, ManagerError, import("./process").Runner>) =>
  Effect.runPromise(effect.pipe(Effect.provide(runnerLayer())));

/** Options of the curated experiences and the Computer page, so the inventory reports their state. */
const curatedOptions = (scope: Target) => [
  ...experiences
    .filter((experience) => experience.scope === scope)
    .flatMap((experience) => [
      ...Object.keys(experience.enable),
      ...experience.settings.map((setting) => setting.option),
    ]),
  ...(scope === "system" ? computerOptions : []),
];

/**
 * The software store's server side: the pending draft, what each host
 * installs, the package catalog and option documentation. Evaluations are
 * cached by the checkout state they read.
 */
export class Store {
  private inventories = new Memo<{ home: ScopeInventory; system: ScopeInventory }>();
  private docs = new Memo<OptionDoc[]>();
  private rendered?: { key: string; draft: Promise<RenderedDraft> };
  private catalog?: { key: string; entries: CatalogEntry[]; builtAt: string };
  private building?: { key: string; promise: Promise<void> };
  private failure?: { key: string; error: string };
  private loading?: Promise<void>;
  private appStream?: { value?: AppStream };
  private fetchingApps?: Promise<void>;
  private appsFailure?: { error: string; at: number };
  private matched?: { key: string; apps: Map<string, AppInfo> };

  constructor(
    private readonly config: Config,
    private readonly changed: () => void,
  ) {}

  draft(fingerprint = ""): Promise<RenderedDraft> {
    const changes = readDraft(this.config);
    const key = `${changeHash(changes)}\0${fingerprint}`;
    if (this.rendered?.key !== key)
      this.rendered = { key, draft: draftPublic(this.config.flake, changes) };
    return this.rendered.draft;
  }

  async setDraft(input: unknown) {
    const changes = Schema.decodeUnknownSync(Changes)(input) as Change[];
    writeDraft(this.config, changes);
    this.rendered = undefined;
    this.changed();
    return this.draft();
  }

  inventory(host: string, fingerprint: string) {
    const name = host || this.config.hostKey;
    return this.inventories.get(name, fingerprint, () =>
      run(
        Effect.all(
          {
            home: inventory(this.config, name, "home", curatedOptions("home")),
            system: inventory(this.config, name, "system", curatedOptions("system")),
          },
          { concurrency: 2 },
        ),
      ),
    );
  }

  optionDocs(host: string, scope: Target, fingerprint: string) {
    const name = host || this.config.hostKey;
    return this.docs.get(`${name}\0${scope}`, fingerprint, () => run(optionDocs(this.config, name, scope)));
  }

  /** Documented options matching a query, best matches first. */
  async searchOptions(host: string, scope: Target, query: string, fingerprint: string) {
    const docs = await this.optionDocs(host, scope, fingerprint);
    const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
    if (!terms.length) return [];
    const prefix = query.trim();
    return docs
      .flatMap((doc) => {
        const name = doc.n.toLowerCase();
        const text = `${name} ${doc.d?.toLowerCase() ?? ""}`;
        if (doc.r || !terms.every((term) => text.includes(term))) return [];
        const score =
          (doc.n.startsWith(`${prefix}.`) || doc.n === prefix ? 100 : 0) +
          terms.filter((term) => name.includes(term)).length * 20 -
          doc.n.split(".").length;
        return [{ doc, score }];
      })
      .sort((a, b) => b.score - a.score || a.doc.n.localeCompare(b.doc.n))
      .slice(0, 120)
      .map(({ doc }) => doc);
  }

  /** Documentation of exactly these options; unknown names are left out. */
  async describe(host: string, scope: Target, names: readonly string[], fingerprint: string) {
    const wanted = new Set(names.slice(0, 500));
    return (await this.optionDocs(host, scope, fingerprint)).filter((doc) => wanted.has(doc.n));
  }

  /** Current values and definition files of up to 200 options. */
  async values(host: string, scope: Target, options: readonly string[]) {
    if (options.length > 200 || options.some((option) => !/^[A-Za-z0-9_'.-]+$/.test(option)))
      throw new ManagerError({ message: "Invalid option list." });
    return run(
      Effect.gen(this, function* () {
        const source = yield* flakeSource(this.config.flake);
        const state = yield* evaluateState(source, configurationAttr(this.config, host, scope), scope, { options });
        return state.options;
      }),
    );
  }

  catalogStatus(lockHash: string | undefined): CatalogStatus {
    if (!lockHash) return { state: "missing" };
    if (this.building?.key === lockHash) return { state: "building" };
    if (this.catalog?.key === lockHash)
      return { state: "ready", count: this.catalog.entries.length, builtAt: this.catalog.builtAt };
    if (this.failure?.key === lockHash) return { state: "failed", error: this.failure.error };
    if (existsSync(catalogPath(this.config, lockHash))) {
      void this.load(lockHash);
      return { state: "building" };
    }
    return { state: "missing" };
  }

  private load(lockHash: string) {
    return (this.loading ??= (async () => {
      try {
        const path = catalogPath(this.config, lockHash);
        const entries = JSON.parse(await readFile(path, "utf8")) as CatalogEntry[];
        const { mtime } = await Bun.file(path).stat();
        this.catalog = { key: lockHash, entries, builtAt: mtime.toISOString() };
      } catch (error) {
        this.failure = { key: lockHash, error: `Read package catalog: ${error}` };
      } finally {
        this.loading = undefined;
        this.changed();
      }
    })());
  }

  /** Builds the catalog for the current lock file unless it exists already. */
  buildCatalog(lockHash: string, rebuild = false) {
    if (this.building) return;
    if (!rebuild && (this.catalog?.key === lockHash || existsSync(catalogPath(this.config, lockHash)))) return;
    this.failure = undefined;
    const promise = run(buildCatalog(this.config, this.config.hostKey, catalogPath(this.config, lockHash)))
      .then(
        () => {
          this.catalog = undefined;
          this.building = undefined;
          return this.load(lockHash);
        },
        (error) => {
          this.building = undefined;
          this.failure = { key: lockHash, error: error instanceof Error ? error.message : String(error) };
        },
      )
      .finally(() => this.changed());
    this.building = { key: lockHash, promise };
    this.changed();
  }

  search(lockHash: string | undefined, query: string, filter: CatalogFilter = {}): AppEntry[] {
    if (!lockHash || this.catalog?.key !== lockHash) return [];
    const apps = this.apps(lockHash);
    return searchCatalog(this.catalog.entries, query, 60, filter, apps).map((entry) => this.withApp(entry, apps));
  }

  /** Catalog entries by attribute, for details of installed packages. */
  lookup(lockHash: string | undefined, attrs: readonly string[]) {
    if (!lockHash || this.catalog?.key !== lockHash) return {};
    const wanted = new Set(attrs.slice(0, 500));
    const apps = this.apps(lockHash);
    return Object.fromEntries(
      this.catalog.entries.filter((entry) => wanted.has(entry.a)).map((entry) => [entry.a, this.withApp(entry, apps)]),
    );
  }

  private withApp(entry: CatalogEntry, apps: Map<string, AppInfo>): AppEntry {
    const app = apps.get(entry.a);
    return app ? { ...entry, app } : entry;
  }

  /** The saved Flathub catalogue, read once. */
  private stream() {
    this.appStream ??= { value: readAppStream(this.config) };
    return this.appStream.value;
  }

  appsStatus(enabled: boolean): AppsStatus {
    if (!enabled) return { state: "off" };
    if (this.fetchingApps) return { state: "fetching" };
    const stream = this.stream();
    if (stream) return { state: "ready", fetchedAt: stream.fetchedAt, error: this.appsFailure?.error };
    return this.appsFailure ? { state: "failed", error: this.appsFailure.error } : { state: "missing" };
  }

  /** Downloads Flathub's catalogue when there is none or it is a week old; a failure waits an hour. */
  fetchApps(force = false) {
    const stream = this.stream();
    if (this.fetchingApps) return;
    if (!force && stream && Date.now() - Date.parse(stream.fetchedAt) < 7 * 24 * 3600_000) return;
    if (!force && this.appsFailure && Date.now() - this.appsFailure.at < 3600_000) return;
    this.fetchingApps = fetchAppStream(this.config, AbortSignal.timeout(120_000))
      .then(
        (value) => {
          this.appStream = { value };
          this.appsFailure = undefined;
        },
        (error) => {
          this.appsFailure = { error: `App details could not be downloaded from Flathub. ${error instanceof Error ? error.message : error}`, at: Date.now() };
        },
      )
      .finally(() => {
        this.fetchingApps = undefined;
        this.changed();
      });
    this.changed();
  }

  /** Apps of the current catalog by attribute. */
  private apps(lockHash: string): Map<string, AppInfo> {
    const stream = this.stream();
    if (!stream || this.catalog?.key !== lockHash) return new Map();
    const key = `${lockHash}\0${stream.fetchedAt}`;
    if (this.matched?.key !== key) this.matched = { key, apps: matchApps(stream.apps, this.catalog.entries) };
    return this.matched.apps;
  }

  /** Every package Flathub describes as an app. */
  appList(lockHash: string | undefined): AppEntry[] {
    if (!lockHash || this.catalog?.key !== lockHash) return [];
    const apps = this.apps(lockHash);
    return this.catalog.entries.flatMap((entry) => (apps.has(entry.a) ? [this.withApp(entry, apps)] : []));
  }

  /** An app's icon by AppStream id, if the catalogue knows it. */
  icon(id: string) {
    const app = this.stream()?.apps.find((item) => item.id === id);
    return app ? appIcon(this.config, app, AbortSignal.timeout(30_000)) : Promise.resolve(undefined);
  }
}
