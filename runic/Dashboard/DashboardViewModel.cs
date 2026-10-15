using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Operations;
using NixManager.Software;
using NixManager.ViewModels;
using Runic.Application.Views;
using Runic.Navigation;

namespace NixManager.Dashboard;

/// <summary>A configuration the checkout defines, as the dashboard lists it.</summary>
/// <param name="Name">The host's key, which requests name.</param>
/// <param name="System">Its platform, such as <c>x86_64-linux</c>.</param>
/// <param name="NixosConfiguration">The <c>nixosConfigurations</c> attribute it builds.</param>
/// <param name="HomeConfiguration">The <c>homeConfigurations</c> attribute it builds.</param>
public sealed record DashboardHost(string Name, string System, string NixosConfiguration, string HomeConfiguration);

/// <summary>The checkout the dashboard works on and the hosts it defines.</summary>
/// <param name="Flake">The configuration checkout.</param>
/// <param name="HostKey">The host of this computer.</param>
/// <param name="HomeProfile">The Home Manager profile that home generations go to.</param>
/// <param name="Hosts">Every host of the checkout, by name.</param>
public sealed record DashboardConfiguration(string Flake, string HostKey, string HomeProfile, IReadOnlyList<DashboardHost> Hosts);

/// <summary>A change to the dashboard's preferences (<c>Preferences</c> in <c>src/preferences.ts</c>); null leaves a preference as it is.</summary>
/// <param name="Theme"><c>system</c>, <c>light</c> or <c>dark</c>.</param>
/// <param name="Language"><c>system</c>, <c>en</c> or <c>de</c>.</param>
/// <param name="Detail"><c>simple</c> or <c>detailed</c>.</param>
/// <param name="PrepareUpdates">Prepare updates the tray finds while plugged in and not busy.</param>
/// <param name="CleanUp">Remove older restore points of the apps once a week.</param>
/// <param name="History">Commit the files each applied change writes.</param>
/// <param name="Backup">Push those commits when the branch has an upstream.</param>
/// <param name="AppDetails">Name, describe and picture apps with Flathub's catalogue.</param>
public sealed record PreferenceChange(string? Theme = null, string? Language = null, string? Detail = null, bool? PrepareUpdates = null,
    bool? CleanUp = null, bool? History = null, bool? Backup = null, bool? AppDetails = null);

/// <summary>What a request would run, without running it: the dry run behind a confirmation.</summary>
/// <param name="Request">The request this answers.</param>
/// <param name="Plan">Its steps and notes, or null when it cannot run.</param>
/// <param name="Error">Why it cannot run, in English.</param>
public sealed record PlanPreview(Request Request, Plan? Plan, string? Error);

