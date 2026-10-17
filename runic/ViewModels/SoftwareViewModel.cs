using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Operations;
using NixManager.Software;
using Runic.Application.Views;
using Runic.Navigation;

namespace NixManager.ViewModels;

/// <summary>Why a Software page command could not do what it was asked, in English as the backend stores it.</summary>
public sealed record SoftwareFailure(string Message);

/// <summary>Options of one configuration: those to describe or read the values of.</summary>
/// <param name="Scope"><c>home</c> or <c>system</c>; anything else is <c>home</c>, as in the Bun version.</param>
/// <param name="Options">Option paths, such as <c>programs.git.enable</c>.</param>
public sealed record OptionsRequest(string Scope, IReadOnlyList<string> Options);

/// <summary>A search of a configuration's documented options.</summary>
/// <param name="Scope"><c>home</c> or <c>system</c>.</param>
/// <param name="Query">Words that the option's name or description contain.</param>
public sealed record OptionQuery(string Scope, string Query);

/// <summary>
/// The Software and Computer pages of one checkout: the pending draft, what the selected host installs, the package
/// catalog with Flathub's app details and its search, option documentation and values, and commands to change the
/// draft. A view of <see cref="ISoftwareService"/>, which caches evaluations by the checkout state; the page passes
/// the fingerprint and lock hash it observed (W300-030 binds it).
/// </summary>
/// <remarks>
/// Questions with parameters, such as <see cref="LookupPackagesCommand"/>, answer with their command's result, which the
/// page reads from the operation (<c>startLookupPackages(attrs).outcome()</c>). Failures that the page reports are declared as
/// <see cref="SoftwareFailure"/>. The app list, the large part, is a child View (<see cref="Apps"/>) with snapshots of its
/// own. App icons are files beside the page (<c>Dashboard/AppIconResources.cs</c>).
/// </remarks>
public sealed partial class SoftwareViewModel : ObservableObject, IDisposable
{
    /// <summary>Documentation kept for the page, by <c>scope:name</c>; older entries go first beyond this.</summary>
    public const int MaxDocs = 2000;

    private readonly ISoftwareService _service;
    private readonly IRunicModelContext _context;
    private readonly List<string> _docOrder = [];
    private string? _appsKey;

    /// <param name="hardware">This computer's hardware; <see cref="Operations.Hardware.Current"/> unless a test says otherwise.</param>
    /// <param name="user">The user running the dashboard; <see cref="Environment.UserName"/> by default.</param>
    public SoftwareViewModel(ISoftwareService service, IRunicModelContext context, HardwareFeatures? hardware = null, string? user = null)
    {
        _service = service;
        _context = context;
        Hardware = hardware ?? Operations.Hardware.Current;
        User = user ?? Environment.UserName;
        Apps = new AppListViewModel();
        AppsStatus = service.AppsStatus(service.AppDetails);
        _service.Changed += OnChanged;
    }

    /// <summary>The user running the dashboard, as the Computer page names them (<c>config.user</c>).</summary>
    public string User { get; }

    /// <summary>This computer's hardware, read once per process, for the suggestions that need it (<c>config.hardware</c>).</summary>
    public HardwareFeatures Hardware { get; }

    /// <summary>Every package Flathub describes as an app, once <c>loadApps</c> asked for them.</summary>
    public AppListViewModel Apps { get; }

    /// <summary>The host whose configurations are shown; empty for this computer's.</summary>
    [ObservableProperty]
    public partial string Host { get; set; } = "";

    /// <summary>The checkout state the inventory and the draft were read from.</summary>
    [ObservableProperty]
    public partial string Fingerprint { get; set; } = "";

    /// <summary>Hash of the checkout's flake.lock, which selects the package catalog.</summary>
    [ObservableProperty]
    public partial string? LockHash { get; set; }

    [ObservableProperty]
    public partial RenderedDraft? Draft { get; private set; }

    /// <summary>What the host's home and system configurations install, with the options the pages show.</summary>
    [ObservableProperty]
    public partial SoftwareInventory? Inventory { get; private set; }

    /// <summary>The inventory is being read.</summary>
    [ObservableProperty]
    public partial bool Loading { get; private set; }

