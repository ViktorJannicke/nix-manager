using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NixManager.Domain;
using NixManager.Operations;
using Runic.Platform;

namespace NixManager.Tests;

/// <summary>Work started unasked: the power check, weekly cleanup and preparing updates (<c>test/background.test.ts</c>).</summary>
public sealed class BackgroundTests : IDisposable
{
    private readonly string _flake = TestConfig.TemporaryFlake();
    private Config Config => TestConfig.Create(_flake);
    private static readonly BackgroundConditions Always = new(() => true, () => true);

    public void Dispose() => Directory.Delete(_flake, recursive: true);

    private void Preferences(string json)
    {
        Directory.CreateDirectory(Config.StateDir);
        File.WriteAllText(Path.Combine(Config.StateDir, "preferences.json"), json);
    }

    private void Report(params (string Name, string Latest, string Kind)[] items) =>
        File.WriteAllText(Path.Combine(Config.StateDir, "updates.json"), new JsonObject
        {
            ["flake"] = Config.Flake,
            ["report"] = new JsonObject
            {
                ["checkedAt"] = Timestamps.Now(),
                ["errors"] = new JsonArray(),
                ["items"] = new JsonArray([.. items.Select(item => (JsonNode)new JsonObject
                {
                    ["name"] = item.Name, ["current"] = "old", ["latest"] = item.Latest, ["kind"] = item.Kind,
                })]),
            },
        }.ToJsonString());

    [Test]
    public async Task Runics_power_state_decides_battery_and_power_saving_and_unknown_counts_as_plugged_in()
    {
        var events = new FakePowerEvents();
        var power = new PowerWatch(events);
        var resumed = 0;
        power.Resumed += () => Interlocked.Increment(ref resumed);
        using var stop = new CancellationTokenSource();
        var watching = power.RunAsync(stop.Token);
        await Assert.That(power.OnMains).IsTrue();
        await Assert.That(power.SavingPower).IsFalse();

        events.Report(DesktopPowerEventKind.StateChanged, new DesktopPowerState { IsOnBattery = true, IsLowPower = false });
        await WaitForAsync(() => !power.OnMains);
        events.Report(DesktopPowerEventKind.StateChanged, new DesktopPowerState { IsOnBattery = false, IsLowPower = true });
        await WaitForAsync(() => power.OnMains && power.SavingPower);
        events.Report(DesktopPowerEventKind.Resumed, new DesktopPowerState { IsOnBattery = false, IsLowPower = false });
        await WaitForAsync(() => Volatile.Read(ref resumed) == 1);
        await Assert.That(power.SavingPower).IsFalse();
        // A platform that does not say keeps the background work possible.
        events.Report(DesktopPowerEventKind.StateChanged, new DesktopPowerState());
        await WaitForAsync(() => power.State is { IsOnBattery: null });
        await Assert.That(power.OnMains).IsTrue();

        await stop.CancelAsync();
        await watching;
    }

