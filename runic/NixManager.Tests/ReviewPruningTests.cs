using NixManager.Domain;
using NixManager.Execution;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Updates;

namespace NixManager.Tests;

/// <summary>
/// Obsolete reviews: which ones the rules discard and why, discarding them when operations end and when the service
/// starts, and refusing to apply a review whose checkout changed.
/// </summary>
public sealed class ReviewPruningTests : IDisposable
{
    private const string BaseLock = """{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a","lastModified":1}}}}""";
    private static readonly RepositoryState Checkout = new() { Fingerprint = "now", LockHash = "lock", Revision = "r2" };
    private readonly string _flake;
    private readonly string _home;
    private readonly string _system;

    public ReviewPruningTests()
    {
        _flake = Native.RealPath(TestConfig.TemporaryFlake())!;
        _home = Directory.CreateDirectory(Path.Combine(_flake, "home-output")).FullName;
        _system = Directory.CreateDirectory(Path.Combine(_flake, "system-output")).FullName;
        File.WriteAllText(Path.Combine(_flake, "flake.lock"), BaseLock);
        Directory.CreateDirectory(Path.GetDirectoryName(Config.HomeProfile)!);
    }

    public void Dispose() => Directory.Delete(_flake, recursive: true);

    private Config Config => TestConfig.Create(_flake);

    private OperationService Service(ICommandRunner runner) =>
        new(Config, runner, new FakeUpdates(new UpdateReport { CheckedAt = "2026-10-15T00:00:00.000Z" }));

    /// <summary>A current review of the checkout, prepared at minute <paramref name="minute"/>.</summary>
    private static BuildReview Review(string id, int minute, string source = ReviewSources.Checkout, string scope = Scopes.Home, string host = "test") => new()
    {
        Id = id, Host = host, Scope = scope, CreatedAt = $"2026-10-20T10:{minute:00}:00.000Z", Fingerprint = "now", Source = source,
        Inputs = source == ReviewSources.Inputs ? new ReviewInputs { BaseLockHash = "lock" } : null,
    };

    private static ReviewUndo Undo => new() { OperationId = "1-aaaaaaaa", AppliedAt = "2026-10-20T09:00:00.000Z", Point = new UndoPoint() };

    private static Operation Ran(string action, int minute, string status = OperationStatuses.Succeeded, string? reviewId = null, bool dryRun = false) => new()
    {
        Id = $"{minute}-aaaaaaaa", Flake = "/flake", Status = status, StartedAt = $"2026-10-20T10:{minute:00}:00.000Z",
        FinishedAt = status == OperationStatuses.Running ? null : $"2026-10-20T10:{minute:00}:30.000Z",
        Request = Request.Create() with { Action = action, ReviewId = reviewId, DryRun = dryRun },
    };

    private static Dictionary<string, string> Discarded(IReadOnlyList<BuildReview> reviews, params Operation[] history) =>
        ReviewStore.Obsolete(reviews, Checkout, history).ToDictionary(item => item.Review.Id, item => item.Reason.English);

    [Test]
    public async Task A_review_built_from_a_checkout_that_changed_since_is_obsolete()
    {
        var current = Review("a", 1);
        var changed = Review("b", 2, scope: Scopes.System);
        changed.Fingerprint = "before";
        var committed = Review("c", 3, scope: Scopes.All);
        committed.Revision = "r1";
        var update = Review("d", 4, ReviewSources.Inputs, Scopes.All, host: "other");
        update.Inputs!.BaseLockHash = "older";
        // Written by an earlier version, without the commit.
        var earlier = Review("e", 5, host: "other");

        var discarded = Discarded([current, changed, committed, update, earlier]);

        await Assert.That(discarded.Keys).IsEquivalentTo(["b", "c", "d"]);
        await Assert.That(discarded["b"]).IsEqualTo("The configuration changed after it was prepared.");
        await Assert.That(discarded["c"]).IsEqualTo("The configuration changed after it was prepared.");
        await Assert.That(discarded["d"]).IsEqualTo("flake.lock changed after it was prepared.");
    }

