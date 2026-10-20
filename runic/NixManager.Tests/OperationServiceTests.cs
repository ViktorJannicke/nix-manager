using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Updates;

namespace NixManager.Tests;

internal sealed class FakeRunner(Func<Command, Action<OutputEvent>, CancellationToken, Task<CommandResult>>? run = null) : ICommandRunner
{
    public List<Command> Calls { get; } = [];

    public Task<CommandResult> RunAsync(Command command, Action<OutputEvent> output, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add(command);
        return run?.Invoke(command, output, cancellationToken) ?? Task.FromResult(new CommandResult(0, "", ""));
    }
}

internal sealed class FakeUpdates(UpdateReport report) : IUpdateChecker
{
    public async Task<UpdateReport> CheckAsync(Config config, Func<Command, Task<CommandResult>> run, CancellationToken cancellationToken)
    {
        await run(new Command { Label = DisplayText.Of("stored_check_flake_inputs_without_changing_the_checkout"), Argv = ["nix", "flake", "update"] });
        return report;
    }
}

/// <summary>The operation service: steps, events, the stored record and log, the lock and cancellation.</summary>
public sealed class OperationServiceTests : IDisposable
{
    private readonly string _flake = TestConfig.TemporaryFlake();
    private Config Config => TestConfig.Create(_flake);

    public void Dispose() => Directory.Delete(_flake, recursive: true);

    private OperationService Service(ICommandRunner runner, IUpdateChecker? updates = null) =>
        new(Config, runner, updates ?? new FakeUpdates(new UpdateReport { CheckedAt = "2026-10-15T00:00:00.000Z" }));

    private static Request Switch(string scope = Scopes.All, bool cleanup = false) =>
        Request.Create() with { Action = Actions.Switch, Scope = scope, Cleanup = cleanup, Host = "test" };

