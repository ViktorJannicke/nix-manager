using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Observation;
using Runic.Navigation;

namespace NixManager.ViewModels;

/// <summary>
/// The computer as the dashboard shows it: the observed state with both profiles' generations, whether a restart is
/// needed, the store's free space, and what a cleanup keeping <see cref="Retention"/> restore points of
/// <see cref="CleanupScope"/> would free.
/// </summary>
/// <remarks>
/// The ViewModel is a view of the shared <see cref="Observer"/>; refreshes from other Windows or after operations
/// reach it through <see cref="Observer.OnChange"/>, committed in model-context turns.
/// </remarks>
public sealed partial class SystemViewModel : ObservableObject, IDisposable
{
    private readonly Observer _observer;
    private readonly ICommandRunner _runner;
    private readonly IRunicModelContext _context;
    private readonly IDisposable _subscription;

    public SystemViewModel(Observer observer, ICommandRunner runner, IRunicModelContext context)
    {
        _observer = observer;
        _runner = runner;
        _context = context;
        _subscription = observer.OnChange(() => _context.TryPost(() => System = _observer.Current));
        System = observer.Current;
    }

    /// <summary>The last observation, or null before the first one.</summary>
    [ObservableProperty]
    public partial SystemState? System { get; private set; }

    /// <summary>Home Manager generations, newest first.</summary>
    public IReadOnlyList<Generation> HomeGenerations => System?.Generations.Home ?? [];

    /// <summary>System generations, newest first, with the booted one marked.</summary>
    public IReadOnlyList<Generation> SystemGenerations => System?.Generations.System ?? [];

    /// <summary><c>home</c> or <c>all</c>: whose restore points a cleanup removes.</summary>
    [ObservableProperty]
    public partial string CleanupScope { get; set; } = Scopes.Home;

    /// <summary>Restore points a cleanup keeps, 1 to 100.</summary>
    [ObservableProperty]
    public partial int Retention { get; set; } = 3;

    /// <summary>What the cleanup would free, for the current scope and retention once estimated.</summary>
    [ObservableProperty]
    public partial CleanupEstimate? Estimate { get; private set; }

    [ObservableProperty]
    public partial bool Estimating { get; private set; }

    /// <summary>Why the last refresh or estimate failed.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnSystemChanged(SystemState? value)
    {
        OnPropertyChanged(nameof(HomeGenerations));
        OnPropertyChanged(nameof(SystemGenerations));
        Estimate = null;
    }

    partial void OnCleanupScopeChanged(string value) => Estimate = null;

    partial void OnRetentionChanged(int value) => Estimate = null;

    /// <summary>Observes the computer again; concurrent refreshes share one pass.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var state = await _observer.RefreshAsync().ConfigureAwait(false);
            _context.TryPost(() =>
            {
                System = state;
                Error = null;
            });
        }
        catch (Exception error) when (error is ManagerException or IOException)
        {
            _context.TryPost(() => Error = error.Message);
        }
    }

    /// <summary>Measures what a cleanup with the chosen scope and retention frees, without removing anything.</summary>
    [RelayCommand]
    private async Task EstimateCleanupAsync()
    {
        var (system, scope, retention) = (System, CleanupScope, Retention);
        if (system is null) return;
        Estimating = true;
        try
        {
            var estimate = await CleanupEstimator.EstimateAsync(system, scope, retention,
                command => _runner.RunAsync(command, _ => { }, CancellationToken.None)).ConfigureAwait(false);
            _context.TryPost(() =>
            {
                // A newer observation or another choice makes this estimate stale.
                if (System == system && CleanupScope == scope && Retention == retention) Estimate = estimate;
                Error = null;
                Estimating = false;
            });
        }
        catch (ManagerException error)
        {
            _context.TryPost(() =>
            {
                Error = error.Message;
                Estimating = false;
            });
        }
    }

    public void Dispose() => _subscription.Dispose();
}
