using System.Globalization;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Updates;
using Runic.CommandLine.Processes;

namespace NixManager.Software;

/// <summary>What a host installs, for both of its configurations (<c>Store.inventory</c>).</summary>
public sealed record SoftwareInventory(ScopeInventory Home, ScopeInventory System);

/// <summary>
/// The Software page's server side: the pending draft, what each host
/// installs, the package catalog with Flathub's app details, and option documentation. Evaluations are cached by the
/// checkout state they read (the fingerprint and lock hash the caller observed); the dashboard (W300-030) and the
/// daemon (W300-031) share one.
/// </summary>
public interface ISoftwareService
{
    Config Config { get; }

    /// <summary>Raised when the draft, the catalog or the app details changed, so views can take a new snapshot (<c>changed</c>).</summary>
    event Action? Changed;

    /// <summary>The draft rendered against the checkout, cached by its edits and the checkout fingerprint.</summary>
    Task<RenderedDraft> DraftAsync(string fingerprint = "");

    /// <summary>Replaces the draft with the dashboard's edits (<c>setDraft</c>).</summary>
    /// <exception cref="ManagerException">The edits are not valid (<see cref="Change.DecodeList(IReadOnlyList{Change})"/>).</exception>
    Task<RenderedDraft> SetDraftAsync(IReadOnlyList<Change> changes);

    /// <summary>What a host's home and system configurations install, cached by checkout fingerprint (<c>inventory</c>).</summary>
    Task<SoftwareInventory> InventoryAsync(string host, string fingerprint);

    CatalogStatus CatalogStatus(string? lockHash);

    /// <summary>Builds the catalog for the current lock file in the background unless it exists already (<c>buildCatalog</c>).</summary>
    void BuildCatalog(string lockHash, bool rebuild = false);

    /// <summary>
    /// Catalog entries matching a query, best first, with their app details; empty until the catalog of this lock
    /// file is loaded (<c>search</c>).
    /// </summary>
    IReadOnlyList<AppEntry> Search(string? lockHash, string query, CatalogFilter? filter = null);

    /// <summary>Catalog entries by attribute with their app details, for details of installed packages (<c>lookup</c>).</summary>
    IReadOnlyDictionary<string, AppEntry> Lookup(string? lockHash, IReadOnlyList<string> attrs);

    /// <summary>Whether app details are on: the <c>appDetails</c> preference, on unless turned off.</summary>
    bool AppDetails { get; }

    /// <summary>The state of Flathub's catalogue; <c>off</c> unless <paramref name="enabled"/> (<c>appsStatus</c>).</summary>
    AppsStatus AppsStatus(bool enabled);

    /// <summary>
    /// Downloads Flathub's catalogue in the background when there is none or it is a week old, or with
    /// <paramref name="force"/>; after a failure it waits an hour unless forced (<c>fetchApps</c>).
    /// </summary>
    void FetchApps(bool force = false);

    /// <summary>Every package of the catalog that Flathub describes as an app, in catalog order (<c>appList</c>).</summary>
    IReadOnlyList<AppEntry> AppList(string? lockHash);

    /// <summary>An app's icon by AppStream id, downloaded once and kept; null when the catalogue has none (<c>icon</c>).</summary>
    Task<byte[]?> IconAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Documented options matching a query, best matches first (<c>searchOptions</c>).</summary>
    Task<IReadOnlyList<OptionDoc>> SearchOptionsAsync(string host, string scope, string query, string fingerprint);

    /// <summary>Documentation of exactly these options; unknown names are left out (<c>describe</c>).</summary>
    Task<IReadOnlyList<OptionDoc>> DescribeAsync(string host, string scope, IReadOnlyList<string> names, string fingerprint);

    /// <summary>Current values and definition files of up to 200 options (<c>values</c>).</summary>
    Task<IReadOnlyDictionary<string, OptionValue>> ValuesAsync(string host, string scope, IReadOnlyList<string> options);
}

