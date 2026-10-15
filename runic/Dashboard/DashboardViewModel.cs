using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NixManager.Domain;
using NixManager.Operations;
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

/// <summary>A change to the dashboard's preferences; null leaves a preference as it is.</summary>
/// <param name="Theme"><c>system</c>, <c>light</c> or <c>dark</c>.</param>
/// <param name="Language"><c>system</c>, <c>en</c> or <c>de</c>.</param>
public sealed record PreferenceChange(string? Theme, string? Language);

/// <summary>What a request would run, without running it: the dry run behind a confirmation.</summary>
/// <param name="Request">The request this answers.</param>
/// <param name="Plan">Its steps and notes, or null when it cannot run.</param>
/// <param name="Error">Why it cannot run, in English.</param>
public sealed record PlanPreview(Request Request, Plan? Plan, string? Error);

/// <summary>
/// The dashboard Window: the operations of the checkout, its hosts, the dashboard's preferences and the dry run
/// that a confirmation shows.
/// </summary>
/// <remarks>
/// A view of the process's operation service, like <see cref="OperationsViewModel"/>: closing the Window leaves a
/// running operation running (D025.1). The state directory is watched, so an operation that the command line or
/// the Bun version runs, a new update report and preferences changed elsewhere appear without a refresh.
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IOperationService _service;
    private readonly IRunicModelContext _context;
    private readonly RunicPathWatcherOptions? _watching;
    private readonly RunicPathWatcher _watcher;
    private readonly RunicWatchTarget _preferences;
    private RunicPathWatcher? _foreign;
    private string _foreignKey = "";

    public DashboardViewModel(OperationsViewModel operations, IOperationService service, IRunicModelContext context,
        RunicPathWatcherOptions? watching = null)
    {
        Operations = operations;
        _service = service;
        _context = context;
        _watching = watching;
        var config = service.Config;
        Configuration = new DashboardConfiguration(config.Flake, config.HostKey, config.HomeProfile,
            [.. config.Hosts.OrderBy(host => host.Key, StringComparer.Ordinal)
                .Select(host => new DashboardHost(host.Key, host.Value.System, host.Value.NixosConfiguration, host.Value.HomeConfiguration))]);
        // New records, a new update report and preferences saved by another process. A record rewritten in place,
        // such as another process's running operation, is followed by its own watcher (WatchForeign).
        _preferences = RunicWatchTarget.File(DashboardPreferences.PathOf(config));
        _watcher = RunicPathWatcher.Start(context,
            [
                RunicWatchTarget.Directory(Paths.Operations(config), "*.json"),
                RunicWatchTarget.File(Path.Combine(config.StateDir, "updates.json")),
                _preferences,
            ],
            change =>
            {
                if (change.Contains(_preferences)) ReadPreferences();
                RefreshOperations();
            },
            options: watching);
        Theme = "system";
        Language = "system";
        Locale = "en";
        ReadPreferences();
        WatchForeign();
        Operations.PropertyChanged += OnOperationsChanged;
    }

    /// <summary>The running operation, its log and the history.</summary>
    public OperationsViewModel Operations { get; }

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

    /// <summary>The last dry run, with the request it answers.</summary>
    [ObservableProperty]
    public partial PlanPreview? Preview { get; private set; }

    /// <summary>Saves preferences for this and later launches; the Bun version reads the same file.</summary>
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
    }

    private void RefreshOperations()
    {
        if (Operations.RefreshCommand.CanExecute(null)) Operations.RefreshCommand.Execute(null);
    }

    private void OnOperationsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationsViewModel.Operations)) WatchForeign();
    }

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
        Operations.PropertyChanged -= OnOperationsChanged;
        _watcher.Dispose();
        _foreign?.Dispose();
    }
}