    [ObservableProperty]
    public partial CatalogStatus Catalog { get; private set; } = new() { State = "missing" };

    /// <summary>Flathub's app catalogue: <c>off</c> when the <c>appDetails</c> preference turned it off.</summary>
    [ObservableProperty]
    public partial AppsStatus AppsStatus { get; private set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool FreeOnly { get; set; }

    [ObservableProperty]
    public partial bool WorkingOnly { get; set; }

    /// <summary>The best 60 catalog entries for <see cref="Searched"/>, with their app details.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AppEntry> Results { get; private set; } = [];

    /// <summary>The query <see cref="Results"/> answer, so the page can tell a finished search from a pending one.</summary>
    [ObservableProperty]
    public partial string Searched { get; private set; } = "";

    /// <summary>Option documentation that <see cref="DescribeCommand"/> loaded, by <c>scope:name</c>, for the selected host.</summary>
    [ObservableProperty]
    public partial IReadOnlyDictionary<string, OptionDoc> Docs { get; private set; } = new Dictionary<string, OptionDoc>();

    /// <summary>Why the last read of the draft or the inventory failed.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnQueryChanged(string value) => Search();

    partial void OnFreeOnlyChanged(bool value) => Search();

    partial void OnWorkingOnlyChanged(bool value) => Search();

    partial void OnLockHashChanged(string? value) => ReadCatalog();

    partial void OnHostChanged(string value)
    {
        // Option documentation belongs to a host's configurations.
        _docOrder.Clear();
        Docs = new Dictionary<string, OptionDoc>();
    }

    private static string ScopeOf(string scope) => scope == Targets.System ? Targets.System : Targets.Home;

    private static RunicFailureException Failure(Exception error) => new(new SoftwareFailure(error.Message));

    /// <summary>Reads the draft, the catalog and app status and the host's inventory for the current checkout state.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        Loading = true;
        try
        {
            Error = null;
            ReadCatalog();
            Draft = await _service.DraftAsync(Fingerprint);
            Inventory = await _service.InventoryAsync(Host, Fingerprint);
        }
        catch (ManagerException error)
        {
            Error = error.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Builds the package catalog of the current lock file, or again with <paramref name="rebuild"/>.</summary>
    [RelayCommand, RunicFailure(typeof(SoftwareFailure))]
    private void BuildCatalog(bool rebuild)
    {
        if (LockHash is not { Length: > 0 } hash) throw new RunicFailureException(new SoftwareFailure("The checkout has not been read yet."));
        _service.BuildCatalog(hash, rebuild);
        Catalog = _service.CatalogStatus(hash);
    }

    /// <summary>Searches the catalog for <see cref="Query"/> with the filters; setting them searches as well.</summary>
    [RelayCommand]
    private void Search()
    {
        var query = Query.Length > 200 ? Query[..200] : Query;
        Results = query.Trim().Length == 0 ? [] : _service.Search(LockHash, query, new CatalogFilter(FreeOnly, WorkingOnly));
        Searched = Query;
    }

    /// <summary>
    /// Lists the apps of the catalog in <see cref="Apps"/>, starting the download of Flathub's catalogue when there is
    /// none or it is a week old; <paramref name="refresh"/> downloads it again (<c>software/apps</c>). The list follows
    /// later changes of the catalog or the catalogue by itself.
    /// </summary>
    [RelayCommand]
    private void LoadApps(bool refresh)
    {
        _appsKey = null;
        if (_service.AppDetails) _service.FetchApps(refresh);
        UpdateApps(wanted: true);
    }

    /// <summary>
    /// Catalog entries among the first 500 <paramref name="attrs"/>, by attribute, with their app details; unknown
    /// attributes are left out (<c>software/lookup</c>).
    /// </summary>
    [RelayCommand]
    internal Task<IReadOnlyDictionary<string, AppEntry>> LookupPackagesAsync(IReadOnlyList<string> attrs) =>
        Task.FromResult(_service.Lookup(LockHash, attrs.Take(500).ToList()));

    /// <summary>The documented options of a configuration that match a query, best first (<c>software/options</c>).</summary>
    [RelayCommand(AllowConcurrentExecutions = true), RunicFailure(typeof(SoftwareFailure))]
    private async Task<IReadOnlyList<OptionDoc>> SearchOptionsAsync(OptionQuery query)
    {
        var scope = ScopeOf(query.Scope);
        var text = query.Query.Length > 200 ? query.Query[..200] : query.Query;
        try
        {
            return await _service.SearchOptionsAsync(Host, scope, text, Fingerprint);
        }
        catch (ManagerException error)
        {
            throw Failure(error);
        }
    }

    /// <summary>Adds the documentation of these options to <see cref="Docs"/>; known ones are not read again.</summary>
    [RelayCommand(AllowConcurrentExecutions = true), RunicFailure(typeof(SoftwareFailure))]
    private async Task DescribeAsync(OptionsRequest request)
    {
        var scope = ScopeOf(request.Scope);
        var missing = request.Options.Where(name => !Docs.ContainsKey($"{scope}:{name}")).Distinct().ToList();
        if (missing.Count == 0) return;
        var host = Host;
        IReadOnlyList<OptionDoc> docs;
        try
        {
            docs = await _service.DescribeAsync(host, scope, missing, Fingerprint);
        }
        catch (ManagerException error)
        {
            throw Failure(error);
        }
        if (host != Host) return;
        var next = new Dictionary<string, OptionDoc>(Docs);
        foreach (var doc in docs)
        {
            var key = $"{scope}:{doc.N}";
            if (next.TryAdd(key, doc)) _docOrder.Add(key);
            else next[key] = doc;
        }
        while (_docOrder.Count > MaxDocs)
        {
            next.Remove(_docOrder[0]);
            _docOrder.RemoveAt(0);
        }
        Docs = next;
    }

    /// <summary>Current values and definition files of up to 200 options, by name (<c>software/values</c>).</summary>
    [RelayCommand(AllowConcurrentExecutions = true), RunicFailure(typeof(SoftwareFailure))]
    private async Task<IReadOnlyDictionary<string, OptionValue>> ReadValuesAsync(OptionsRequest request)
    {
        var scope = ScopeOf(request.Scope);
        try
        {
            return await _service.ValuesAsync(Host, scope, request.Options);
        }
        catch (ManagerException error)
        {
            throw Failure(error);
        }
    }

    /// <summary>Replaces the draft with the page's edits (<c>/api/draft</c>).</summary>
    [RelayCommand, RunicFailure(typeof(SoftwareFailure))]
    private async Task SetDraftAsync(IReadOnlyList<Change> changes)
    {
        try
        {
            Draft = await _service.SetDraftAsync(new JsonArray([.. changes.Select(change => (JsonNode)change.ToJson())]));
        }
        catch (Exception error) when (error is ManagerException or IOException or UnauthorizedAccessException)
        {
            throw Failure(error);
        }
    }

    private void ReadCatalog()
    {
        Catalog = _service.CatalogStatus(LockHash);
        AppsStatus = _service.AppsStatus(_service.AppDetails);
        Search();
        UpdateApps(wanted: false);
    }

    /// <summary>Lists the apps again when the catalog or Flathub's catalogue changed since the list was made.</summary>
    private void UpdateApps(bool wanted)
    {
        if (!wanted && _appsKey is null && Apps.Entries.Count == 0) return;
        var key = $"{LockHash}\0{Catalog.State}\0{Catalog.BuiltAt}\0{AppsStatus.State}\0{AppsStatus.FetchedAt}";
        if (key == _appsKey) return;
        _appsKey = key;
        Apps.Entries = AppsStatus.State == "off" ? [] : _service.AppList(LockHash);
    }

    private void OnChanged() => _context.TryPost(ReadCatalog);

    public void Dispose()
    {
        _service.Changed -= OnChanged;
    }
}

/// <summary>
/// Every package of the catalog that Flathub describes as an app, for browsing by category (<c>software/apps</c>).
/// A View of its own, because the list holds a few thousand entries and changes only with the catalog.
/// </summary>
public sealed partial class AppListViewModel : ObservableObject
{
    /// <summary>The apps in catalog order, with their app details.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AppEntry> Entries { get; internal set; } = [];
}
