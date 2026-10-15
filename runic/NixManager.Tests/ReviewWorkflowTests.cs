using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Execution;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Updates;

namespace NixManager.Tests;

/// <summary>
/// A fake Nix for review flows: lock proposals, retained outputs and closure comparisons, as the review tests of
/// <c>test/core.test.ts</c> and <c>test/edits.test.ts</c> fake them. <paramref name="fails"/> picks commands that fail.
/// </summary>
internal sealed class ReviewNix(string home, string system, Func<Command, bool>? fails = null)
{
    public const string ProposedLock = """{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"b","lastModified":2,"type":"github","owner":"NixOS","repo":"nixpkgs"}}}}""";

    public List<Command> Calls { get; } = [];

    public FakeRunner Runner => new((command, _, _) =>
    {
        lock (Calls) Calls.Add(command);
        if (fails?.Invoke(command) == true) throw new ManagerException($"{command.Label} failed (exit 1).");
        var argv = command.Argv;
        var lockIndex = argv.ToList().IndexOf("--output-lock-file");
        if (lockIndex >= 0) File.WriteAllText(argv[lockIndex + 1], ProposedLock);
        var link = argv.ToList().IndexOf("--out-link");
        if (link >= 0) File.CreateSymbolicLink(argv[link + 1], command.Label.Contains("system", StringComparison.Ordinal) ? system : home);
        if (argv.Count > 2 && argv[2] == "diff-closures") return Task.FromResult(new CommandResult(0, "firefox: 130.0 → 131.0, +12.0 KiB\n", ""));
        return Task.FromResult(new CommandResult(0, "", ""));
    });
}

/// <summary>
/// The review workflow without the edit engine: reviewed input updates and restore points, applying, going back
/// after a failed apply, undoing an applied change, the next-boot handover, and the planner's review steps.
/// </summary>
public sealed class ReviewWorkflowTests : IDisposable
{
    private const string BaseLock = """{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a","lastModified":1}}}}""";
    private readonly string _flake = TestConfig.TemporaryFlake();
    private readonly string _home;
    private readonly string _system;

    public ReviewWorkflowTests()
    {
        _flake = Native.RealPath(_flake)!;
        _home = Directory.CreateDirectory(Path.Combine(_flake, "home-output")).FullName;
        _system = Directory.CreateDirectory(Path.Combine(_flake, "system-output")).FullName;
        File.WriteAllText(Path.Combine(_flake, "flake.lock"), BaseLock);
        Directory.CreateDirectory(Path.GetDirectoryName(Config.HomeProfile)!);
    }

    private Config Config => TestConfig.Create(_flake);
    private string Lock => File.ReadAllText(Path.Combine(_flake, "flake.lock"));

    public void Dispose() => Directory.Delete(_flake, recursive: true);

    private OperationService Service(ICommandRunner runner) =>
        new(Config, runner, new FakeUpdates(new UpdateReport { CheckedAt = "2026-10-15T00:00:00.000Z" }));

    private static Request Review(string id, string scope = Scopes.All) =>
        Request.Create() with { Action = Actions.ReviewBuild, ReviewId = id, Scope = scope, Inputs = ["nixpkgs"] };

    private static Request Apply(string id, string scope = Scopes.Home, string? activation = null) =>
        Request.Create() with { Action = Actions.ReviewApply, ReviewId = id, Scope = scope, Activation = activation };

    /// <summary>Points the home profile at generation <paramref name="number"/>, as Home Manager links it.</summary>
    private void HomeGeneration(int number) => File.CreateSymbolicLink(Config.HomeProfile, $"home-manager-{number}-link");