    [Test]
    public async Task Events_are_ordered_and_the_record_keeps_step_outcomes_output_and_progress()
    {
        var runner = new FakeRunner((command, output, _) =>
        {
            output(new TextOutput("stderr", $"\u001b[1m{command.Label}\u001b[0m\rdone\n"));
            if (command.Progress == true) output(new ProgressOutput(new BuildProgress { Builds = new() { Done = 1, Expected = 2 } }));
            return Task.FromResult(new CommandResult(0, "", ""));
        });
        await using var service = Service(runner);
        var events = new List<ManagerEvent>();
        using var _ = service.Subscribe(e => { lock (events) events.Add(e); });

        var run = service.Start(Request.Create() with { Action = Actions.Build, Scope = Scopes.Home, Host = "test" });
        var operation = await run.Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(operation.Step.English).IsEqualTo("Complete");
        await Assert.That(operation.Steps!.Select(s => (s.Id, s.Label.English, s.Status)))
            .IsEquivalentTo([("1-build-home-configuration", "Build home configuration", StepStatuses.Succeeded)]);
        await Assert.That(operation.Steps![0].Progress!.Builds.Expected).IsEqualTo(2);
        await Assert.That(events.Select(e => e.Sequence)).IsInOrder();
        await Assert.That(events.OfType<OperationChanged>().Any(e =>
            e.Operation.Status == OperationStatuses.Running && e.Operation.Steps!.Any(s => s.Status == StepStatuses.Running))).IsTrue();
        await Assert.That(events.OfType<OperationProgress>().Single().StepId).IsEqualTo("1-build-home-configuration");
        // The log keeps readable text: ANSI sequences removed, line ends normalised, offsets in bytes.
        var output = events.OfType<OperationOutput>().ToList();
        var log = service.Log(operation.Id);
        await Assert.That(log.Text).IsEqualTo(string.Concat(output.Select(o => o.Text)));
        await Assert.That(log.Text).Contains("→ Build home configuration\n  nix build --no-link");
        await Assert.That(log.Text).Contains("\nBuild home configuration\ndone\n");
        await Assert.That(output.Zip(output.Skip(1)).All(pair => pair.First.Offset + System.Text.Encoding.UTF8.GetByteCount(pair.First.Text) == pair.Second.Offset)).IsTrue();
        // The stored record is the one the history reads, in the stored layout.
        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(Config.StateDir, "operations", $"{operation.Id}.json")))!.AsObject();
        await Assert.That(stored["status"]!.GetValue<string>()).IsEqualTo("succeeded");
        await Assert.That(stored["request"]!["dryRun"]!.GetValue<bool>()).IsFalse();
        await Assert.That(stored["steps"]![0]!["finishedAt"]).IsNotNull();
        await Assert.That(service.History()[0].Id).IsEqualTo(operation.Id);
    }

    [Test]
    [Arguments("Build home configuration")]
    [Arguments("Build system configuration")]
    [Arguments("Activate Home Manager")]
    public async Task A_failure_prevents_later_activation_and_cleanup(string failure)
    {
        var runner = new FakeRunner((command, _, _) => command.Label.English == failure
            ? throw new ManagerException(DisplayText.Plain("Test failure"))
            : Task.FromResult(new CommandResult(0, "", "")));
        await using var service = Service(runner);

        var operation = await service.Start(Switch(cleanup: true)).Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(operation.Error?.English).IsEqualTo("Test failure");
        await Assert.That(operation.Steps!.Any(s => s.Status == StepStatuses.Failed)).IsTrue();
        await Assert.That(operation.Steps!.Any(s => s.Status == StepStatuses.Skipped)).IsTrue();
        await Assert.That(runner.Calls[^1].Label.English).IsEqualTo(failure);
        await Assert.That(runner.Calls.Any(c => c.Label.English.StartsWith("Activate system") || c.Label.English.StartsWith("Keep"))).IsFalse();
        await Assert.That(Directory.Exists(Paths.BuildRoot(Config))).IsFalse();
        await Assert.That(new OperationStore(Config).History()[0].Status).IsEqualTo(OperationStatuses.Failed);
    }

    [Test]
    public async Task A_failure_keeps_its_explanation()
    {
        var runner = new FakeRunner((_, _, _) => throw new ManagerException(DisplayText.Plain("Build failed (exit 1). error: No space left on device"),
            Explain.Explainer.Explain("No space left on device")));
        await using var service = Service(runner);

        var operation = await service.Start(Request.Create() with { Action = Actions.Build, Host = "test" }).Completion;

        await Assert.That(operation.Explanation!.Title.English).IsEqualTo("The disk is full");
    }

    [Test]
    public async Task A_dry_run_runs_nothing_and_logs_each_command()
    {
        var runner = new FakeRunner((_, _, _) => throw new InvalidOperationException("Must not run"));
        await using var service = Service(runner);

        var operation = await service.Start(Switch(cleanup: true) with { Update = true, DryRun = true }).Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(operation.Step.English).IsEqualTo("Preview complete");
        await Assert.That(runner.Calls).IsEmpty();
        await Assert.That(service.Log(operation.Id).Text).Contains("nix flake update --flake");
    }

    [Test]
    public async Task The_lock_excludes_other_managers_and_is_released_afterwards()
    {
        await using var service = Service(new FakeRunner());
        using (new OperationStore(Config).AcquireLock())
        {
            await Assert.That(() => service.Start(Switch())).Throws<ManagerException>().WithMessageContaining("Another nix-manager operation is running");
        }
        await service.Start(Switch()).Completion;
        new OperationStore(Config).AcquireLock().Dispose();
    }

    [Test]
    public async Task Cancellation_stops_a_running_command_and_releases_the_lock()
    {
        var entered = new TaskCompletionSource();
        var runner = new FakeRunner(async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new CommandResult(0, "", "");
        });
        await using var service = Service(runner);
        var run = service.Start(Switch());
        await entered.Task;

        service.Cancel();
        var operation = await run.Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Cancelled);
        await Assert.That(operation.Error?.English).IsEqualTo("Cancelled. Any completed activation or input update remains applied.");
        await Assert.That(operation.Steps![0].Status).IsEqualTo(StepStatuses.Cancelled);
        await Assert.That(operation.Steps!.Skip(1).All(s => s.Status == StepStatuses.Skipped)).IsTrue();
        await Assert.That(service.Busy).IsFalse();
        new OperationStore(Config).AcquireLock().Dispose();
    }

    [Test]
    public async Task Cancellation_waits_for_a_running_privileged_step_and_prevents_cleanup()
    {
        var entered = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var runner = new FakeRunner(async (command, _, _) =>
        {
            if (command.Argv.Contains("--privileged") && command.Argv.Contains("activate-output"))
            {
                // The helper runs as root: cancellation cannot stop it.
                entered.TrySetResult();
                await finish.Task;
            }
            return new CommandResult(0, "", "");
        });
        await using var service = Service(runner);
        var run = service.Start(Switch(Scopes.System, cleanup: true));
        await entered.Task;
        await Assert.That(service.Active!.FinishingPrivilegedStep == true).IsTrue();

        service.Cancel();
        await Assert.That(service.Active!.Step.English).IsEqualTo("Stopping after privileged step");
        await Task.Delay(20);
        await Assert.That(service.Busy).IsTrue();
        await Assert.That(() => new OperationStore(Config).AcquireLock()).Throws<ManagerException>();
        finish.SetResult();
        var operation = await run.Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Cancelled);
        await Assert.That(runner.Calls.Count).IsEqualTo(2);
        await Assert.That(runner.Calls.Any(c => c.Argv.Contains("gc"))).IsFalse();
        await Assert.That(operation.Steps!.Single(s => s.Label.English == "Activate system").Status).IsEqualTo(StepStatuses.Succeeded);
    }

    [Test]
    public async Task Check_saves_the_report_and_fails_when_a_check_failed()
    {
        var report = new UpdateReport
        {
            CheckedAt = "2026-10-15T08:04:43.249Z",
            Items = [new UpdateItem { Name = "nixpkgs", Current = "7fd4004d0511", Latest = "55bfb4d7437b", Kind = "input" }],
            Errors = [DisplayText.Plain("pince: Fetch failed: HTTP 403")],
        };
        var runner = new FakeRunner();
        await using var service = Service(runner, new FakeUpdates(report));

        var operation = await service.Start(Request.Create() with { Action = Actions.Check, Host = "test" }).Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(operation.Error?.English).IsEqualTo("Some update checks failed. The report contains the successful results and errors.");
        await Assert.That(operation.Steps!.Select(s => s.Label.English)).IsEquivalentTo(["Check inputs and package releases"]);
        await Assert.That(ManagerJson.Serialize(service.Updates())).IsEqualTo(ManagerJson.Serialize(report));
        var log = service.Log(operation.Id).Text;
        await Assert.That(log).Contains("→ Check flake inputs without changing the checkout\n  nix flake update\n");
        await Assert.That(log).Contains("→ nixpkgs: 7fd4004d0511 → 55bfb4d7437b\n");
        await Assert.That(log).Contains("→ 1 updates found; 1 checks failed.\n");
    }

    [Test]
    public async Task A_first_operation_decides_the_preferences_of_a_new_user()
    {
        await using var service = Service(new FakeRunner());
        await service.Start(Request.Create() with { Action = Actions.Build, Host = "test" }).Completion;

        var preferences = JsonNode.Parse(File.ReadAllText(Path.Combine(Config.StateDir, "preferences.json")))!;
        await Assert.That(preferences["detail"]!.GetValue<string>()).IsEqualTo("simple");
        await Assert.That(preferences["history"]!.GetValue<bool>()).IsTrue();
    }
}