    [Test]
    public async Task Nothing_runs_unasked_while_the_computer_saves_power()
    {
        await using var service = new OperationService(Config, new FakeRunner(), new FakeUpdates(new UpdateReport { CheckedAt = "" }));
        Preferences("""{"detail":"simple","cleanUp":true,"prepareUpdates":true}""");
        Report(("nixpkgs", "b", "input"));
        var saving = new BackgroundTasks(service, new(() => true, () => true, () => true));
        await Assert.That(saving.CleanUpInBackground().Reason).IsEqualTo("The computer saves power.");
        await Assert.That(saving.PrepareInBackground().Reason).IsEqualTo("The computer saves power.");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Test]
    public async Task Cleaning_up_runs_once_a_week_only_for_the_apps_and_only_when_allowed()
    {
        var runner = new FakeRunner();
        await using var service = new OperationService(Config, runner, new FakeUpdates(new UpdateReport { CheckedAt = "" }));
        await Assert.That(new BackgroundTasks(service, Always).CleanUpInBackground().Reason).IsEqualTo("Cleaning up automatically is off.");
        Preferences("""{"detail":"simple","cleanUp":true}""");
        await Assert.That(new BackgroundTasks(service, new(() => false, () => true)).CleanUpInBackground().Reason).IsEqualTo("The computer runs on battery.");
        await Assert.That(new BackgroundTasks(service, new(() => true, () => false)).CleanUpInBackground().Reason).IsEqualTo("The computer is busy.");
        var background = new BackgroundTasks(service, Always);
        var now = DateTimeOffset.Parse("2026-10-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var (operation, reason) = background.CleanUpInBackground(now, store: _flake);
        await Assert.That(reason).IsNull();
        await Assert.That(operation!.Request.Action).IsEqualTo(Actions.Gc);
        await Assert.That(operation.Request.Scope).IsEqualTo(Scopes.Home);
        await Assert.That(operation.Request.Host).IsEqualTo("test");
        await Assert.That(operation.Request.Retention).IsEqualTo(BackgroundTasks.KeptAutomatically);
        await Assert.That(background.CleanUpInBackground(now).Reason).IsEqualTo("The last cleanup was less than a week ago.");
        await Assert.That((await service.WaitAsync())!.Status).IsEqualTo(OperationStatuses.Succeeded);
        var last = BackgroundTasks.LastCleanup(Config)!;
        await Assert.That(last.OperationId).IsEqualTo(operation.Id);
        await Assert.That(last.At).IsEqualTo("2026-10-11T12:00:00.000Z");
        await Assert.That(last.Free).IsNotNull();
        // No privileged step: system restore points are only removed when asked.
        await Assert.That(runner.Calls.Select(c => c.Argv[0]).ToArray()).IsEquivalentTo(new[] { "nix-env", "nix-store" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(background.CleanUpInBackground(now + TimeSpan.FromDays(6)).Reason).IsEqualTo("The last cleanup was less than a week ago.");
        var next = background.CleanUpInBackground(now + TimeSpan.FromDays(7));
        await Assert.That(next.Operation).IsNotNull();
        await service.WaitAsync();
    }

    [Test]
    public async Task Updates_are_prepared_only_when_allowed_new_plugged_in_and_idle()
    {
        await using var service = new OperationService(Config, new FakeRunner(), new FakeUpdates(new UpdateReport { CheckedAt = "" }));
        Directory.CreateDirectory(Config.StateDir);
        Report(("nixpkgs", "b", "input"));
        await Assert.That(new BackgroundTasks(service, Always).PrepareInBackground().Reason).IsEqualTo("Preparing updates in the background is off.");
        Preferences("""{"detail":"simple","prepareUpdates":true}""");
        await Assert.That(new BackgroundTasks(service, new(() => false, () => true)).PrepareInBackground().Reason).IsEqualTo("The computer runs on battery.");
        await Assert.That(new BackgroundTasks(service, new(() => true, () => false)).PrepareInBackground().Reason).IsEqualTo("The computer is busy.");
        // Package pins change files in the checkout, so they are not prepared unasked.
        Report(("firefox", "131", "package"));
        await Assert.That(new BackgroundTasks(service, Always).PrepareInBackground().Reason).IsEqualTo("There are no updates to prepare.");
        Report(("nixpkgs", "b", "input"), ("firefox", "131", "package"));
        File.WriteAllText(Path.Combine(Config.StateDir, "background.json"), """{"signature":"[[\"nixpkgs\",\"b\"]]","reviewId":"aaaaaaaa"}""");
        await Assert.That(new BackgroundTasks(service, Always).PrepareInBackground().Reason).IsEqualTo("These updates were prepared already.");
        await Assert.That(BackgroundTasks.LastPrepared(Config)).IsEqualTo(new PreparedUpdates("[[\"nixpkgs\",\"b\"]]", "aaaaaaaa"));
        // A newer release is prepared again, but not while something else runs.
        Report(("nixpkgs", "c", "input"));
        var release = new TaskCompletionSource();
        await using var busy = new OperationService(Config, new FakeRunner(async (_, _, _) =>
        {
            await release.Task;
            return new Execution.CommandResult(0, "", "");
        }), new FakeUpdates(new UpdateReport { CheckedAt = "" }));
        busy.Start(Request.Create() with { Action = Actions.Build, Scope = Scopes.Home, Host = "test" });
        await Assert.That(new BackgroundTasks(busy, Always).PrepareInBackground().Reason).IsEqualTo("Something else is running.");
        release.SetResult();
        await busy.WaitAsync();
    }
}

/// <summary>Power events the test reports, as Runic's would.</summary>
internal sealed class FakePowerEvents : IDesktopPowerEvents
{
    private readonly Channel<PlatformResult<DesktopPowerEvent>> _events = Channel.CreateUnbounded<PlatformResult<DesktopPowerEvent>>();
    private DesktopPowerState _state = new();

    public void Report(DesktopPowerEventKind kind, DesktopPowerState state)
    {
        _state = state;
        _events.Writer.TryWrite(new PlatformResult<DesktopPowerEvent>.Success(new DesktopPowerEvent(kind, state)));
    }

    public ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<PlatformResult<DesktopPowerState>>(new PlatformResult<DesktopPowerState>.Success(_state));

    public async IAsyncEnumerable<PlatformResult<DesktopPowerEvent>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken)) yield return item;
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