    [Test]
    public async Task A_newer_review_of_the_same_kind_host_and_scope_replaces_an_older_one()
    {
        var older = Review("a", 1, ReviewSources.Inputs, Scopes.All);
        var newer = Review("b", 2, ReviewSources.Inputs, Scopes.All);
        var otherScope = Review("c", 0, ReviewSources.Inputs, Scopes.Home);
        var otherHost = Review("d", 0, ReviewSources.Inputs, Scopes.All, host: "other");
        var otherKind = Review("e", 0, ReviewSources.Checkout, Scopes.All);
        // A newer review that is obsolete itself replaces nothing.
        var edit = Review("f", 3, ReviewSources.Checkout, Scopes.System);
        var staleEdit = Review("g", 4, ReviewSources.Checkout, Scopes.System);
        staleEdit.Fingerprint = "before";

        var obsolete = ReviewStore.Obsolete([staleEdit, edit, newer, older, otherScope, otherHost, otherKind], Checkout, []);

        await Assert.That(obsolete.Select(item => item.Review.Id)).IsEquivalentTo(["a", "g"]);
        var replaced = obsolete.Single(item => item.Review.Id == "a");
        await Assert.That(replaced.Reason.English).IsEqualTo("A newer review of the same kind replaces it.");
        await Assert.That(replaced.SupersededBy).IsEqualTo("b");
    }

    [Test]
    public async Task Going_back_or_undoing_is_obsolete_once_another_change_was_applied_and_not_for_checkout_changes()
    {
        var rollback = Review("a", 10, ReviewSources.Generation);
        rollback.Generation = 4;
        rollback.Fingerprint = "before";
        var undo = Review("b", 10, ReviewSources.Generation, Scopes.All);
        undo.Undo = Undo;
        undo.Fingerprint = "before";

        // The checkout changed, but neither was built from it.
        await Assert.That(Discarded([rollback, undo])).IsEmpty();
        // Changes applied before, failed, previewed, or not activating anything.
        await Assert.That(Discarded([rollback, undo], Ran(Actions.ReviewApply, 5), Ran(Actions.Switch, 20, OperationStatuses.Failed),
            Ran(Actions.ReviewApply, 21, dryRun: true), Ran(Actions.Build, 22), Ran(Actions.ReviewBuild, 23))).IsEmpty();

        foreach (var action in new[] { Actions.ReviewApply, Actions.Switch, Actions.Boot, Actions.Rollback })
        {
            var discarded = Discarded([rollback, undo], Ran(action, 20));
            await Assert.That(discarded.Keys).IsEquivalentTo(["a", "b"]);
            await Assert.That(discarded["a"]).IsEqualTo("Another change was applied after it was prepared.");
        }
    }

    [Test]
    public async Task An_undo_and_going_back_are_kinds_of_their_own()
    {
        var rollback = Review("a", 1, ReviewSources.Generation);
        var undo = Review("b", 2, ReviewSources.Generation);
        undo.Undo = Undo;
        var newerUndo = Review("c", 3, ReviewSources.Generation);
        newerUndo.Undo = Undo;

        var obsolete = ReviewStore.Obsolete([newerUndo, undo, rollback], Checkout, []);

        await Assert.That(obsolete.Select(item => (item.Review.Id, item.SupersededBy))).IsEquivalentTo([("b", (string?)"c")]);
    }

    [Test]
    public async Task A_review_kept_for_the_next_boot_stays_until_a_newer_one_waits_instead()
    {
        var kept = Review("a", 1, ReviewSources.Inputs);
        kept.AfterBoot = new AfterBoot { System = "/nix/store/system-a" };
        kept.Fingerprint = "before";
        kept.Inputs!.BaseLockHash = "older";
        // An ordinary review prepared later does not replace it.
        var later = Review("b", 2, ReviewSources.Inputs);

        await Assert.That(Discarded([later, kept], Ran(Actions.ReviewApply, 30))).IsEmpty();

        var newer = Review("c", 3, ReviewSources.Checkout);
        newer.AfterBoot = new AfterBoot { System = "/nix/store/system-c" };
        var elsewhere = Review("d", 4, ReviewSources.Checkout, host: "other");
        elsewhere.AfterBoot = new AfterBoot { System = "/nix/store/system-d" };
        var obsolete = ReviewStore.Obsolete([elsewhere, newer, later, kept], Checkout, []);
        await Assert.That(obsolete.Select(item => (item.Review.Id, item.SupersededBy))).IsEquivalentTo([("a", (string?)"c")]);
    }