    [Test]
    public async Task Reviewed_builds_retain_compare_and_later_apply_the_exact_outputs()
    {
        var nix = new ReviewNix(_home, _system);
        await using var service = Service(nix.Runner);

        var built = await service.Start(Review("aaaaaaaa")).Completion;

        await Assert.That(built.Status).IsEqualTo(OperationStatuses.Succeeded);
        // The checkout's lock is untouched until the review is applied.
        await Assert.That(Lock).Contains("\"rev\":\"a\"");
        await Assert.That(nix.Calls.Where(c => c.Argv[1] == "build").All(c => c.Argv.Contains("--reference-lock-file"))).IsTrue();
        var review = ReviewStore.List(Config).Single();
        await Assert.That(review.Source).IsEqualTo(ReviewSources.Inputs);
        await Assert.That(review.Home!.Path).IsEqualTo(_home);
        await Assert.That(review.System!.Path).IsEqualTo(_system);
        await Assert.That(review.OperationId).IsEqualTo(built.Id);
        var change = review.Inputs!.Changes.Single();
        await Assert.That(change.Name).IsEqualTo("nixpkgs");
        await Assert.That(change.After!.Rev).IsEqualTo("b");
        await Assert.That(change.After.Source).IsEqualTo("NixOS/nixpkgs");
        await Assert.That(review.Inputs.BaseLockHash).IsEqualTo(Repository.HashText(BaseLock));

        var apply = service.Plan(Request.Create() with { Action = Actions.ReviewApply, ReviewId = review.Id });
        await Assert.That(apply.Steps[0].Internal).IsEqualTo(InternalSteps.WriteLock);
        await Assert.That(apply.Steps[1].Argv).Contains(_home);
        await Assert.That(apply.Steps[^1].Argv).Contains(_system);
        var boot = service.Plan(Request.Create() with { Action = Actions.ReviewApply, ReviewId = review.Id, Activation = "boot" });
        await Assert.That(boot.Steps.Select(s => s.Label)).IsEquivalentTo(["Write the reviewed flake.lock", "Activate system on next boot"]);

        var applied = await service.Start(Apply(review.Id)).Completion;

        await Assert.That(applied.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(Lock).Contains("\"rev\":\"b\"");
        // Applied reviews release their GC roots; the profile now references them.
        await Assert.That(ReviewStore.List(Config)).IsEmpty();
    }

    [Test]
    public async Task The_comparison_writes_what_the_Bun_version_writes()
    {
        var nix = new ReviewNix(_home, _system);
        await using var service = Service(nix.Runner);
        await service.Start(Review("abcdef01", Scopes.Home)).Completion;

        var text = File.ReadAllText(Path.Combine(Paths.ReviewRoot(Config, "abcdef01"), "review.json"));
        var stored = JsonNode.Parse(text)!.AsObject();
        await Assert.That(stored.Select(p => p.Key)).IsEquivalentTo(["id", "host", "scope", "createdAt", "fingerprint", "source", "inputs", "home", "operationId"]);
        await Assert.That(stored["home"]!.AsObject().Select(p => p.Key)).IsEquivalentTo(["path", "changes", "compared"]);
        await Assert.That(stored["host"]!.GetValue<string>()).IsEqualTo("test");
        // JSON.stringify(review, null, 2) and a line feed.
        await Assert.That(text).StartsWith("{\n  \"id\": \"abcdef01\",\n");
        await Assert.That(text).EndsWith("\n}\n");
        await Assert.That(nix.Calls.Single(c => c.Label == "Measure closure sizes").Quiet).IsEqualTo(true);
    }

    [Test]
    public async Task A_failed_review_build_removes_its_partial_outputs()
    {
        await using var service = Service(new FakeRunner((_, _, _) => throw new ManagerException("evaluation error")));

        var operation = await service.Start(Review("dddddddd", Scopes.Home) with { Inputs = null }).Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(Directory.Exists(Paths.ReviewRoot(Config, "dddddddd"))).IsFalse();
    }

    [Test]
    public async Task The_reviewed_lock_is_written_only_if_flake_lock_is_unchanged()
    {
        var config = Config;
        File.WriteAllText(Path.Combine(_flake, "flake.lock"), """{"nodes":{"root":{}}}""");
        const string id = "bbbbbbbb";
        Directory.CreateDirectory(Paths.ReviewRoot(config, id));
        File.WriteAllText(Path.Combine(Paths.ReviewRoot(config, id), "flake.lock"), """{"nodes":{"root":{},"nixpkgs":{}}}""");
        var review = new BuildReview
        {
            Id = id, Host = "test", Scope = Scopes.Home, CreatedAt = Timestamps.Now(), Source = ReviewSources.Inputs,
            Fingerprint = (await Repository.StateAsync(_flake)).Fingerprint,
            Inputs = new ReviewInputs { Names = ["nixpkgs"], BaseLockHash = Repository.HashText("""{"nodes":{"root":{}}}""") },
        };
        File.WriteAllText(Path.Combine(Paths.ReviewRoot(config, id), "review.json"), ReviewStore.Stringify(review, indented: false));
        await Assert.That(ReviewStore.Get(config, id).Id).IsEqualTo(id);

        ReviewStore.WriteReviewedLock(config, review);

        await Assert.That(Lock).Contains("nixpkgs");
        await Assert.That(() => ReviewStore.WriteReviewedLock(config, review)).Throws<ManagerException>()
            .WithMessage("flake.lock changed after this review was prepared. Prepare the update again.");
        await Assert.That(() => ReviewStore.Get(config, "../x")).Throws<ManagerException>().WithMessage("Invalid review.");
        await Assert.That(() => ReviewStore.Get(config, "cccccccc")).Throws<ManagerException>().WithMessage("The selected review is unavailable.");
    }

    [Test]
    public async Task A_written_file_keeps_its_mode_and_a_new_one_is_readable()
    {
        var path = Path.Combine(_flake, "flake.lock");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        ReviewStore.AtomicWrite(path, "{}", ReviewStore.ModeOf(path));
        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await Assert.That(ReviewStore.ModeOf(Path.Combine(_flake, "new.nix"))).IsEqualTo(0b110_100_100);
        ReviewStore.AtomicWrite(Path.Combine(_flake, "private.json"), "{}");
        await Assert.That(File.GetUnixFileMode(Path.Combine(_flake, "private.json"))).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(Directory.EnumerateFiles(_flake, "*.tmp")).IsEmpty();
    }

    [Test]
    public async Task A_generation_review_retains_the_selected_generation()
    {
        var config = Config;
        var rollback = Planner.Plan(config, Request.Create() with { Action = Actions.ReviewBuild, Scope = Scopes.Home, Generation = 1, ReviewId = "cccccccc" });
        await Assert.That(rollback.Steps[0].Label).IsEqualTo("Retain home generation 1");
        await Assert.That(rollback.Steps[0].Argv).Contains($"{config.HomeProfile}-1-link");
        await Assert.That(rollback.Steps[0].Argv).Contains($"{Paths.ReviewRoot(config, "cccccccc")}/home");
        await Assert.That(rollback.Steps[^1].Internal).IsEqualTo(InternalSteps.Compare);
        await Assert.That(rollback.Notes).IsEquivalentTo(["Nothing is activated. The build is kept until you apply or discard it."]);
        var system = Planner.Plan(config, Request.Create() with { Action = Actions.ReviewBuild, Scope = Scopes.System, Generation = 79, ReviewId = "cccccccc" });
        await Assert.That(system.Steps[0].Argv).Contains("/nix/var/nix/profiles/system-79-link");
        await Assert.That(() => Planner.Plan(config, Request.Create() with { Action = Actions.ReviewBuild, Generation = 1, ReviewId = "cccccccc" }))
            .Throws<ManagerException>().WithMessageContaining("either");
    }

    [Test]
    public async Task A_new_review_gets_an_ID_and_names_its_copy_of_edited_files()
    {
        var config = Config;
        await using var service = Service(new FakeRunner());
        var plan = service.Plan(Request.Create() with { Action = Actions.ReviewBuild, Scope = Scopes.Home });
        await Assert.That(plan.Steps[0].Argv.Single(a => a.Contains('#'))).StartsWith($"{config.Flake}#homeConfigurations");
        await Assert.That(plan.Steps[0].Argv).Contains(a => System.Text.RegularExpressions.Regex.IsMatch(a, "/reviews/[a-f0-9-]{36}/home$"));
        var edits = Planner.Plan(config, Request.Create() with { Action = Actions.ReviewBuild, ReviewId = "ffffffff", Edits = true });
        await Assert.That(edits.Steps.Take(2).Select(s => s.Internal)).IsEquivalentTo([InternalSteps.StageSources, InternalSteps.VerifyEdits]);
        await Assert.That(edits.Steps[2].Argv).Contains($"path:{Paths.ReviewRoot(config, "ffffffff")}/source#homeConfigurations.user-test.activationPackage");
        await Assert.That(edits.Notes).Contains("Edits are built from a copy of the checkout. Your files change only when you apply this review.");
        await Assert.That(() => Planner.Plan(config, Request.Create() with { Action = Actions.Build, Edits = true })).Throws<ManagerException>().WithMessageContaining("reviews");
        await Assert.That(() => Planner.Plan(config, Request.Create() with { Action = Actions.ReviewBuild, Scope = Scopes.Home, Generation = 1, Edits = true }))
            .Throws<ManagerException>().WithMessageContaining("generation");
        var all = Planner.Plan(config, Request.Create() with { Action = Actions.ReviewBuild, ReviewId = "ffffffff", Update = true });
        await Assert.That(all.Steps[0].Label).IsEqualTo("Resolve all flake inputs");
        await Assert.That(all.Steps[0].Argv).IsEquivalentTo(["nix", "flake", "update", "--flake", config.Flake, "--output-lock-file",
            $"{Paths.ReviewRoot(config, "ffffffff")}/flake.lock", "--log-format", "internal-json"]);
    }

    [Test]
    [Arguments(Scopes.All, true, false)]
    [Arguments(Scopes.Home, false, false)]
    [Arguments(Scopes.Home, true, true)]
    public async Task Only_edits_of_the_home_configuration_are_applied_in_one_step(string scope, bool edits, bool update)
    {
        var r = Request.Create() with { Action = Actions.ReviewBuild, Scope = scope, Edits = edits, Update = update, ReviewId = "abababab", Then = "apply" };
        await Assert.That(() => Planner.Plan(Config, r)).Throws<ManagerException>().WithMessageContaining("one step");
        await Assert.That(() => Planner.Plan(Config, Request.Create() with { Action = Actions.Build, Scope = Scopes.Home, Then = "apply" }))
            .Throws<ManagerException>().WithMessageContaining("one step");
    }

    [Test]
    public async Task The_system_goes_back_before_home_to_exactly_the_recorded_generations()
    {
        var config = Config;
        await Assert.That(Planner.GoBackSteps(config, home: 3, system: 7).Select(s => string.Join(' ', s.Argv))).IsEquivalentTo([
            $"{string.Join(' ', config.Privileged)} activate-generation 7",
            $"nix-env --profile {config.HomeProfile} --switch-generation 3",
            $"{config.HomeProfile}-3-link/activate --driver-version 1",
        ]);
        await Assert.That(Planner.GoBackSteps(config, null, null)).IsEmpty();
    }

    [Test]
    public async Task A_failed_activation_goes_back_to_the_earlier_generation_and_lock()
    {
        HomeGeneration(4);
        // The new generation does not activate; the earlier one does.
        var nix = new ReviewNix(_home, _system, c => c.Argv[0].EndsWith("/activate", StringComparison.Ordinal) && !c.Argv[0].Contains("-4-link"));
        await using var service = Service(nix.Runner);
        await service.Start(Review("abababab", Scopes.Home)).Completion;

        var failed = await service.Start(Apply("abababab")).Completion;

        await Assert.That(failed.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(failed.Rollback).IsEqualTo(new RollbackOutcome { Done = true });
        await Assert.That(failed.Explanation).IsEqualTo(new Explanation
        {
            Title = "Activate Home Manager failed",
            Body = "Activate Home Manager failed (exit 1). Everything went back to how it was before, files included.",
        });
        await Assert.That(nix.Calls.TakeLast(4).Select(c => string.Join(' ', c.Argv.Take(5)))).IsEquivalentTo([
            $"nix-env --profile {Config.HomeProfile} --set {_home}",
            $"{_home}/activate --driver-version 1",
            $"nix-env --profile {Config.HomeProfile} --switch-generation 4",
            $"{Config.HomeProfile}-4-link/activate --driver-version 1",
        ]);
        await Assert.That(failed.Steps!.Select(s => (s.Label, s.Status))).IsEquivalentTo([
            ("Write the reviewed flake.lock", StepStatuses.Succeeded),
            ("Select Home Manager generation", StepStatuses.Succeeded),
            ("Activate Home Manager", StepStatuses.Failed),
            ("Go back to home generation 4", StepStatuses.Succeeded),
            ("Activate the earlier home generation", StepStatuses.Succeeded),
            ("Put back the configuration files", StepStatuses.Succeeded),
        ]);
        await Assert.That(failed.Steps!.Select(s => s.Id).Skip(3)).IsEquivalentTo(
            ["4-go-back-to-home-generation-4", "5-activate-the-earlier-home-generation", "6-put-back-the-configuration-files"]);
        await Assert.That(Lock).IsEqualTo(BaseLock);
        // The change can be tried again: its review is kept.
        await Assert.That(ReviewStore.List(Config).Select(r => r.Id)).IsEquivalentTo(["abababab"]);
        await Assert.That(service.Log(failed.Id).Text).Contains("→ Going back to how things were before.\n");
    }

    [Test]
    public async Task Going_back_that_fails_too_is_reported_and_the_files_go_back_anyway()
    {
        HomeGeneration(4);
        var nix = new ReviewNix(_home, _system, c => c.Argv[0].EndsWith("/activate", StringComparison.Ordinal));
        await using var service = Service(nix.Runner);
        await service.Start(Review("abababab", Scopes.Home)).Completion;

        var failed = await service.Start(Apply("abababab")).Completion;

        await Assert.That(failed.Rollback).IsEqualTo(new RollbackOutcome { Done = false, Error = "Activate the earlier home generation failed (exit 1)." });
        await Assert.That(failed.Explanation!.Body).IsEqualTo(
            "Activate Home Manager failed (exit 1). Going back did not work either: Activate the earlier home generation failed (exit 1). The Restore points page can take you back.");
        await Assert.That(failed.Steps!.TakeLast(3).Select(s => (s.Label, s.Status))).IsEquivalentTo([
            ("Go back to home generation 4", StepStatuses.Succeeded),
            ("Activate the earlier home generation", StepStatuses.Failed),
            ("Put back the configuration files", StepStatuses.Succeeded),
        ]);
        await Assert.That(Lock).IsEqualTo(BaseLock);
    }

    [Test]
    public async Task Without_an_earlier_home_generation_only_the_files_go_back()
    {
        var nix = new ReviewNix(_home, _system, c => c.Argv[0].EndsWith("/activate", StringComparison.Ordinal));
        await using var service = Service(nix.Runner);
        await service.Start(Review("abababab", Scopes.Home)).Completion;

        var failed = await service.Start(Apply("abababab")).Completion;

        await Assert.That(failed.Rollback).IsEqualTo(new RollbackOutcome { Done = false });
        await Assert.That(failed.Explanation!.Body).IsEqualTo(
            "Activate Home Manager failed (exit 1). The files were put back, but there was no earlier restore point of your user to go back to, so the new version stays in use.");
        await Assert.That(Lock).IsEqualTo(BaseLock);
    }

    [Test]
    public async Task An_applied_change_can_be_undone_which_puts_its_files_back_and_redone()
    {
        HomeGeneration(5);
        var nix = new ReviewNix(_home, _system);
        await using var service = Service(nix.Runner);
        await service.Start(Review("aaaaaaaa", Scopes.Home)).Completion;
        var applied = await service.Start(Apply("aaaaaaaa")).Completion;

        await Assert.That(applied.Undo!.Changes).IsEquivalentTo(["Update nixpkgs"]);
        await Assert.That(applied.Undo.Generations).IsEqualTo(new UndoGenerations { Home = 5 });
        var written = applied.Undo.Files.Single();
        await Assert.That((written.Path, written.Created, written.Written)).IsEqualTo(("flake.lock", false, Repository.HashText(ReviewNix.ProposedLock)));
        // The earlier contents are kept next to the operation, as JSON.stringify(contents, null, 2) writes them.
        var undoFile = Path.Combine(Config.StateDir, "operations", $"{applied.Id}.undo");
        await Assert.That(File.ReadAllText(undoFile)).IsEqualTo($"{{\n  \"flake.lock\": {Nix.JsJson.Quote(BaseLock)}\n}}\n");
        var edited = Lock;

        // Undoing prepares the generation that was active before, for review.
        await Assert.That((await service.Start(Request.Create() with { Action = Actions.ReviewBuild, ReviewId = "bbbbbbbb", Undo = applied.Id }).Completion).Status)
            .IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(nix.Calls.Any(c => c.Argv.Contains($"{Config.HomeProfile}-5-link"))).IsTrue();
        var review = ReviewStore.Get(Config, "bbbbbbbb");
        await Assert.That(review.Undo!.OperationId).IsEqualTo(applied.Id);
        await Assert.That(review.Scope).IsEqualTo(Scopes.Home);
        await Assert.That(review.Source).IsEqualTo(ReviewSources.Generation);
        var restore = service.Plan(Apply("bbbbbbbb")).Steps[0];
        await Assert.That((restore.Label, restore.Internal)).IsEqualTo(("Restore flake.lock", InternalSteps.RestoreSources));

        // A file edited since is not overwritten.
        File.WriteAllText(Path.Combine(_flake, "flake.lock"), edited + "\n");
        var refused = await service.Start(Apply("bbbbbbbb")).Completion;
        await Assert.That(refused.Explanation!.Title).IsEqualTo("flake.lock was edited since");
        await Assert.That(Lock).EndsWith("}\n");

        File.WriteAllText(Path.Combine(_flake, "flake.lock"), edited);
        await service.Start(Request.Create() with { Action = Actions.ReviewBuild, ReviewId = "cccccccc", Undo = applied.Id }).Completion;
        var undone = await service.Start(Apply("cccccccc")).Completion;
        await Assert.That(undone.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(Lock).IsEqualTo(BaseLock);
        await Assert.That(undone.Undo!.Changes).IsEquivalentTo(["Undo: Update nixpkgs"]);

        // The undo is itself a change that can be undone.
        await service.Start(Request.Create() with { Action = Actions.ReviewBuild, ReviewId = "dddddddd", Undo = undone.Id }).Completion;
        await Assert.That((await service.Start(Apply("dddddddd")).Completion).Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(Lock).IsEqualTo(edited);
    }

    [Test]
    public async Task A_change_without_an_undo_point_cannot_be_undone()
    {
        await using var service = Service(new ReviewNix(_home, _system).Runner);
        var build = await service.Start(Request.Create() with { Action = Actions.Build, Scope = Scopes.Home }).Completion;
        await Assert.That(() => service.Plan(Request.Create() with { Action = Actions.ReviewBuild, Undo = build.Id }))
            .Throws<ManagerException>().WithMessage("This change cannot be undone: it recorded nothing to go back to.");
    }

    [Test]
    public async Task A_review_applied_on_next_boot_applies_its_home_part_once_the_computer_runs_that_system()
    {
        var nix = new ReviewNix(_home, _system);
        await using var service = Service(nix.Runner);
        await service.Start(Review("a1a1a1a1")).Completion;

        var boot = await service.Start(Apply("a1a1a1a1", Scopes.All, "boot")).Completion;

        await Assert.That(boot.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(boot.Steps!.Select(s => s.Label)).IsEquivalentTo(["Write the reviewed flake.lock", "Activate system on next boot"]);
        // Applied on next boot, a change is not undoable.
        await Assert.That(boot.Undo).IsNull();
        var kept = ReviewStore.Get(Config, "a1a1a1a1");
        await Assert.That(kept.Scope).IsEqualTo(Scopes.Home);
        await Assert.That(kept.AfterBoot!.System).IsEqualTo(_system);
        await Assert.That(kept.System).IsNull();
        await Assert.That(kept.Inputs!.BaseLockHash).IsEqualTo(Repository.HashText(ReviewNix.ProposedLock));
        // keepForBoot writes JSON.stringify(kept): one line, afterBoot last.
        var text = File.ReadAllText(Path.Combine(Paths.ReviewRoot(Config, "a1a1a1a1"), "review.json"));
        await Assert.That(text).DoesNotContain("\n");
        await Assert.That(text).EndsWith($",\"afterBoot\":{{\"system\":{Nix.JsJson.Quote(_system)}}}}}");

        // Another system booted: nothing to do.
        await Assert.That(await service.ResumeAfterBootAsync(_home)).IsNull();
        var booted = Path.Combine(_flake, "booted-system");
        File.CreateSymbolicLink(booted, _system);
        var resumed = await service.ResumeAfterBootAsync(booted);
        var home = await resumed!.Completion;

        await Assert.That(home.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(home.Request.Scope).IsEqualTo(Scopes.Home);
        await Assert.That(home.Request.Activation).IsEqualTo("switch");
        await Assert.That(home.Steps!.Select(s => s.Label)).IsEquivalentTo(["Write the reviewed flake.lock", "Select Home Manager generation", "Activate Home Manager"]);
        await Assert.That(ReviewStore.List(Config)).IsEmpty();
        await Assert.That(await service.ResumeAfterBootAsync(booted)).IsNull();
    }

    [Test]
    public async Task Hold_applies_only_to_a_one_step_operation_that_runs()
    {
        await using var service = Service(new FakeRunner());
        await Assert.That(service.Hold("1-aaaaaaaa")).IsFalse();
    }

    [Test]
    public async Task Reviews_are_listed_newest_first_and_a_review_in_use_is_kept()
    {
        var entered = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var nix = new ReviewNix(_home, _system);
        var first = true;
        var runner = new FakeRunner(async (command, output, token) =>
        {
            if (command.Argv.Contains("--set") && first)
            {
                first = false;
                entered.TrySetResult();
                await finish.Task;
            }
            return await nix.Runner.RunAsync(command, output, token);
        });
        await using var service = Service(runner);
        var reviews = new ReviewService(service);
        await service.Start(Review("a2a2a2a2", Scopes.Home)).Completion;
        await Task.Delay(5);
        await service.Start(Review("b2b2b2b2", Scopes.Home)).Completion;
        await Assert.That(reviews.Reviews().Select(r => r.Id)).IsEquivalentTo(["b2b2b2b2", "a2a2a2a2"]);

        var apply = service.Start(Apply("a2a2a2a2"));
        await entered.Task;
        await Assert.That(() => reviews.Remove("a2a2a2a2")).Throws<ManagerException>().WithMessage("This review is in use by the running operation.");
        reviews.Remove("b2b2b2b2");
        finish.SetResult();
        await apply.Completion;

        await Assert.That(reviews.Reviews()).IsEmpty();
        await Assert.That(() => reviews.Get("a2a2a2a2")).Throws<ManagerException>().WithMessage("The selected review is unavailable.");
    }

    [Test]
    public async Task A_dry_run_of_a_review_logs_its_steps_and_keeps_nothing()
    {
        await using var service = Service(new FakeRunner((_, _, _) => throw new InvalidOperationException("Must not run")));
        var operation = await service.Start(Review("e1e1e1e1") with { DryRun = true }).Completion;
        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(service.Log(operation.Id).Text).Contains("→ (performed by Nix Manager)\n");
        await Assert.That(File.Exists(Path.Combine(Paths.ReviewRoot(Config, "e1e1e1e1"), "review.json"))).IsFalse();
    }

    [Test]
    public async Task Attribute_paths_split_on_dots_outside_quotes_and_applied_edits_leave_the_draft()
    {
        await Assert.That(SourceEdits.SplitAttr("pkgs.kdePackages.kate")).IsEquivalentTo(["pkgs", "kdePackages", "kate"]);
        await Assert.That(SourceEdits.SplitAttr("pkgs.\"7zz\"")).IsEquivalentTo(["pkgs", "7zz"]);
        await Assert.That(SourceEdits.SplitAttr("pkgs..x")).IsNull();
        await Assert.That(SourceEdits.SplitAttr("pkgs.${x}")).IsNull();
        await Assert.That(SourceEdits.SplitAttr("pkgs.x\n")).IsNull();
        var btop = Change.AddPackage(Targets.Home, "modules/apps.nix", "pkgs.btop");
        await Assert.That(SourceEdits.Remaining([btop, btop], [btop]).Select(c => c.ToString())).IsEquivalentTo([btop.ToString()]);
        await Assert.That(SourceEdits.UnreadOptions()).Contains("hardware.nvidia.open");
    }
}
