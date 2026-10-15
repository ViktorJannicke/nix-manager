using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Operations;

namespace NixManager.Tests;

/// <summary>
/// Operations the Bun version stored still load and display: real records of every kind it wrote (with and
/// without step outcomes, failed, cancelled, reviews, an applied change with an undo point), plus records in the
/// same format for members the real ones lack.
/// </summary>
public sealed class StoreCompatibilityTests : IDisposable
{
    private readonly BunFixtures _fixtures = new();

    public void Dispose() => _fixtures.Dispose();

    private OperationStore Store() => new(TestConfig.Create(_fixtures.Root) with { StateDir = _fixtures.StateDir });

    private IEnumerable<string> RecordFiles() => Directory.EnumerateFiles(_fixtures.Operations, "*.json");

    [Test]
    public async Task Every_record_reads_and_writes_back_unchanged()
    {
        foreach (var file in RecordFiles())
        {
            var original = JsonNode.Parse(File.ReadAllText(file));
            var operation = ManagerJson.Deserialize<Operation>(File.ReadAllText(file));
            await Assert.That(operation).IsNotNull();
            var written = JsonNode.Parse(ManagerJson.Serialize(operation));
            await Assert.That(BunFixtures.Difference(original, written)).IsNull().Because(Path.GetFileName(file));
        }
    }

    [Test]
    public async Task The_history_lists_this_checkouts_records_newest_first()
    {
        var history = Store().History();

        var expected = RecordFiles()
            .Select(file => JsonNode.Parse(File.ReadAllText(file))!)
            .Where(record => record["flake"]!.GetValue<string>() == _fixtures.Root)
            .Select(record => record["id"]!.GetValue<string>())
            .Order(StringComparer.Ordinal).Reverse().ToList();
        await Assert.That(history.Select(o => o.Id)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(history.Any(o => o.Flake != _fixtures.Root)).IsFalse();
    }

    [Test]
    public async Task Records_from_before_step_outcomes_display_without_steps()
    {
        var old = Store().History().Single(o => o.Id == "1788787590681-08b41598-2588-4dad-b6c0-eb62b76163e3");

        await Assert.That(old.Steps).IsNull();
        await Assert.That(old.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(old.Request.Action).IsEqualTo(Actions.Switch);
        await Assert.That(Actions.Label(old.Request.Action)).IsEqualTo("Build and switch");
        await Assert.That(old.FinishingPrivilegedStep == false).IsTrue();
    }

    [Test]
    public async Task A_record_whose_process_ended_while_running_reads_as_interrupted()
    {
        var store = Store();
        var interrupted = store.History().Single(o => o.Id == "1791000000000-7d6a2b1e-9c3f-4e8a-b5d2-1f0e3c4a5b6c");

        await Assert.That(interrupted.Status).IsEqualTo(OperationStatuses.Interrupted);
        await Assert.That(interrupted.Error).IsEqualTo(OperationStore.InterruptedError);
        // The stored record itself is left as it is.
        await Assert.That(store.Read(interrupted.Id)!.Status).IsEqualTo(OperationStatuses.Running);
    }

    [Test]
    public async Task Reviews_undo_points_progress_and_rollbacks_keep_their_details()
    {
        var history = Store().History();
        var apply = history.Single(o => o.Id == "1791817192010-c45c9d14-1d7b-4b9c-81d7-f0ced48b0947");
        await Assert.That(apply.Request.ReviewId).IsEqualTo("2f89612a-fb6f-4e01-bbd6-530c836e1eae");
        await Assert.That(apply.Request.Activation).IsEqualTo("switch");
        await Assert.That(apply.Undo!.Generations).IsEqualTo(new UndoGenerations { Home = 99, System = 81 });
        await Assert.That(apply.Undo.Files[0].Path).IsEqualTo("flake.lock");

        var build = history.Single(o => o.Id == "1791817115229-465fe18a-e7d2-467a-96fc-3369e963e285");
        await Assert.That(build.Steps!.Any(step => step.Progress is { Evaluating: false })).IsTrue();

        var failed = history.Single(o => o.Id == "1791817200000-3a9e5c71-2b84-4d6f-a0c3-9e8d7f6b5a41");
        await Assert.That(failed.Explanation!.Title).IsEqualTo("Activate system failed");
        await Assert.That(failed.Rollback).IsEqualTo(new RollbackOutcome { Done = true });
    }

    [Test]
    public async Task Logs_and_the_update_report_read_as_the_dashboard_shows_them()
    {
        var store = Store();
        var log = store.Log("1789815615691-ff2bd500-9b4a-4668-b76d-8e1de16ec2cf");
        await Assert.That(log.Text).StartsWith("→ Resolve inputs in a temporary copy");
        await Assert.That(log.Truncated).IsFalse();
        await Assert.That(log.End).IsEqualTo(new FileInfo(Path.Combine(_fixtures.Operations, "1789815615691-ff2bd500-9b4a-4668-b76d-8e1de16ec2cf.log")).Length);

        // A tail starts at a whole line.
        var tail = store.Log("1790107522576-f1f493f8-ff91-4054-bbfb-c506dc96ec61", 200);
        await Assert.That(tail.Truncated).IsTrue();
        await Assert.That(tail.Text.Length).IsLessThan(200);
        await Assert.That(File.ReadAllText(Path.Combine(_fixtures.Operations, "1790107522576-f1f493f8-ff91-4054-bbfb-c506dc96ec61.log"))).Contains("\n" + tail.Text);

        var updates = store.Updates();
        await Assert.That(updates!.Items.Select(item => item.Name)).Contains("t3code");
        await Assert.That(updates.Items.Single(item => item.Name == "t3code").Kind).IsEqualTo("package");
    }

    [Test]
    public async Task A_new_record_has_the_members_the_Bun_version_reads()
    {
        var flake = TestConfig.TemporaryFlake();
        try
        {
            var config = TestConfig.Create(flake);
            await using var service = new OperationService(config, new FakeRunner(), new FakeUpdates(new UpdateReport { CheckedAt = "" }));
            var operation = await service.Start(Request.Create() with { Action = Actions.Gc, Scope = Scopes.Home, Host = "test" }).Completion;

            var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(config.StateDir, "operations", $"{operation.Id}.json")))!.AsObject();
            await Assert.That(stored.Select(p => p.Key)).IsEquivalentTo(["id", "pid", "flake", "request", "status", "startedAt", "finishedAt", "step", "steps"]);
            await Assert.That(stored["request"]!.AsObject().Select(p => p.Key))
                .IsEquivalentTo(["action", "scope", "host", "update", "cleanup", "force", "dryRun", "extra", "retention"]);
            await Assert.That(stored["steps"]![0]!.AsObject().Select(p => p.Key)).IsEquivalentTo(["id", "label", "status", "startedAt", "finishedAt"]);
            await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(operation.StartedAt, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$")).IsTrue();
            await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(operation.Id, "^[0-9]{13}-[a-f0-9-]{36}$")).IsTrue();
        }
        finally
        {
            Directory.Delete(flake, recursive: true);
        }
    }
}