/// <summary>
/// The dashboard Window: the operations of the checkout, its reviews, the computer's state, the Software page, its
/// hosts, the dashboard's preferences, the pending edits and the dry run that a confirmation shows.
/// </summary>
/// <remarks>
/// A view of the process's services, like <see cref="OperationsViewModel"/>: closing the Window leaves a running
/// operation running (D025.1). The state directory is watched, so an operation that the command line or the Bun
/// version runs, a new update report, reviews, pending edits and preferences changed elsewhere appear without a
/// refresh. The computer is observed again when the Window opens, after each operation and every 30 seconds while
/// it is open, as the Bun dashboard does, to notice edits to the checkout and changes made outside Nix Manager.
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the computer is observed again while the Window is open.</summary>
    public static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(30);

    private readonly IOperationService _service;
    private readonly ISoftwareService _software;
    private readonly IRunicModelContext _context;
    private readonly RunicPathWatcherOptions? _watching;
    private readonly RunicPathWatcher _watcher;
    private readonly RunicWatchTarget _preferences;
    private readonly RunicWatchTarget _draft;
    private readonly RunicWatchTarget _reviews;
    private readonly ITimer _ticker;
    private RunicPathWatcher? _foreign;
    private string _foreignKey = "";
    private bool _running;
    private string? _draftFingerprint;

    public DashboardViewModel(OperationsViewModel operations, ReviewsViewModel reviews, SystemViewModel system,
        SoftwareViewModel software, IOperationService service, ISoftwareService softwareService, IRunicModelContext context,
        TimeProvider? clock = null, RunicPathWatcherOptions? watching = null)
    {
        Operations = operations;
        Reviews = reviews;
        System = system;
        Software = software;
        _service = service;
        _software = softwareService;
        _context = context;
        _watching = watching;
        var config = service.Config;
        Configuration = new DashboardConfiguration(config.Flake, config.HostKey, config.HomeProfile,
            [.. config.Hosts.OrderBy(host => host.Key, StringComparer.Ordinal)
                .Select(host => new DashboardHost(host.Key, host.Value.System, host.Value.NixosConfiguration, host.Value.HomeConfiguration))]);
        // New records, a new update report, reviews, pending edits and preferences saved by another process. A record
        // rewritten in place, such as another process's running operation, is followed by its own watcher (WatchForeign).
        _preferences = RunicWatchTarget.File(DashboardPreferences.PathOf(config));
        _draft = RunicWatchTarget.File(Path.Combine(config.StateDir, "draft.json"));
        _reviews = RunicWatchTarget.Directory(Path.Combine(config.StateDir, "reviews"));
        _watcher = RunicPathWatcher.Start(context,
            [
                RunicWatchTarget.Directory(Paths.Operations(config), "*.json"),
                RunicWatchTarget.File(Path.Combine(config.StateDir, "updates.json")),
                _preferences,
                _draft,
                _reviews,
            ],
            change =>
            {
                if (change.Contains(_preferences)) ReadPreferences();
                if (change.Contains(_draft)) ReadDraft(force: true);
                if (change.Contains(_reviews)) Execute(Reviews.RefreshCommand);
                RefreshOperations();
            },
            options: watching);
        Theme = "system";
        Language = "system";
        Locale = "en";
        Detail = "simple";
        ReadPreferences();
        WatchForeign();
        _running = Running();
        Operations.PropertyChanged += OnOperationsChanged;
        System.PropertyChanged += OnSystemChanged;
        _software.Changed += OnSoftwareChanged;
        // A fresh observation unless another Window or the daemon made one in the last seconds (src/server.ts).
        var observed = System.System is { } state && Timestamps.Parse(state.ObservedAt) is { } at ? at : (DateTimeOffset?)null;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (observed is null || now - observed > TimeSpan.FromSeconds(5)) _context.TryPost(RefreshSystem);
        ReadDraft(force: false);
        _ticker = (clock ?? TimeProvider.System).CreateTimer(_ => _context.TryPost(RefreshSystem), null, ObservationInterval, ObservationInterval);
    }

    /// <summary>The running operation, its log and the history.</summary>
    public OperationsViewModel Operations { get; }

    /// <summary>The prepared reviews and the configuration's history of changes.</summary>
    public ReviewsViewModel Reviews { get; }

    /// <summary>Restore points, the checkout's state, whether a restart is needed, free space and cleanup estimates.</summary>
    public SystemViewModel System { get; }

    /// <summary>The Software page: what a host installs, the package catalogue and editing the draft.</summary>
    public SoftwareViewModel Software { get; }

    /// <summary>The checkout and its hosts.</summary>
    public DashboardConfiguration Configuration { get; }

    /// <summary>The chosen theme: <c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    [ObservableProperty]
    public partial string Theme { get; private set; }

    /// <summary>The chosen language: <c>system</c>, <c>en</c> or <c>de</c>.</summary>
    [ObservableProperty]
    public partial string Language { get; private set; }

    /// <summary>The language the dashboard speaks, <c>en</c> or <c>de</c>: the chosen one, or the desktop's.</summary>
    [ObservableProperty]
    public partial string Locale { get; private set; }

    /// <summary>
    /// <c>simple</c> shows one button and a plain summary; <c>detailed</c> shows every prepare, review and apply step.
    /// Someone who ran operations before starts detailed, everyone else simple (<see cref="Preferences"/>).
    /// </summary>
    [ObservableProperty]
    public partial string Detail { get; private set; }

    /// <summary>Updates the tray finds are prepared while the computer is plugged in and not busy.</summary>
    [ObservableProperty]
    public partial bool PrepareUpdates { get; private set; }

    /// <summary>Older restore points of the apps are removed once a week.</summary>
    [ObservableProperty]
    public partial bool CleanUp { get; private set; }

    /// <summary>Each applied change is committed to the checkout with a message saying what it did.</summary>
    [ObservableProperty]
    public partial bool History { get; private set; }

    /// <summary>Those commits are pushed when the branch has an upstream.</summary>
    [ObservableProperty]
    public partial bool Backup { get; private set; }

    /// <summary>Apps are named, described and pictured with Flathub's catalogue.</summary>
    [ObservableProperty]
    public partial bool AppDetails { get; private set; }

    /// <summary>
    /// The pending edits, rendered against the observed checkout, or null before they are read. The Overview, the
    /// navigation and the Changes page show them; the Software page edits them.
    /// </summary>
    [ObservableProperty]
    public partial RenderedDraft? Draft { get; private set; }

    /// <summary>The last dry run, with the request it answers.</summary>
    [ObservableProperty]
    public partial PlanPreview? Preview { get; private set; }

    /// <summary>Saves preferences for this and later launches; the Bun version and the background tasks read the same file.</summary>
    [RelayCommand]
    private void SavePreferences(PreferenceChange change)
    {
        DashboardPreferences.Save(_service.Config, change);
        ReadPreferences();
    }

    /// <summary>Plans <paramref name="request"/> without running it, for a confirmation to show.</summary>
    [RelayCommand]
    private void PreviewPlan(Request request)
    {
        var r = request with { Host = request.Host is { Length: > 0 } ? request.Host : _service.Config.HostKey };
        try
        {
            Preview = new PlanPreview(request, _service.Plan(r), null);
        }
        catch (ManagerException error)
        {
            Preview = new PlanPreview(request, null, error.Message);
        }
    }

    private void ReadPreferences()
    {
        var preferences = DashboardPreferences.Read(_service.Config);
        Theme = preferences.Theme;
        Language = preferences.Language;
        Locale = DashboardPreferences.ResolveLocale(preferences.Language, Environment.GetEnvironmentVariable);
        Detail = preferences.Detail;
        PrepareUpdates = preferences.PrepareUpdates;
        CleanUp = preferences.CleanUp;
        History = preferences.History;
        Backup = preferences.Backup;
        AppDetails = preferences.AppDetails;
    }

    private void RefreshSystem() => Execute(System.RefreshCommand);

    private static void Execute(global::System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void RefreshOperations() => Execute(Operations.RefreshCommand);

    /// <summary>Reads the pending edits for the observed checkout; <paramref name="force"/> reads them again for the same one.</summary>
    private void ReadDraft(bool force)
    {
        var fingerprint = System.System?.Repository.Fingerprint ?? "";
        if (!force && fingerprint == _draftFingerprint) return;
        _draftFingerprint = fingerprint;
        _ = ReadDraftAsync(fingerprint);
    }

    private async Task ReadDraftAsync(string fingerprint)
    {
        try
        {
            var draft = await _software.DraftAsync(fingerprint).ConfigureAwait(false);
            // A newer observation reads the draft again.
            _context.TryPost(() =>
            {
                if (_draftFingerprint == fingerprint) Draft = draft;
            });
        }
        catch (Exception error) when (error is ManagerException or IOException)
        {
            _context.TryPost(() =>
            {
                if (_draftFingerprint == fingerprint) Draft = null;
            });
        }
    }

    private bool Running() => Operations.Active is not null || Operations.Operations.Any(operation => operation.Status == OperationStatuses.Running);

    private void OnOperationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationsViewModel.Operations)) WatchForeign();
        if (e.PropertyName is not (nameof(OperationsViewModel.Operations) or nameof(OperationsViewModel.Active))) return;
        // Generations, the checkout and reviews change when an operation ends, here or in another process.
        var running = Running();
        if (_running && !running)
        {
            RefreshSystem();
            Execute(Reviews.RefreshCommand);
        }
        _running = running;
    }

    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemViewModel.System)) ReadDraft(force: false);
    }

    private void OnSoftwareChanged() => _context.TryPost(() => ReadDraft(force: true));

    /// <summary>
    /// Follows the records of operations that another process runs: each change is written by renaming a new
    /// file over the record, which the directory watch does not report.
    /// </summary>
    private void WatchForeign()
    {
        var running = Operations.Operations
            .Where(operation => operation.Status == OperationStatuses.Running && operation.Id != _service.Active?.Id)
            .Select(operation => Path.Combine(Paths.Operations(_service.Config), $"{operation.Id}.json"))
            .Order(StringComparer.Ordinal)
            .ToList();
        var key = string.Join('\n', running);
        if (key == _foreignKey) return;
        _foreignKey = key;
        _foreign?.Dispose();
        _foreign = running.Count == 0 ? null : RunicPathWatcher.Start(_context, running.Select(RunicWatchTarget.File), _ => RefreshOperations(), options: _watching);
    }

    public void Dispose()
    {
        _ticker.Dispose();
        Operations.PropertyChanged -= OnOperationsChanged;
        System.PropertyChanged -= OnSystemChanged;
        _software.Changed -= OnSoftwareChanged;
        _watcher.Dispose();
        _foreign?.Dispose();
    }
}
