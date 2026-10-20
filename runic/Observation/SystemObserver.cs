using NixManager.Domain;
using NixManager.Nix;
using NixManager.Operations;

namespace NixManager.Observation;

/// <summary>Where the computer's state is read; tests point them into a temporary folder.</summary>
/// <param name="CurrentSystem">The active system, <c>/run/current-system</c>.</param>
/// <param name="BootedSystem">The system the computer started with, <c>/run/booted-system</c>.</param>
/// <param name="SystemProfile">The system profile, whose <c>system-N-link</c> links are its generations.</param>
/// <param name="Store">The file system whose free space the dashboard shows, <c>/nix/store</c>.</param>
public sealed record ObservationPaths(string CurrentSystem, string BootedSystem, string SystemProfile, string Store)
{
    public static ObservationPaths Default { get; } = new("/run/current-system", "/run/booted-system", Paths.SystemProfile, "/nix/store");
}

/// <summary>Generations and the observed state of the computer.</summary>
public static class SystemObserver
{
    private static string? Resolved(string path) => Native.RealPath(path);

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadLink(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The NixOS version and kernel of a system output or generation link (<c>systemMeta</c>).</summary>
    private static (string? Version, string? Kernel) SystemMeta(string link)
    {
        var version = ReadText(Path.Combine(link, "nixos-version"))?.Trim();
        return (version is { Length: > 0 } ? version : null, NixOutputs.KernelVersion(ReadLink(Path.Combine(link, "kernel"))));
    }

    /// <summary>
    /// The generations of <paramref name="profile"/>, newest first: each <c>PROFILE-N-link</c> beside it, with the link's
    /// modification time as its creation. System generations also say whether they were booted and which NixOS
    /// version and kernel they have.
    /// </summary>
    public static IReadOnlyList<Generation> Generations(string profile, string? current, string? booted = null, bool system = false)
    {
        var prefix = $"{Path.GetFileName(profile)}-";
        var directory = Path.GetDirectoryName(profile) ?? "/";
        string[] names;
        try { names = Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { names = []; }
        var found = new List<Generation>();
        foreach (var name in names)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith("-link", StringComparison.Ordinal) || name.Length < prefix.Length + 5) continue;
            var middle = name[prefix.Length..^5];
            if (!int.TryParse(middle, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1) continue;
            var link = Path.Combine(directory, name);
            var output = Resolved(link);
            if (output is null) continue;
            DateTime modified;
            // The link's own time (lstat), not its target's.
            try { modified = new FileInfo(link).LastWriteTimeUtc; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var generation = new Generation
            {
                Number = number,
                CreatedAt = Timestamps.Format(new DateTimeOffset(modified, TimeSpan.Zero)),
                Output = output,
                Current = output == current,
            };
            if (system)
            {
                var (version, kernel) = SystemMeta(link);
                generation = generation with { Booted = output == booted, Version = version, Kernel = kernel };
            }
            found.Add(generation);
        }
        return found.OrderByDescending(generation => generation.Number).ToList();
    }

    /// <summary>
    /// The checkout's Git state, the generations of both profiles, the current and booted outputs, whether a restart
    /// is needed, and the free space of the store.
    /// </summary>
    public static async Task<SystemState> ObserveAsync(Config config, ObservationPaths? paths = null, TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        paths ??= ObservationPaths.Default;
        var repositoryTask = Repository.StateAsync(config.Flake, cancellationToken);
        var home = Resolved(config.HomeProfile);
        var current = Resolved(paths.CurrentSystem);
        var booted = Resolved(paths.BootedSystem);
        var homeGenerations = Generations(config.HomeProfile, home);
        var systemGenerations = Generations(paths.SystemProfile, current, booted, system: true);
        var currentMeta = current is null ? default((string?, string?)?) : SystemMeta(current);
        var bootedMeta = booted is null ? default((string?, string?)?) : SystemMeta(booted);
        // Mirrors nixos-rebuild: a reboot is needed when boot-critical parts differ.
        var rebootRequired = current is not null && booted is not null && new[] { "kernel", "initrd", "kernel-modules" }.Any(part =>
        {
            var before = Resolved(Path.Combine(paths.BootedSystem, part));
            return before is not null && before != Resolved(Path.Combine(paths.CurrentSystem, part));
        });
        return new SystemState
        {
            ObservedAt = Timestamps.Now(clock),
            Repository = await repositoryTask.ConfigureAwait(false),
            Generations = new ProfileGenerations { Home = homeGenerations, System = systemGenerations },
            Current = new CurrentOutputs { Home = home, System = current, Booted = booted },
            NixosVersion = currentMeta?.Item1,
            Kernel = new KernelVersions { Current = currentMeta?.Item2, Booted = bootedMeta?.Item2 },
            RebootRequired = rebootRequired,
            Disk = Disk(paths.Store),
        };
    }

    /// <summary>Free and total bytes of the file system holding <paramref name="path"/> (<c>statfs</c>), or null.</summary>
    public static DiskSpace? Disk(string path)
    {
        if (!Directory.Exists(path)) return null;
        try
        {
            var drive = new DriveInfo(path);
            return new DiskSpace { Path = path, Free = drive.AvailableFreeSpace, Total = drive.TotalSize };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Caches the last observation. Concurrent refreshes share one pass so
/// clients and operation events never queue redundant Git or file system scans; a refresh asked for during a pass
/// runs the pass once more, and listeners hear of each finished refresh.
/// </summary>
public sealed class Observer(Config config, Func<CancellationToken, Task<SystemState>>? observe = null)
{
    private readonly Func<CancellationToken, Task<SystemState>> _observe = observe ?? (token => SystemObserver.ObserveAsync(config, cancellationToken: token));
    private readonly Lock _gate = new();
    private readonly List<Action> _listeners = [];
    private Task<SystemState>? _pending;
    private bool _again;

    public Config Config => config;

    /// <summary>The last observation, or null before the first refresh finished.</summary>
    public SystemState? Current { get; private set; }

    /// <summary>Calls <paramref name="listener"/> after each refresh; dispose the result to stop.</summary>
    public IDisposable OnChange(Action listener)
    {
        lock (_gate) _listeners.Add(listener);
        return new Subscription(() => { lock (_gate) _listeners.Remove(listener); });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }

    public Task<SystemState> RefreshAsync()
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                _again = true;
                return _pending;
            }
            // The pass clears _pending under the same lock, so it cannot finish before _pending is set; a refresh
            // asked for from now on runs it once more.
            _again = false;
            _pending = Task.Run(RunAsync);
            return _pending;
        }
    }

    private async Task<SystemState> RunAsync()
    {
        try
        {
            SystemState state;
            bool again;
            do
            {
                state = await _observe(CancellationToken.None).ConfigureAwait(false);
                Current = state;
                lock (_gate)
                {
                    again = _again;
                    _again = false;
                }
            } while (again);
            Action[] listeners;
            lock (_gate) listeners = [.. _listeners];
            foreach (var listener in listeners)
            {
                try { listener(); }
                catch (Exception e) { Console.Error.WriteLine(e); }
            }
            return state;
        }
        finally
        {
            lock (_gate) _pending = null;
        }
    }
}
