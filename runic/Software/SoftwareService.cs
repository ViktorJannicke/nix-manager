using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Execution;
using Runic.CommandLine.Processes;

namespace NixManager.Software;

/// <summary>What a host installs, for both of its configurations (<c>Store.inventory</c>).</summary>
public sealed record SoftwareInventory(ScopeInventory Home, ScopeInventory System);

/// <summary>
/// The Software page's server side (<c>Store</c> in <c>src/store.ts</c>): the pending draft, what each host
/// installs, the package catalog and option documentation. Evaluations are cached by the checkout state they read
/// (the fingerprint and lock hash the caller observed); the dashboard (W300-030) and the daemon (W300-031) share one.
/// </summary>
public interface ISoftwareService
{
    Config Config { get; }

    /// <summary>Raised when the draft or the catalog changed, so views can take a new snapshot (<c>changed</c>).</summary>
    event Action? Changed;

    /// <summary>The draft rendered against the checkout, cached by its edits and the checkout fingerprint.</summary>
    Task<RenderedDraft> DraftAsync(string fingerprint = "");

    /// <summary>Replaces the draft with the dashboard's edits (<c>setDraft</c>).</summary>
    /// <exception cref="ManagerException">The edits do not decode.</exception>
    Task<RenderedDraft> SetDraftAsync(JsonNode? changes);

    /// <summary>What a host's home and system configurations install, cached by checkout fingerprint (<c>inventory</c>).</summary>
    Task<SoftwareInventory> InventoryAsync(string host, string fingerprint);

    CatalogStatus CatalogStatus(string? lockHash);

    /// <summary>Builds the catalog for the current lock file in the background unless it exists already (<c>buildCatalog</c>).</summary>
    void BuildCatalog(string lockHash, bool rebuild = false);

    /// <summary>Catalog entries matching a query, best first; empty until the catalog of this lock file is loaded.</summary>
    IReadOnlyList<CatalogEntry> Search(string? lockHash, string query, CatalogFilter? filter = null);

    /// <summary>Catalog entries by attribute, for details of installed packages (<c>lookup</c>).</summary>
    IReadOnlyDictionary<string, CatalogEntry> Lookup(string? lockHash, IReadOnlyList<string> attrs);

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
/// (<c>curatedOptions</c>); the experiences are not ported yet, so the default reports none.
/// </param>
public sealed partial class SoftwareService(Config config, ICommandRunner runner, IProcessRunner? processes = null,
    Func<string, IReadOnlyList<string>>? curatedOptions = null) : ISoftwareService
{
    private readonly Memo<SoftwareInventory> _inventories = new();
    private readonly Memo<List<OptionDoc>> _docs = new();
    private readonly Lock _lock = new();
    private (string Key, Task<RenderedDraft> Draft)? _rendered;
    private (string Key, List<CatalogEntry> Entries, string BuiltAt)? _catalog;
    private (string Key, Task Task)? _building;
    private (string Key, string Error)? _failure;
    private Task? _loading;

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

    public Task<RenderedDraft> SetDraftAsync(JsonNode? changes)
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
        if (options.Count > 200 || options.Any(option => !OptionName().IsMatch(option))) throw new ManagerException("Invalid option list.");
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
                    lock (_lock) _failure = (lockHash, $"Read package catalog: {error.Message}");
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
                        _failure = (lockHash, error.Message);
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

    public IReadOnlyList<CatalogEntry> Search(string? lockHash, string query, CatalogFilter? filter = null) =>
        Entries(lockHash) is { } entries ? Catalog.Search(entries, query, 60, filter) : [];

    public IReadOnlyDictionary<string, CatalogEntry> Lookup(string? lockHash, IReadOnlyList<string> attrs)
    {
        var result = new Dictionary<string, CatalogEntry>();
        if (Entries(lockHash) is not { } entries) return result;
        var wanted = attrs.Take(500).ToHashSet();
        foreach (var entry in entries)
            if (wanted.Contains(entry.A)) result[entry.A] = entry;
        return result;
    }
}