/// <summary>The software store of one checkout (<c>Store</c>).</summary>
/// <param name="curatedOptions">
/// Options of the curated experiences and the Computer page for a scope, so the inventory reports their state
/// (<c>curatedOptions</c>); <see cref="CuratedOptions.For"/> in the application, none by default.
/// </param>
/// <param name="network">Where Flathub's catalogue and icons come from; tests answer it.</param>
public sealed partial class SoftwareService(Config config, ICommandRunner runner, IProcessRunner? processes = null,
    Func<string, IReadOnlyList<string>>? curatedOptions = null, INetwork? network = null, TimeProvider? clock = null) : ISoftwareService
{
    /// <summary>Flathub's catalogue is downloaded again after a week (<c>fetchApps</c>).</summary>
    public static readonly TimeSpan AppsMaxAge = TimeSpan.FromDays(7);

    /// <summary>A failed download is not tried again on its own for an hour.</summary>
    public static readonly TimeSpan AppsRetry = TimeSpan.FromHours(1);

    private readonly INetwork _network = network ?? new HttpNetwork();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Memo<SoftwareInventory> _inventories = new();
    private readonly Memo<List<OptionDoc>> _docs = new();
    private readonly Lock _lock = new();
    private (string Key, Task<RenderedDraft> Draft)? _rendered;
    private (string Key, List<CatalogEntry> Entries, string BuiltAt)? _catalog;
    private (string Key, Task Task)? _building;
    private (string Key, DisplayText Error)? _failure;
    private Task? _loading;
    private (bool Read, AppStream? Value) _appStream;
    private Task? _fetchingApps;
    private (DisplayText Error, DateTimeOffset At)? _appsFailure;
    private (string Key, Dictionary<string, AppInfo> Apps)? _matched;

    public Config Config => config;
    public event Action? Changed;

    private Task<CommandResult> Run(Command command) => runner.RunAsync(command, _ => { }, CancellationToken.None);

    private string HostName(string host) => host is { Length: > 0 } ? host : config.HostKey;

    private void OnChanged() => Changed?.Invoke();

    public Task<RenderedDraft> DraftAsync(string fingerprint = "")
    {
        var changes = Drafts.Read(config);
        var key = $"{Change.Hash(changes)}\0{fingerprint}";
        lock (_lock)
        {
            if (_rendered is not { } rendered || rendered.Key != key)
                _rendered = rendered = (key, Task.Run(() => Drafts.Public(config.Flake, changes)));
            return rendered.Draft;
        }
    }

    public Task<RenderedDraft> SetDraftAsync(IReadOnlyList<Change> changes)
    {
        var decoded = Change.DecodeList(changes);
        Drafts.Write(config, decoded);
        lock (_lock) _rendered = null;
        OnChanged();
        return DraftAsync();
    }

    public Task<SoftwareInventory> InventoryAsync(string host, string fingerprint)
    {
        var name = HostName(host);
        return _inventories.Get(name, fingerprint, async () =>
        {
            var home = Inventory.ReadAsync(config, name, Targets.Home, Run, curatedOptions?.Invoke(Targets.Home) ?? []);
            var system = Inventory.ReadAsync(config, name, Targets.System, Run, curatedOptions?.Invoke(Targets.System) ?? []);
            await Task.WhenAll(home, system).ConfigureAwait(false);
            return new SoftwareInventory(await home.ConfigureAwait(false), await system.ConfigureAwait(false));
        });
    }

    private Task<List<OptionDoc>> OptionDocsAsync(string host, string scope, string fingerprint)
    {
        var name = HostName(host);
        return _docs.Get($"{name}\0{scope}", fingerprint, () => OptionDocs.ReadAsync(config, name, scope, Run));
    }

    public async Task<IReadOnlyList<OptionDoc>> SearchOptionsAsync(string host, string scope, string query, string fingerprint) =>
        OptionDocs.Search(await OptionDocsAsync(host, scope, fingerprint).ConfigureAwait(false), query);

    public async Task<IReadOnlyList<OptionDoc>> DescribeAsync(string host, string scope, IReadOnlyList<string> names, string fingerprint)
    {
        var wanted = names.Take(500).ToHashSet();
        return (await OptionDocsAsync(host, scope, fingerprint).ConfigureAwait(false)).Where(doc => wanted.Contains(doc.N)).ToList();
    }

    [GeneratedRegex("^[A-Za-z0-9_'.-]+$")]
    private static partial Regex OptionName();

    public async Task<IReadOnlyDictionary<string, OptionValue>> ValuesAsync(string host, string scope, IReadOnlyList<string> options)
    {
        if (options.Count > 200 || options.Any(option => !OptionName().IsMatch(option))) throw new ManagerException(DisplayText.Of("stored_invalid_option_list"));
        var source = await Evaluation.FlakeSourceAsync(config.Flake, Run).ConfigureAwait(false);
        var state = await Evaluation.EvaluateStateAsync(source, Evaluation.ConfigurationAttr(config, host, scope), scope,
            new StateRequest { Options = options }, Run).ConfigureAwait(false);
        return state.Options;
    }

    public CatalogStatus CatalogStatus(string? lockHash)
    {
        if (string.IsNullOrEmpty(lockHash)) return new CatalogStatus { State = "missing" };
        lock (_lock)
        {
            if (_building?.Key == lockHash) return new CatalogStatus { State = "building" };
            if (_catalog is { } catalog && catalog.Key == lockHash)
                return new CatalogStatus { State = "ready", Count = catalog.Entries.Count, BuiltAt = catalog.BuiltAt };
            if (_failure is { } failure && failure.Key == lockHash) return new CatalogStatus { State = "failed", Error = failure.Error };
        }
        if (File.Exists(Catalog.PathFor(config, lockHash)))
        {
            _ = Load(lockHash);
            return new CatalogStatus { State = "building" };
        }
        return new CatalogStatus { State = "missing" };
    }

    private Task Load(string lockHash)
    {
        lock (_lock)
            return _loading ??= Task.Run(() =>
            {
                try
                {
                    var path = Catalog.PathFor(config, lockHash);
                    var entries = Catalog.Read(path);
                    var builtAt = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                    lock (_lock) _catalog = (lockHash, entries, builtAt);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    lock (_lock) _failure = (lockHash, DisplayText.Of("stored_step_failed_because", ("step", DisplayText.Of("stored_read_package_catalog")), ("reason", DisplayText.From(error))));
                }
                finally
                {
                    lock (_lock) _loading = null;
                    OnChanged();
                }
            });
    }

    public void BuildCatalog(string lockHash, bool rebuild = false)
    {
        lock (_lock)
        {
            if (_building is not null) return;
            if (!rebuild && (_catalog?.Key == lockHash || File.Exists(Catalog.PathFor(config, lockHash)))) return;
            _failure = null;
            _building = (lockHash, Task.Run(async () =>
            {
                try
                {
                    await Catalog.BuildAsync(config, config.HostKey, Catalog.PathFor(config, lockHash), processes).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _catalog = null;
                        _building = null;
                    }
                    await Load(lockHash).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    lock (_lock)
                    {
                        _building = null;
                        _failure = (lockHash, DisplayText.From(error));
                    }
                }
                finally
                {
                    OnChanged();
                }
            }));
        }
        OnChanged();
    }

    private List<CatalogEntry>? Entries(string? lockHash)
    {
        lock (_lock) return !string.IsNullOrEmpty(lockHash) && _catalog is { } catalog && catalog.Key == lockHash ? catalog.Entries : null;
    }

    public IReadOnlyList<AppEntry> Search(string? lockHash, string query, CatalogFilter? filter = null)
    {
        if (Entries(lockHash) is not { } entries) return [];
        var apps = MatchedApps(lockHash!, entries);
        return Catalog.Search(entries, query, 60, filter, apps).Select(entry => WithApp(entry, apps)).ToList();
    }

    public IReadOnlyDictionary<string, AppEntry> Lookup(string? lockHash, IReadOnlyList<string> attrs)
    {
        var result = new Dictionary<string, AppEntry>();
        if (Entries(lockHash) is not { } entries) return result;
        var apps = MatchedApps(lockHash!, entries);
        var wanted = attrs.Take(500).ToHashSet();
        foreach (var entry in entries)
            if (wanted.Contains(entry.A)) result[entry.A] = WithApp(entry, apps);
        return result;
    }

    private static AppEntry WithApp(CatalogEntry entry, IReadOnlyDictionary<string, AppInfo> apps) => new(entry, apps.GetValueOrDefault(entry.A));

    // --- Flathub's app details -------------------------------------------------------------------------------

    public bool AppDetails => Apps.Enabled(config);

    /// <summary>The saved Flathub catalogue, read once (<c>stream</c>).</summary>
    private AppStream? Stream()
    {
        lock (_lock)
        {
            if (!_appStream.Read) _appStream = (true, Apps.Read(config));
            return _appStream.Value;
        }
    }

    public AppsStatus AppsStatus(bool enabled)
    {
        if (!enabled) return new AppsStatus { State = "off" };
        lock (_lock)
            if (_fetchingApps is not null) return new AppsStatus { State = "fetching" };
        var stream = Stream();
        lock (_lock)
        {
            if (stream is not null) return new AppsStatus { State = "ready", FetchedAt = stream.FetchedAt, Error = _appsFailure?.Error };
            return _appsFailure is { } failure ? new AppsStatus { State = "failed", Error = failure.Error } : new AppsStatus { State = "missing" };
        }
    }

    public void FetchApps(bool force = false)
    {
        var stream = Stream();
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (_fetchingApps is not null) return;
            if (!force && stream is not null && Timestamps.Parse(stream.FetchedAt) is { } fetchedAt && now - fetchedAt < AppsMaxAge) return;
            if (!force && _appsFailure is { } failure && now - failure.At < AppsRetry) return;
            _fetchingApps = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    var value = await Apps.FetchAsync(config, _network, _clock, timeout.Token).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _appStream = (true, value);
                        _appsFailure = null;
                    }
                }
                catch (Exception error)
                {
                    lock (_lock) _appsFailure = (DisplayText.Of("stored_app_details_could_not_be_downloaded_from", ("error", DisplayText.From(error))), _clock.GetUtcNow());
                }
                finally
                {
                    lock (_lock) _fetchingApps = null;
                    OnChanged();
                }
            });
        }
        OnChanged();
    }

    /// <summary>Apps of the current catalog by attribute, matched once per catalog and catalogue (<c>apps</c>).</summary>
    private Dictionary<string, AppInfo> MatchedApps(string lockHash, List<CatalogEntry> entries)
    {
        if (Stream() is not { } stream) return [];
        var key = $"{lockHash}\0{stream.FetchedAt}";
        lock (_lock)
            if (_matched is { } matched && matched.Key == key) return matched.Apps;
        // Matching reads the whole catalog; a concurrent caller may match the same pair once more.
        var apps = Apps.Match(stream.Apps, entries);
        lock (_lock) _matched = (key, apps);
        return apps;
    }

    public IReadOnlyList<AppEntry> AppList(string? lockHash)
    {
        if (Entries(lockHash) is not { } entries) return [];
        var apps = MatchedApps(lockHash!, entries);
        return entries.Where(entry => apps.ContainsKey(entry.A)).Select(entry => WithApp(entry, apps)).ToList();
    }

    public async Task<byte[]?> IconAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Apps.ValidAppId(id) || Stream()?.Apps.FirstOrDefault(item => item.Id == id) is not { } app) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await Apps.IconAsync(config, app, _network, timeout.Token).ConfigureAwait(false);
    }
}
