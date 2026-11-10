using NixManager.Domain;
using NixManager.Edits;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Reviews;

namespace NixManager.Tests;

/// <summary>
/// The operation queue (<see cref="OperationService.Submit"/>): requests asked for while another runs wait their turn,
/// a failure holds what was asked after it, and the daemon's queue continues in its next process.
/// </summary>
public sealed class QueueTests : IDisposable
{
    private readonly string _flake = TestConfig.TemporaryFlake();
    // Commands wait while the gate is closed; a build of the home configuration fails while _failHome is set.
    private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _failHome;

    private Config Config => TestConfig.Create(_flake);

    public void Dispose() => Directory.Delete(_flake, recursive: true);

    private OperationService Service() => new(Config, new FakeRunner(async (command, _, token) =>
    {
        await _gate.Task.WaitAsync(token);
        if (_failHome && command.Label.English == "Build home configuration")
            throw new ManagerException(DisplayText.Plain("Build home configuration failed (exit 1)."));
        return new CommandResult(0, "", "");
    }), new FakeUpdates(new UpdateReport { CheckedAt = "" }));

    private static Request Build(string scope) => Request.Create() with { Action = Actions.Build, Scope = scope, Host = "test" };

    private static Request Clean => Request.Create() with { Action = Actions.Gc, Scope = Scopes.Home, Host = "test" };

    /// <summary>The actions of the history, oldest first.</summary>
    private static string Ran(IOperationService service) =>
        string.Join(" ", service.History().Reverse().Select(operation => $"{operation.Request.Action}:{operation.Request.Scope}:{operation.Status}"));

    [Test]
    public async Task A_request_runs_at_once_when_nothing_runs_and_waits_its_turn_otherwise()
    {
        await using var service = Service();

        var first = service.Submit(Build(Scopes.Home));
        var second = service.Submit(Build(Scopes.System));
        // The same request again is queued once.
        var again = service.Submit(Build(Scopes.System));
        var third = service.Submit(Clean);

        await Assert.That(first.Started?.Request.Scope).IsEqualTo(Scopes.Home);
        await Assert.That(first.Queued).IsNull();
        await Assert.That(second.Queued).IsNotNull();
        await Assert.That(again.Queued?.Id).IsEqualTo(second.Queued!.Id);
        await Assert.That(service.Queue.Select(entry => entry.Id)).IsEquivalentTo([second.Queued.Id, third.Queued!.Id]);
        await Assert.That(service.Pending).IsTrue();

        _gate.SetResult();
        await service.IdleAsync();

        await Assert.That(Ran(service)).IsEqualTo("build:home:succeeded build:system:succeeded gc:home:succeeded");
        await Assert.That(service.Queue).IsEmpty();
        // Each queued operation names the entry it ran for.
        await Assert.That(service.History().Count(operation => operation.Request.Queued is not null)).IsEqualTo(3);
    }

    [Test]
    public async Task A_failure_holds_what_was_asked_after_it_while_later_requests_still_run()
    {
        _failHome = true;
        await using var service = Service();
        service.Submit(Build(Scopes.Home));
        var held = service.Submit(Build(Scopes.System)).Queued!;

        _gate.SetResult();
        await service.IdleAsync();

        await Assert.That(Ran(service)).IsEqualTo("build:home:failed");
        var waiting = service.Queue.Single();
        await Assert.That(waiting.Id).IsEqualTo(held.Id);
        await Assert.That(waiting.Held!.English).IsEqualTo("Not started, because Build configuration failed before it.");
        await Assert.That(service.Pending).IsFalse();

        // Something asked for afterwards runs; the held entry keeps waiting.
        var later = service.Submit(Clean);
        await Assert.That(later.Started).IsNotNull();
        await service.IdleAsync();
        await Assert.That(service.Queue.Single().Id).IsEqualTo(held.Id);

        service.ContinueQueue();
        await service.IdleAsync();
        await Assert.That(Ran(service)).IsEqualTo("build:home:failed gc:home:succeeded build:system:succeeded");
        await Assert.That(service.Queue).IsEmpty();
    }

    [Test]
    public async Task Stopping_a_queued_operation_holds_the_rest_and_entries_can_be_removed()
    {
        await using var service = Service();
        service.Submit(Build(Scopes.Home));
        var removed = service.Submit(Build(Scopes.System)).Queued!;
        var kept = service.Submit(Clean).Queued!;
        await Assert.That(service.Unqueue(removed.Id)).IsTrue();
        await Assert.That(service.Unqueue(removed.Id)).IsFalse();

        service.Cancel();
        await service.IdleAsync();

        await Assert.That(Ran(service)).IsEqualTo("build:home:cancelled");
        await Assert.That(service.Queue.Single().Id).IsEqualTo(kept.Id);
        await Assert.That(service.Queue.Single().Held!.English).IsEqualTo("Not started, because Build configuration was stopped before it.");
        service.ClearQueue();
        await Assert.That(service.Queue).IsEmpty();
    }

