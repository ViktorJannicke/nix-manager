using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Updates;

namespace NixManager.Tests;

/// <summary>Whether the edit engine (<c>Drafts</c>, <c>ChangeRules</c>) is ported, or still its placeholders.</summary>
internal static class EditEngine
{
    public static bool Ported
    {
        get
        {
            try
            {
                Drafts.SourcePath("/nonexistent-flake", "a.nix");
                return true;
            }
            catch (NotImplementedException)
            {
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public static void Require() => Skip.Unless(Ported, "Needs the edit engine of w300-033-edits (Drafts, ChangeRules); it runs once that branch is merged.");
}

/// <summary>
/// Reviews with source edits (<c>test/edits.test.ts</c>): staging, verification, writing on apply, one-step
/// installs, holding, going back with the files and undoing. They need the edit engine's real drafts and are
/// skipped while <c>EditEngineStubs.cs</c> stands in for it.
/// </summary>
public sealed class ReviewEditFlowTests : IDisposable
{
    private const string Apps = "{ pkgs, ... }: {\n  home.packages = with pkgs; [\n    git\n  ];\n}\n";
    private static readonly ProcessCommandRunner Processes = new();
    private readonly string _flake;
    private readonly string _snapshot;
    private readonly string _output;

    public ReviewEditFlowTests()
    {
        _flake = Native.RealPath(TestConfig.TemporaryFlake())!;
        Directory.CreateDirectory(Path.Combine(_flake, "modules"));
        File.WriteAllText(Path.Combine(_flake, "modules/apps.nix"), Apps);
        Directory.CreateDirectory(Path.GetDirectoryName(Config.HomeProfile)!);
        _snapshot = Path.Combine(_flake, "snapshot");
        Directory.CreateDirectory(Path.Combine(_snapshot, "modules"));
        File.Copy(Path.Combine(_flake, "modules/apps.nix"), Path.Combine(_snapshot, "modules/apps.nix"));
        File.WriteAllText(Path.Combine(_snapshot, "flake.nix"), "{}");
        _output = Directory.CreateDirectory(Path.Combine(_flake, "home-output")).FullName;
    }

    public void Dispose() => Directory.Delete(_flake, recursive: true);

    private Config Config => TestConfig.Create(_flake);
    private string AppsNow => File.ReadAllText(Path.Combine(_flake, "modules/apps.nix"));
    private static Change Btop => Change.AddPackage(Targets.Home, "modules/apps.nix", "pkgs.btop");

    private static JsonObject Package(string name) => new() { ["out"] = $"/nix/store/{name}", ["name"] = name, ["version"] = "1", ["description"] = null };

    /// <summary>
    /// A service whose Nix finds btop added to modules/apps.nix by the edits and builds the home configuration;
    /// <paramref name="options"/> answers option reads by source, and the commands <paramref name="fails"/> picks fail.
    /// </summary>
    private OperationService Service(List<Command>? calls = null, Func<Command, bool>? fails = null, Func<string, JsonArray>? options = null, Func<string, JsonArray>? packages = null)
    {
        var runner = new FakeRunner(async (command, output, token) =>
        {
            if (calls is not null) lock (calls) calls.Add(command);
            var argv = command.Argv;
            if (argv[0] == "cp") return await Processes.RunAsync(command, output, token);
            if (fails?.Invoke(command) == true) throw new ManagerException($"{command.Label} failed (exit 1).");
            if (argv.Count > 4 && argv[1] == "flake" && argv[2] == "metadata")
                return new CommandResult(0, JsJson.Stringify(new JsonObject { ["path"] = argv[4] == _flake ? _snapshot : argv[4][5..] }), "");
            if (argv.Count > 3 && argv[1] == "eval")
            {
                var source = argv[3][5..].Split('#')[0];
                var items = source == _snapshot ? new JsonArray(Package("git")) : new JsonArray(Package("git"), Package("btop"));
                var state = new JsonObject
                {
                    ["packages"] = packages?.Invoke(source) ?? new JsonArray(new JsonObject { ["file"] = $"{source}/modules/apps.nix", ["items"] = items }),
                    // An option the edits set; extra entries are ignored when none is asked for.
                    ["options"] = options?.Invoke(source) ?? new JsonArray(new JsonObject
                    {
                        ["exists"] = true,
                        ["value"] = source != _snapshot,
                        ["files"] = source == _snapshot ? new JsonArray() : new JsonArray($"{source}/modules/apps.nix"),
                    }),
                    ["resolved"] = new JsonArray("/nix/store/btop"),
                    ["used"] = new JsonArray(),
                };
                return new CommandResult(0, state.ToJsonString(), "");
            }
            var list = argv.ToList();
            if (list.IndexOf("--out-link") is var link and >= 0) File.CreateSymbolicLink(argv[link + 1], _output);
            return new CommandResult(0, "", "");
        });
        return new OperationService(Config, runner, new FakeUpdates(new UpdateReport { CheckedAt = "" }));
    }

    private static Request Build(string? id = null, string? then = null) =>
        Request.Create() with { Action = Actions.ReviewBuild, ReviewId = id, Scope = Scopes.Home, Edits = true, Then = then };

    private static Request Apply(string id) => Request.Create() with { Action = Actions.ReviewApply, ReviewId = id, Scope = Scopes.Home };

    [Test]
    public async Task Applying_writes_created_files_and_adds_them_to_Git()
    {
        EditEngine.Require();
        var config = Config;
        Directory.CreateDirectory(Path.Combine(_flake, ".git"));
        var source = Path.Combine(Paths.ReviewRoot(config, "abababab"), "source");
        Directory.CreateDirectory(Path.Combine(source, "modules/programs"));
        File.WriteAllText(Path.Combine(source, "modules/programs/fzf.nix"), "{ }\n");
        var edits = new ReviewEdits { Hash = "", Files = [new DraftFile { Path = "modules/programs/fzf.nix", BaseHash = "", Created = true }] };
        var calls = new List<string>();
        Task<CommandResult> Run(Command command)
        {
            calls.Add(string.Join(' ', command.Argv));
            return Task.FromResult(new CommandResult(0, "", ""));
        }

        await SourceEdits.WriteSourcesAsync(config, "abababab", edits, Run);

        await Assert.That(File.ReadAllText(Path.Combine(_flake, "modules/programs/fzf.nix"))).IsEqualTo("{ }\n");
        await Assert.That(calls).IsEquivalentTo([$"git -C {_flake} add --intent-to-add -- modules/programs/fzf.nix"]);
        // A file that appeared meanwhile is not overwritten.
        await Assert.That(() => SourceEdits.WriteSourcesAsync(config, "abababab", edits, Run)).Throws<ManagerException>().WithMessageContaining("was created after");
    }

    [Test]
    public async Task Edited_reviews_build_a_staged_copy_verify_the_edits_and_write_them_on_apply()
    {
        EditEngine.Require();
        var calls = new List<Command>();
        await using var service = Service(calls);
        Drafts.Write(Config, [Btop]);

        var built = await service.Start(Build("eeeeeeee")).Completion;

        await Assert.That(built.Error).IsNull();
        await Assert.That(calls.First(c => c.Argv[1] == "build").Argv).Contains($"path:{Paths.ReviewRoot(Config, "eeeeeeee")}/source#homeConfigurations.user-test.activationPackage");
        // The checkout is untouched until the review is applied.
        await Assert.That(AppsNow).DoesNotContain("btop");
        var review = ReviewStore.List(Config).Single();
        var effect = review.Edits!.Effects.Single();
        await Assert.That(JsonSerializer.Serialize(effect, ManagerJson.Compact.EditEffect)).IsEqualTo(
            """{"scope":"home","added":[{"out":"/nix/store/btop","name":"btop","version":"1","description":null,"file":"modules/apps.nix"}],"removed":[],"options":[]}""");
        await Assert.That(review.Edits.Files.Select(f => f.Path)).IsEquivalentTo(["modules/apps.nix"]);
        await Assert.That(service.Plan(Request.Create() with { Action = Actions.ReviewApply, ReviewId = review.Id }).Steps[0].Internal).IsEqualTo(InternalSteps.WriteSources);
        await Assert.That(service.Log(built.Id).Text).Contains("→ Edited modules/apps.nix\n");
        await Assert.That(service.Log(built.Id).Text).Contains("→ home: adds btop (modules/apps.nix)\n");

        var applied = await service.Start(Apply(review.Id)).Completion;

        await Assert.That(applied.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(AppsNow).Contains("    btop\n");
        await Assert.That(Drafts.Read(Config)).IsEmpty();
    }

    [Test]
    public async Task Installing_an_app_is_prepared_and_applied_in_one_step()
    {
        EditEngine.Require();
        await using var service = Service();
        // The follow-up may end before the first operation's completion is observed; its events tell.
        var followed = new TaskCompletionSource<Operation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = service.Subscribe(e =>
        {
            if (e is OperationChanged { Operation: { Request.Action: Actions.ReviewApply, Status: not OperationStatuses.Running } operation })
                followed.TrySetResult(operation);
        });
        Drafts.Write(Config, [Btop]);

        var built = await service.Start(Build(then: "apply")).Completion;

        await Assert.That(built.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(built.Held).IsNull();
        // Applying follows on its own, for the review just prepared.
        var applied = await followed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That((applied.Request.Action, applied.Request.ReviewId, applied.Request.Activation, applied.Request.Scope))
            .IsEqualTo((Actions.ReviewApply, built.Request.ReviewId, "switch", Scopes.Home));
        await Assert.That(applied.Status).IsEqualTo(OperationStatuses.Succeeded);
        await service.WaitAsync();
        await Assert.That(AppsNow).Contains("    btop\n");
        await Assert.That(Drafts.Read(Config)).IsEmpty();
        await Assert.That(ReviewStore.List(Config)).IsEmpty();
    }

    [Test]
    public async Task A_failed_activation_goes_back_to_the_earlier_generation_and_files()
    {
        EditEngine.Require();
        File.CreateSymbolicLink(Config.HomeProfile, "home-manager-4-link");
        var calls = new List<Command>();
        // The new generation does not activate; the earlier one does.
        await using var service = Service(calls, c => c.Argv[0].EndsWith("/activate", StringComparison.Ordinal) && !c.Argv[0].Contains("-4-link"));
        Drafts.Write(Config, [Btop]);
        await service.Start(Build("abababab")).Completion;
        var before = AppsNow;

        var failed = await service.Start(Apply("abababab")).Completion;

        await Assert.That(failed.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(failed.Rollback).IsEqualTo(new RollbackOutcome { Done = true });
        await Assert.That(failed.Explanation).IsEqualTo(new Explanation
        {
            Title = "Activate Home Manager failed",
            Body = "Activate Home Manager failed (exit 1). Everything went back to how it was before, files included.",
        });
        await Assert.That(failed.Steps!.Select(s => (s.Label, s.Status))).IsEquivalentTo([
            ("Write modules/apps.nix", StepStatuses.Succeeded),
            ("Select Home Manager generation", StepStatuses.Succeeded),
            ("Activate Home Manager", StepStatuses.Failed),
            ("Go back to home generation 4", StepStatuses.Succeeded),
            ("Activate the earlier home generation", StepStatuses.Succeeded),
            ("Put back the configuration files", StepStatuses.Succeeded),
        ]);
        await Assert.That(AppsNow).IsEqualTo(before);
        // The change can be tried again: its pending edit and review are kept.
        await Assert.That(Drafts.Read(Config).Select(c => c.Attr)).IsEquivalentTo(["pkgs.btop"]);
        await Assert.That(ReviewStore.List(Config).Select(r => r.Id)).IsEquivalentTo(["abababab"]);
    }

    [Test]
    public async Task Edits_that_change_settings_wait_for_review_instead_of_being_applied_in_one_step()
    {
        EditEngine.Require();
        await using var service = Service();
        Drafts.Write(Config, [Btop, Change.SetOption(Targets.Home, "modules/apps.nix", "programs.git.enable", JsonValue.Create(true))]);

        var built = await service.Start(Build(then: "apply")).Completion;

        await Assert.That(built.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(built.Held).IsEqualTo("It changes settings, which can restart programs you are using.");
        await Assert.That(service.Active).IsNull();
        await Assert.That(AppsNow).DoesNotContain("btop");
        await Assert.That(ReviewStore.List(Config).Select(r => r.Id)).IsEquivalentTo([built.Request.ReviewId!]);
        await Assert.That(service.Log(built.Id).Text).Contains("→ Waiting for your review: It changes settings, which can restart programs you are using.\n");
    }

    [Test]
    public async Task One_step_edits_can_be_held_for_review_while_they_are_prepared()
    {
        EditEngine.Require();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var service = Service(fails: c =>
        {
            if (c.Label == "Compare with the active configuration" || c.Argv.Contains("path-info"))
            {
                entered.TrySetResult();
                release.Task.Wait();
            }
            return false;
        });
        Drafts.Write(Config, [Btop]);
        var run = service.Start(Build(then: "apply"));
        await entered.Task;

        await Assert.That(service.Hold("not-this-one")).IsFalse();
        await Assert.That(service.Hold(run.Operation.Id)).IsTrue();
        release.SetResult();

        await Assert.That((await run.Completion).Held).IsEqualTo("You chose to review it first.");
        await Assert.That(service.Active).IsNull();
    }

    [Test]
    public async Task A_setting_that_does_not_take_effect_is_reported_before_the_package_it_was_meant_to_allow()
    {
        EditEngine.Require();
        var calls = new List<Command>();
        await using var service = Service(calls,
            options: source => new JsonArray(new JsonObject
            {
                ["exists"] = true,
                ["value"] = false,
                ["files"] = source == _snapshot
                    ? new JsonArray("/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-source/flake.nix")
                    : new JsonArray("/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-source/flake.nix", $"{source}/modules/apps.nix"),
            }),
            packages: _ => []);
        Drafts.Write(Config, [
            Change.AddPackage(Targets.Home, "modules/apps.nix", "pkgs.steam"),
            Change.SetOption(Targets.Home, "modules/apps.nix", "nixpkgs.config.allowUnfree", JsonValue.Create(true)),
        ]);

        var failed = await service.Start(Build("ffffffff")).Completion;

        await Assert.That(failed.Explanation).IsEqualTo(new Explanation
        {
            Title = "nixpkgs.config.allowUnfree is also set elsewhere",
            Body = "A module written directly in flake.nix sets nixpkgs.config.allowUnfree too, and that value wins. Change it there instead of adding a new definition.",
        });
        await Assert.That(failed.Error).IsEqualTo(
            "nixpkgs.config.allowUnfree is false instead of true. Another definition takes precedence, or modules/apps.nix is not part of test's home configuration.");
        await Assert.That(calls.Any(c => c.Argv[^1].EndsWith(".pkgs.steam.outPath", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task An_applied_edit_can_be_undone_which_puts_its_files_back_and_redone()
    {
        EditEngine.Require();
        File.CreateSymbolicLink(Config.HomeProfile, "home-manager-5-link");
        await using var service = Service(options: _ => []);
        var original = AppsNow;
        Drafts.Write(Config, [Btop]);
        await service.Start(Build("aaaaaaaa")).Completion;
        var applied = await service.Start(Apply("aaaaaaaa")).Completion;

        await Assert.That(applied.Undo!.Changes).IsEquivalentTo(["Install btop"]);
        await Assert.That(applied.Undo.Generations).IsEqualTo(new UndoGenerations { Home = 5 });
        await Assert.That(applied.Undo.Files.Single().Path).IsEqualTo("modules/apps.nix");
        var edited = AppsNow;
        await Assert.That(edited).Contains("btop");

        await service.Start(Request.Create() with { Action = Actions.ReviewBuild, ReviewId = "bbbbbbbb", Undo = applied.Id }).Completion;
        var restore = service.Plan(Apply("bbbbbbbb")).Steps[0];
        await Assert.That((restore.Label, restore.Internal)).IsEqualTo(("Restore modules/apps.nix", InternalSteps.RestoreSources));

        // A file edited since is not overwritten.
        File.WriteAllText(Path.Combine(_flake, "modules/apps.nix"), $"{edited}# mine\n");
        var refused = await service.Start(Apply("bbbbbbbb")).Completion;
        await Assert.That(refused.Explanation!.Title).IsEqualTo("modules/apps.nix was edited since");
        await Assert.That(AppsNow).EndsWith("# mine\n");

        File.WriteAllText(Path.Combine(_flake, "modules/apps.nix"), edited);
        await service.Start(Request.Create() with { Action = Actions.ReviewBuild, ReviewId = "cccccccc", Undo = applied.Id }).Completion;
        var undone = await service.Start(Apply("cccccccc")).Completion;
        await Assert.That(undone.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(AppsNow).IsEqualTo(original);
        await Assert.That(undone.Undo!.Changes).IsEquivalentTo(["Undo: Install btop"]);

        await service.Start(Request.Create() with { Action = Actions.ReviewBuild, ReviewId = "dddddddd", Undo = undone.Id }).Completion;
        await Assert.That((await service.Start(Apply("dddddddd")).Completion).Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(AppsNow).IsEqualTo(edited);
    }

    [Test]
    public async Task Staging_refuses_edits_that_are_not_packages_from_pkgs_or_change_nothing()
    {
        EditEngine.Require();
        var config = Config;
        var r = Build("abababab");
        Task<CommandResult> Run(Command command) => Task.FromResult(new CommandResult(0, "", ""));
        await Assert.That(() => SourceEdits.StageSourcesAsync(config, r, [], Run)).Throws<ManagerException>().WithMessage("There are no pending edits.");
        await Assert.That(() => SourceEdits.StageSourcesAsync(config, r, [Change.AddPackage(Targets.Home, "modules/apps.nix", "lib.x")], Run))
            .Throws<ManagerException>().WithMessage("lib.x is not a package from pkgs.");
        await Assert.That(() => SourceEdits.StageSourcesAsync(config, r, [Change.AddPackage(Targets.Home, "modules/apps.nix", "pkgs.git")], Run))
            .Throws<ManagerException>().WithMessageContaining("pkgs.git: ");
    }
}
