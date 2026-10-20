using NixManager.Domain;
using NixManager.Execution;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.ViewModels;
using Runic.Navigation;

namespace NixManager.Tests;

/// <summary>
/// Generations and the observed system with fake profile folders, the observation cache, and the cleanup estimate.
/// </summary>
public sealed class ObservationTests : IDisposable
{
    private readonly string _root = Native.RealPath(Directory.CreateTempSubdirectory("nix-manager-observe-").FullName)!;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A system output with a NixOS version and kernel, initrd and modules links.</summary>
    private string SystemOutput(string name, string version, string kernel, string? initrd = null)
    {
        var output = Folder(name);
        File.WriteAllText(Path.Combine(output, "nixos-version"), version + "\n");
        File.CreateSymbolicLink(Path.Combine(output, "kernel"), $"{Folder($"store/abc-linux-{kernel}")}/bzImage");
        File.CreateSymbolicLink(Path.Combine(output, "initrd"), Folder($"store/initrd-{initrd ?? kernel}"));
        File.CreateSymbolicLink(Path.Combine(output, "kernel-modules"), Folder($"store/modules-{kernel}"));
        return output;
    }

    [Test]
    public async Task The_generation_inventory_marks_the_current_home_output()
    {
        var profiles = Folder("profiles");
        var profile = Path.Combine(profiles, "home-manager");
        var first = Folder("generation-one");
        var second = Folder("generation-two");
        // The target's time is not the generation's: the link's own time is (lstat).
        Directory.SetLastWriteTimeUtc(second, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.CreateSymbolicLink(profile, second);
        File.CreateSymbolicLink($"{profile}-1-link", first);
        File.CreateSymbolicLink($"{profile}-2-link", second);
        // Not generations: another profile's links, a broken link, a malformed number.
        File.CreateSymbolicLink(Path.Combine(profiles, "home-manager-x-link"), first);
        File.CreateSymbolicLink(Path.Combine(profiles, "home-manager-3-link"), Path.Combine(_root, "missing"));
        File.CreateSymbolicLink(Path.Combine(profiles, "other-4-link"), first);

        var found = SystemObserver.Generations(profile, Native.RealPath(profile));

        await Assert.That(found.Select(g => g.Number).ToArray()).IsEquivalentTo(new[] { 2, 1 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(found[0].Current).IsTrue();
        await Assert.That(found[1].Current).IsFalse();
        await Assert.That(found[0].Output).IsEqualTo(second);
        await Assert.That(found[0].Booted).IsNull();
        await Assert.That(found[0].CreatedAt).DoesNotStartWith("2001");
        await Assert.That(found[0].CreatedAt).Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");
        await Assert.That(SystemObserver.Generations(Path.Combine(_root, "nowhere", "profile"), null)).IsEmpty();
    }

    [Test]
    public async Task The_observed_system_names_versions_kernels_and_a_needed_restart()
    {
        var flake = TestConfig.TemporaryFlake();
        try
        {
            var config = TestConfig.Create(flake) with { HomeProfile = Path.Combine(Folder("home-profiles"), "home-manager") };
            var old = SystemOutput("system-old", "25.05.1 (Warbler)", "6.6.1");
            var next = SystemOutput("system-new", "25.05.2 (Warbler)", "6.6.2");
            var profiles = Folder("system-profiles");
            File.CreateSymbolicLink(Path.Combine(profiles, "system-1-link"), old);
            File.CreateSymbolicLink(Path.Combine(profiles, "system-2-link"), next);
            var run = Folder("run");
            File.CreateSymbolicLink(Path.Combine(run, "current-system"), next);
            File.CreateSymbolicLink(Path.Combine(run, "booted-system"), old);
            var paths = new ObservationPaths(Path.Combine(run, "current-system"), Path.Combine(run, "booted-system"),
                Path.Combine(profiles, "system"), _root);

            var state = await SystemObserver.ObserveAsync(config, paths);

            await Assert.That(state.Current.System).IsEqualTo(next);
            await Assert.That(state.Current.Booted).IsEqualTo(old);
            await Assert.That(state.Current.Home).IsNull();
            await Assert.That(state.NixosVersion).IsEqualTo("25.05.2 (Warbler)");
            await Assert.That(state.Kernel).IsEqualTo(new KernelVersions { Current = "6.6.2", Booted = "6.6.1" });
            await Assert.That(state.RebootRequired).IsTrue();
            var generations = state.Generations.System;
            await Assert.That(generations.Select(g => (g.Number, g.Current, g.Booted, g.Version, g.Kernel)).ToArray()).IsEquivalentTo(new[]
            {
                (2, true, (bool?)false, (string?)"25.05.2 (Warbler)", (string?)"6.6.2"),
                (1, false, (bool?)true, (string?)"25.05.1 (Warbler)", (string?)"6.6.1"),
            }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(state.Generations.Home).IsEmpty();
            await Assert.That(state.Disk!.Path).IsEqualTo(_root);
            await Assert.That(state.Disk.Total).IsGreaterThan(0);
            await Assert.That(state.Repository.LockHash).IsEqualTo(Repository.HashText(""));
            // Serialized in the stored format: home generations without the system's members.
            var json = ManagerJson.Serialize(state);
            await Assert.That(json).Contains("\"rebootRequired\": true");

            // The same boot-critical parts need no restart, whatever else changed.
            File.Delete(Path.Combine(run, "booted-system"));
            File.CreateSymbolicLink(Path.Combine(run, "booted-system"), SystemOutput("system-same", "25.05.2", "6.6.2"));
            await Assert.That((await SystemObserver.ObserveAsync(config, paths)).RebootRequired).IsFalse();
            // Without a booted system nothing can be compared.
            File.Delete(Path.Combine(run, "booted-system"));
            var unbooted = await SystemObserver.ObserveAsync(config, paths);
            await Assert.That(unbooted.RebootRequired).IsFalse();
            await Assert.That(unbooted.Kernel.Booted).IsNull();
            await Assert.That(SystemObserver.Disk(Path.Combine(_root, "missing"))).IsNull();
        }
        finally
        {
            Directory.Delete(flake, recursive: true);
        }
    }

    private static SystemState State(int number) => new()
    {
        ObservedAt = $"pass {number}",
        Repository = new RepositoryState { Fingerprint = "", LockHash = "" },
        Generations = new ProfileGenerations(),
        Current = new CurrentOutputs(),
        Kernel = new KernelVersions(),
    };

    [Test]
    public async Task Concurrent_refreshes_share_one_pass_and_one_asked_for_during_it_runs_it_again()
    {
        var passes = 0;
        var gate = new TaskCompletionSource();
        var observer = new Observer(TestConfig.Create(_root), async _ =>
        {
            var pass = Interlocked.Increment(ref passes);
            if (pass == 1) await gate.Task;
            return State(pass);
        });
        var changes = 0;
        using var subscription = observer.OnChange(() => Interlocked.Increment(ref changes));

        var first = observer.RefreshAsync();
        var second = observer.RefreshAsync();
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        gate.SetResult();
        var state = await first;

        await Assert.That(passes).IsEqualTo(2);
        await Assert.That(state.ObservedAt).IsEqualTo("pass 2");
        await Assert.That(observer.Current).IsEqualTo(state);
        await Assert.That(changes).IsEqualTo(1);
        await observer.RefreshAsync();
        await Assert.That(passes).IsEqualTo(3);
        await Assert.That(changes).IsEqualTo(2);
    }

    private static Generation Generation(int number, string output, bool current = false, bool? booted = null) =>
        new() { Number = number, Output = output, CreatedAt = "2026-10-01T00:00:00Z", Current = current, Booted = booted };

    [Test]
    public async Task Removed_generations_keep_the_newest_and_the_active_one()
    {
        Generation[] all = [Generation(1, "/a", current: true), Generation(2, "/b"), Generation(3, "/c"), Generation(4, "/d")];
        await Assert.That(CleanupEstimator.RemovedGenerations(all, 2).Select(g => g.Number).ToArray()).IsEquivalentTo(new[] { 2 });
        await Assert.That(CleanupEstimator.RemovedGenerations(all, 4)).IsEmpty();
    }

    private static readonly SystemState Profiles = State(0) with
    {
        Generations = new ProfileGenerations
        {
            Home = [Generation(3, "/h3", current: true), Generation(2, "/h2"), Generation(1, "/h1")],
            System = [Generation(2, "/s2", current: true), Generation(1, "/s1", booted: true)],
        },
        Current = new CurrentOutputs { Home = "/h3", System = "/s2", Booted = "/s1" },
    };

    [Test]
    public async Task The_cleanup_estimate_counts_only_what_removed_generations_use_alone()
    {
        var commands = new List<Command>();
        var closures = new Dictionary<string, string> { ["/h1"] = "/h1 100\n/shared 1000\n/only1 10\n", ["/h2"] = "/h2 200\n/shared 1000\n" };
        Task<CommandResult> Run(Command command)
        {
            lock (commands) commands.Add(command);
            var sizes = command.Argv.Contains("--size");
            var paths = command.Argv.Skip(sizes ? 4 : 3);
            var stdout = sizes ? string.Concat(paths.Select(path => closures[path])) : "/h3\n/s2\n/s1\n/shared\n";
            return Task.FromResult(new CommandResult(0, stdout, ""));
        }

        // With one kept, home generations 1 and 2 go; the shared path stays for generation 3.
        await Assert.That(await CleanupEstimator.EstimateAsync(Profiles, Scopes.Home, 1, Run)).IsEqualTo(new CleanupEstimate { Generations = 2, Bytes = 310 });
        var measure = commands.Single(c => c.Argv.Contains("--size"));
        await Assert.That(measure.Label.English).IsEqualTo("Measure what cleaning up frees");
        await Assert.That(measure.Quiet == true && measure.FullOutput == true).IsTrue();
        await Assert.That(commands.Single(c => !c.Argv.Contains("--size")).Argv.Skip(3).ToArray())
            .IsEquivalentTo(new[] { "/h3", "/s2", "/s1" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        // The booted system is removed as a restore point but frees nothing before the restart.
        commands.Clear();
        await Assert.That(await CleanupEstimator.EstimateAsync(Profiles, Scopes.All, 1, Run)).IsEqualTo(new CleanupEstimate { Generations = 3, Bytes = 310 });
        await Assert.That(commands.Single(c => c.Argv.Contains("--size")).Argv).DoesNotContain("/s1");
        commands.Clear();
        await Assert.That(await CleanupEstimator.EstimateAsync(Profiles, Scopes.Home, 5, Run)).IsEqualTo(new CleanupEstimate { Generations = 0, Bytes = 0 });
        await Assert.That(commands).IsEmpty();
        await Assert.That(ManagerJson.Serialize(new CleanupEstimate { Generations = 2, Bytes = 310 }, ManagerJson.Compact))
            .IsEqualTo("""{"generations":2,"bytes":310}""");
    }

    [Test]
    public async Task The_system_ViewModel_follows_the_observer_and_estimates_for_the_chosen_scope()
    {
        await using var context = new RunicModelContext();
        var observer = new Observer(TestConfig.Create(_root), _ => Task.FromResult(Profiles));
        var runner = new FakeRunner((command, _, _) => Task.FromResult(new CommandResult(0,
            command.Argv.Contains("--size") ? "/h1 100\n/h2 200\n" : "", "")));
        using var viewModel = await context.InvokeAsync(() => new SystemViewModel(observer, runner, context));

        await observer.RefreshAsync();
        await Until(context, () => viewModel.System is not null);
        await Assert.That(await context.InvokeAsync(() => viewModel.HomeGenerations.Count)).IsEqualTo(3);
        await context.InvokeAsync(() => viewModel.Retention = 1);
        await context.InvokeAsync(() => viewModel.EstimateCleanupCommand.ExecuteAsync(null));
        await Until(context, () => viewModel.Estimate is not null);
        await Assert.That(await context.InvokeAsync(() => viewModel.Estimate)).IsEqualTo(new CleanupEstimate { Generations = 2, Bytes = 300 });
        // Another choice makes the estimate stale.
        await context.InvokeAsync(() => viewModel.CleanupScope = Scopes.All);
        await Assert.That(await context.InvokeAsync(() => viewModel.Estimate)).IsNull();
    }

    internal static async Task Until(RunicModelContext context, Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await context.InvokeAsync(condition)) return;
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }
}