    [Test]
    public async Task A_review_a_running_operation_uses_is_kept()
    {
        var applying = Review("a", 1);
        applying.Fingerprint = "before";
        var older = Review("b", 0);

        var obsolete = ReviewStore.Obsolete([applying, older], Checkout, [Ran(Actions.ReviewApply, 2, OperationStatuses.Running, reviewId: "a")]);

        // Kept though its checkout changed; obsolete all the same, it replaces nothing.
        await Assert.That(obsolete).IsEmpty();
        await Assert.That(ReviewStore.Obsolete([applying, older], Checkout, []).Select(item => item.Review.Id)).IsEquivalentTo(["a"]);
    }

    /// <summary>Stores a review whose GC root links to the home output.</summary>
    private string Store(BuildReview review)
    {
        var root = Paths.ReviewRoot(Config, review.Id);
        Directory.CreateDirectory(root);
        File.CreateSymbolicLink(Path.Combine(root, Scopes.Home), _home);
        review.Home = new ReviewOutput { Path = _home };
        File.WriteAllText(Path.Combine(root, "review.json"), ReviewStore.Stringify(review, indented: true));
        return root;
    }

    private void Prepared(string reviewId) =>
        File.WriteAllText(Path.Combine(Config.StateDir, "background.json"), $$"""{"signature":"[[\"nixpkgs\",\"b\"]]","reviewId":"{{reviewId}}"}""");

    private static Request Build(string id, string scope = Scopes.Home) =>
        Request.Create() with { Action = Actions.ReviewBuild, ReviewId = id, Scope = scope, Inputs = ["nixpkgs"] };

    [Test]
    public async Task An_operation_that_ends_discards_the_reviews_it_left_behind_and_says_why()
    {
        var nix = new ReviewNix(_home, _system);
        await using var service = Service(nix.Runner);
        await service.Start(Build("a1a1a1a1")).Completion;
        var older = Paths.ReviewRoot(Config, "a1a1a1a1");
        await Task.Delay(5);

        var newer = await service.Start(Build("b1b1b1b1")).Completion;

        await Assert.That(ReviewStore.List(Config).Select(review => review.Id)).IsEquivalentTo(["b1b1b1b1"]);
        // The GC roots go with the review; their outputs stay.
        await Assert.That(Directory.Exists(older)).IsFalse();
        await Assert.That(Directory.Exists(_home)).IsTrue();
        await Assert.That(service.Log(newer.Id).Text).Contains("(a1a1a1a1): A newer review of the same kind replaces it.\n");
    }