    [Test]
    public async Task The_queue_continues_in_the_next_process_unless_an_operation_ended_with_it()
    {
        await using (var first = Service())
        {
            first.RunQueue();
            first.Submit(Build(Scopes.Home));
            first.Submit(Clean);
            // The process ends while the build runs: it is stopped, and what follows it waits.
            await Assert.That(File.Exists(Path.Combine(Config.StateDir, "queue.json"))).IsTrue();
        }
        await using (var second = Service())
        {
            second.RunQueue();
            var waiting = second.Queue.Single();
            await Assert.That(waiting.Request.Action).IsEqualTo(Actions.Gc);
            await Assert.That(waiting.Held).IsNotNull();
            await Assert.That(second.Busy).IsFalse();
            second.ContinueQueue();
            _gate.SetResult();
            await second.IdleAsync();
        }
        await using (var third = Service())
        {
            // Nothing is left, and nothing runs again.
            third.RunQueue();
            await Assert.That(third.Queue).IsEmpty();
            await Assert.That(third.Busy).IsFalse();
        }
    }

    [Test]
    public async Task A_queue_written_by_an_ended_process_holds_after_an_entry_without_a_successful_operation()
    {
        Directory.CreateDirectory(Config.StateDir);
        var entry = new QueuedOperation { Id = Guid.NewGuid().ToString("D"), Request = Clean, QueuedAt = Timestamps.Now() };
        OperationStore.AtomicJson(Path.Combine(Config.StateDir, "queue.json"),
            ManagerJson.Serialize(new SavedQueue { Entries = [entry], Running = Guid.NewGuid().ToString("D") }));
        _gate.SetResult();
        await using var service = Service();

        service.RunQueue();

        await Assert.That(service.Busy).IsFalse();
        await Assert.That(service.Queue.Single().Held!.English).IsEqualTo("Not started, because Nix Manager ended while a step before it ran.");
    }

    [Test]
    public async Task A_queued_request_waits_for_another_process_s_operation()
    {
        _gate.SetResult();
        await using var service = Service();
        using (new OperationStore(Config).AcquireLock())
        {
            var submitted = service.Submit(Clean);
            await Assert.That(submitted.Started).IsNull();
            await Assert.That(service.Queue.Single().Held).IsNull();
            await Assert.That(service.Pending).IsTrue();
        }
        // The queue tries again once the other process is done.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.IdleAsync().WaitAsync(timeout.Token);
        await Assert.That(Ran(service)).IsEqualTo("gc:home:succeeded");
    }

    [Test]
    public async Task Invalid_requests_are_refused_when_they_are_submitted()
    {
        await using var service = Service();
        await Assert.That(() => service.Submit(Build(Scopes.Home) with { Then = Followups.Switch }))
            .Throws<ManagerException>().WithMessageContaining("Only prepared changes");
        await Assert.That(() => service.Submit(Request.Create() with { Action = Actions.ReviewBuild, Scope = Scopes.Home, Then = Followups.Boot }))
            .Throws<ManagerException>().WithMessageContaining("next boot needs the system");
        await Assert.That(service.Queue).IsEmpty();
    }

    [Test]
    public async Task Prepared_changes_queued_to_be_applied_wait_for_a_review_when_they_need_a_decision()
    {
        BuildReview Review(string host, string? before, string? after) => new()
        {
            Id = "abababab", Host = host, Scope = Scopes.All, CreatedAt = "", Source = ReviewSources.Checkout,
            System = new ReviewOutput { Path = "/nix/store/system", Kernel = new VersionPair { Before = before, After = after } },
        };

        await Assert.That(ChangeRules.QueuedApplyProblem(Review("test", "6.1", "6.1"), "test", Followups.Switch)).IsNull();
        await Assert.That(ChangeRules.QueuedApplyProblem(Review("other", "6.1", "6.1"), "test", Followups.Switch)!.English)
            .IsEqualTo("It was prepared for other, not for this computer.");
        await Assert.That(ChangeRules.QueuedApplyProblem(Review("test", "6.1", "6.2"), "test", Followups.Switch)!.English)
            .IsEqualTo("The kernel changes, so you choose when to restart.");
        // On the next boot the new kernel starts anyway.
        await Assert.That(ChangeRules.QueuedApplyProblem(Review("test", "6.1", "6.2"), "test", Followups.Boot)).IsNull();
    }
}
