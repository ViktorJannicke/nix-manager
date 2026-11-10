using Runic.Platform;

namespace NixManager.Operations;

/// <summary>
/// The computer's power state as Runic reports it (<see cref="IDesktopPowerEvents"/>: logind, UPower and the
/// power-profiles daemon on Linux): whether it runs on battery or saves power, which the background work asks, and
/// when it wakes from sleep, after which the tray checks for updates that became due while it slept.
/// </summary>
/// <remarks>
/// Before the first report, and where the platform does not say (no UPower, or serve-only mode), the state is unknown:
/// the computer counts as plugged in and not saving power, as it did when no battery was found.
/// </remarks>
public sealed class PowerWatch(IDesktopPowerEvents power, TextWriter? log = null)
{
    private readonly Lock _gate = new();
    private DesktopPowerState? _state;

    /// <summary>The state after the latest report, or null before one.</summary>
    public DesktopPowerState? State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Plugged in, or not known to run on battery.</summary>
    public bool OnMains => State?.IsOnBattery != true;

    /// <summary>The user or the system asks applications to save energy, such as the power-saver profile.</summary>
    public bool SavingPower => State?.IsLowPower == true;

    /// <summary>Raised on the watch's thread when the computer woke from sleep.</summary>
    public event Action? Resumed;

    /// <summary>Follows the reports until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var unavailable = false;
        try
        {
            await foreach (var result in power.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                if (result is not PlatformResult<DesktopPowerEvent>.Success(var change))
                {
                    // Once per outage; the watch reports the current state again when its source is back.
                    if (!unavailable) log?.WriteLine($"Power events are unavailable: {result}");
                    unavailable = true;
                    continue;
                }
                unavailable = false;
                lock (_gate) _state = change.State;
                if (change.Kind == DesktopPowerEventKind.Resumed) Resumed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
