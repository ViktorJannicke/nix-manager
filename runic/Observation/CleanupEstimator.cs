using NixManager.Domain;
using NixManager.Execution;

namespace NixManager.Observation;

/// <summary>What a cleanup would free (<c>CleanupEstimate</c> in <c>src/space.ts</c>).</summary>
public sealed record CleanupEstimate
{
    /// <summary>Restore points the cleanup removes.</summary>
    public int Generations { get; init; }
    /// <summary>Bytes in store paths only those restore points use.</summary>
    public long Bytes { get; init; }
}

/// <summary>Estimates a cleanup without removing anything (<c>src/space.ts</c>, <c>removedGenerations</c> in <c>src/summary.ts</c>).</summary>
public static class CleanupEstimator
{
    /// <summary>
    /// The generations <c>nix-env --delete-generations +N</c> removes: all but the newest <paramref name="retention"/>
    /// and the active one (<c>removedGenerations</c>).
    /// </summary>
    public static IReadOnlyList<Generation> RemovedGenerations(IReadOnlyList<Generation> generations, int retention)
    {
        var kept = generations.OrderByDescending(generation => generation.Number).Take(retention).Select(generation => generation.Number).ToHashSet();
        return generations.Where(generation => !generation.Current && !kept.Contains(generation.Number)).ToList();
    }

    /// <summary>
    /// Measures what a cleanup keeping the newest <paramref name="retention"/> restore points of
    /// <paramref name="scope"/> (<c>home</c> or <c>all</c>) frees. Other roots, such as build results, can still keep
    /// some of it; paths nothing uses any more are collected on top (<c>estimateCleanup</c>).
    /// </summary>
    /// <param name="run">Runs the two quiet <c>nix path-info</c> queries.</param>
    public static async Task<CleanupEstimate> EstimateAsync(SystemState system, string scope, int retention, Func<Command, Task<CommandResult>> run)
    {
        var profiles = scope == Scopes.All ? new[] { system.Generations.Home, system.Generations.System } : [system.Generations.Home];
        var removed = profiles.SelectMany(generations => RemovedGenerations(generations, retention)).ToList();
        var removedSet = removed.ToHashSet(ReferenceEqualityComparer.Instance);
        // The booted system stays a root until the next restart, so it frees nothing yet.
        var kept = new List<string>();
        var keptSet = new HashSet<string>();
        void Keep(string path)
        {
            if (keptSet.Add(path)) kept.Add(path);
        }
        foreach (var generation in system.Generations.Home.Concat(system.Generations.System))
            if (!removedSet.Contains(generation) || generation.Booted == true) Keep(generation.Output);
        foreach (var path in new[] { system.Current.Home, system.Current.System, system.Current.Booted })
            if (path is { Length: > 0 }) Keep(path);
        var freed = removed.Select(generation => generation.Output).Distinct().Where(path => !keptSet.Contains(path)).ToList();
        if (freed.Count == 0) return new CleanupEstimate { Generations = removed.Count, Bytes = 0 };
        Task<CommandResult> Closure(IEnumerable<string> paths, bool sizes) => run(new Command
        {
            Label = "Measure what cleaning up frees",
            Argv = ["nix", "path-info", "--recursive", .. sizes ? new[] { "--size" } : [], .. paths],
            Quiet = true,
            FullOutput = true,
        });
        // Without paths, `nix path-info` would read the flake in the working directory.
        var leaving = Closure(freed, sizes: true);
        var staying = kept.Count > 0 ? Closure(kept, sizes: false) : Task.FromResult(new CommandResult(0, "", ""));
        await Task.WhenAll(leaving, staying).ConfigureAwait(false);
        var stays = (await staying.ConfigureAwait(false)).Stdout.Split('\n').Select(line => line.Trim()).ToHashSet();
        long bytes = 0;
        foreach (var line in (await leaving.ConfigureAwait(false)).Stdout.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || stays.Contains(parts[0])) continue;
            if (parts.Length > 1 && long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var size))
                bytes += size;
        }
        return new CleanupEstimate { Generations = removed.Count, Bytes = bytes };
    }
}