    [Test]
    public async Task Applying_a_review_whose_checkout_changed_is_refused_and_the_review_discarded()
    {
        var nix = new ReviewNix(_home, _system);
        await using var service = Service(nix.Runner);
        await service.Start(Build("c1c1c1c1") with { Inputs = null }).Completion;
        await Assert.That(ReviewStore.Get(Config, "c1c1c1c1").Source).IsEqualTo(ReviewSources.Checkout);
        File.WriteAllText(Path.Combine(_flake, "flake.lock"), """{"nodes":{"root":{}}}""");
        nix.Calls.Clear();

        var apply = await service.Start(Request.Create() with { Action = Actions.ReviewApply, ReviewId = "c1c1c1c1", Scope = Scopes.Home }).Completion;

        await Assert.That(apply.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(apply.Error!.English)
            .IsEqualTo("The configuration changed after this review was prepared, so applying it would undo newer changes. Prepare it again.");
        await Assert.That(apply.Steps!.All(step => step.Status == StepStatuses.Skipped)).IsTrue();
        await Assert.That(nix.Calls).IsEmpty();
        await Assert.That(ReviewStore.List(Config)).IsEmpty();
        await Assert.That(service.Log(apply.Id).Text).Contains("(c1c1c1c1): The configuration changed after it was prepared.\n");
    }

    [Test]
    public async Task A_review_kept_for_the_next_boot_survives_later_operations()
    {
        var nix = new ReviewNix(_home, _system);
        await using var service = Service(nix.Runner);
        await service.Start(Build("d1d1d1d1", Scopes.All)).Completion;
        await service.Start(Request.Create() with { Action = Actions.ReviewApply, ReviewId = "d1d1d1d1", Scope = Scopes.All, Activation = "boot" }).Completion;
        File.WriteAllText(Path.Combine(_flake, "flake.lock"), BaseLock);

        await service.Start(Request.Create() with { Action = Actions.Check }).Completion;

        await Assert.That(ReviewStore.Get(Config, "d1d1d1d1").AfterBoot).IsNotNull();
    }

    [Test]
    public async Task A_preview_discards_nothing()
    {
        var stale = Review("e1e1e1e1", 1);
        stale.Fingerprint = "before";
        Store(stale);
        await using var service = Service(new FakeRunner());

        await service.Start(Request.Create() with { Action = Actions.Build, Scope = Scopes.Home, DryRun = true }).Completion;

        await Assert.That(ReviewStore.List(Config).Select(review => review.Id)).IsEquivalentTo(["e1e1e1e1"]);
    }

    [Test]
    public async Task Starting_discards_the_reviews_left_from_before_and_keeps_background_json_consistent()
    {
        Directory.CreateDirectory(Config.StateDir);
        var stale = Review("f1f1f1f1", 1, ReviewSources.Inputs, Scopes.All);
        stale.Fingerprint = "before";
        var root = Store(stale);
        Prepared("f1f1f1f1");
        await using var service = Service(new FakeRunner());

        // The daemon resumes after boot when it starts.
        await Assert.That(await service.ResumeAfterBootAsync(_home)).IsNull();

        await Assert.That(Directory.Exists(root)).IsFalse();
        await Assert.That(Directory.Exists(_home)).IsTrue();
        // The updates are prepared again for the checkout as it is now.
        await Assert.That(BackgroundTasks.LastPrepared(Config)).IsNull();

        var checkout = await Repository.StateAsync(_flake);
        var older = Review("a2a2a2a2", 1, ReviewSources.Inputs, Scopes.All);
        var newer = Review("b2b2b2b2", 2, ReviewSources.Inputs, Scopes.All);
        foreach (var review in new[] { older, newer })
        {
            review.Fingerprint = checkout.Fingerprint;
            review.Inputs!.BaseLockHash = checkout.LockHash;
            Store(review);
        }
        Prepared("a2a2a2a2");

        var discarded = await service.DiscardObsoleteReviewsAsync();

        await Assert.That(discarded.Select(item => item.Review.Id)).IsEquivalentTo(["a2a2a2a2"]);
        // Prepared already, by the review that replaced it.
        await Assert.That(BackgroundTasks.LastPrepared(Config)).IsEqualTo(new PreparedUpdates("[[\"nixpkgs\",\"b\"]]", "b2b2b2b2"));
    }

    [Test]
    public async Task Starting_discards_nothing_while_an_operation_runs()
    {
        var stale = Review("a3a3a3a3", 1);
        stale.Fingerprint = "before";
        Store(stale);
        var entered = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        await using var service = Service(new FakeRunner(async (_, _, _) =>
        {
            entered.TrySetResult();
            await finish.Task;
            return new CommandResult(0, "", "");
        }));
        var run = service.Start(Request.Create() with { Action = Actions.Build, Scope = Scopes.Home });
        await entered.Task;

        await Assert.That(await service.DiscardObsoleteReviewsAsync()).IsEmpty();
        await Assert.That(ReviewStore.List(Config)).IsNotEmpty();

        finish.SetResult();
        await run.Completion;
        await Assert.That(ReviewStore.List(Config)).IsEmpty();
    }
}
