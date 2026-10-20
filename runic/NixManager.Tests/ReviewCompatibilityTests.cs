using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Updates;

namespace NixManager.Tests;

/// <summary>
/// Review records shared with the Bun version: the real ones in <c>Fixtures/recorded-state/reviews</c>, those
/// <c>Fixtures/record-bun-reviews.ts</c> recorded with the Bun functions (<c>Fixtures/recorded-reviews</c>), and records
/// written here, which the Bun version reads.
/// </summary>
public sealed class ReviewCompatibilityTests : IDisposable
{
    private readonly RecordedFixtures _fixtures = new();

    public void Dispose() => _fixtures.Dispose();

    private static string Recorded(string name) => Path.Combine(RecordedFixtures.Directory, "recorded-reviews", name, "review.json");

    private static string RepositoryRoot([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [Test]
    public async Task Real_reviews_of_the_Bun_version_load_newest_first_and_write_back_identically()
    {
        var config = TestConfig.Create(_fixtures.Root) with { StateDir = _fixtures.StateDir };
        var reviews = ReviewStore.List(config);

        await Assert.That(reviews.Select(r => r.Id)).IsEquivalentTo(["d0315610-8827-4463-907c-d8ed36ca93f1", "b9d0e728-193b-4bd9-89a2-84b993d35025"]);
        foreach (var review in reviews)
        {
            var text = File.ReadAllText(Path.Combine(_fixtures.StateDir, "reviews", review.Id, "review.json"));
            await Assert.That(ReviewStore.Stringify(ReviewStore.Get(config, review.Id), indented: true) + "\n").IsEqualTo(text);
        }
        var latest = reviews[0];
        await Assert.That(latest.Source).IsEqualTo(ReviewSources.Inputs);
        await Assert.That(latest.System!.Kernel!.After).IsEqualTo("7.2.10");
        await Assert.That(latest.Downloaded).IsEqualTo(12081594264);
        var apply = Planner.Plan(config, Request.Create() with { Action = Actions.ReviewApply, ReviewId = latest.Id, Host = "test" }, latest);
        await Assert.That(apply.Steps.Select(s => s.Label)).IsEquivalentTo(
            ["Write the reviewed flake.lock", "Select Home Manager generation", "Activate Home Manager", "Activate system"]);
    }

    [Test]
    [Arguments("edits")]
    [Arguments("undo")]
    [Arguments("generation")]
    [Arguments("inputs")]
    public async Task Reviews_the_Bun_version_writes_load_and_write_back_identically(string name)
    {
        var text = File.ReadAllText(Recorded(name));
        var review = ManagerJson.Deserialize<BuildReview>(text)!;
        var written = ReviewStore.Stringify(review, indented: true) + "\n";
        // Pending edits are written with their kind, scope and file first, whatever order they were read in.
        if (name == "edits") await Assert.That(RecordedFixtures.Difference(JsonNode.Parse(text), JsonNode.Parse(written))).IsNull();
        else await Assert.That(written).IsEqualTo(text);
    }

    [Test]
    public async Task Pending_edits_of_a_review_read_with_their_members_in_any_order()
    {
        var json = JsonNode.Parse(File.ReadAllText(Recorded("edits")))!;
        foreach (var change in json["edits"]!["changes"]!.AsArray())
        {
            var kind = change!["kind"]!.GetValue<string>();
            change.AsObject().Remove("kind");
            change["kind"] = kind;
            change["unknown"] = 1;
        }
        var review = ManagerJson.Deserialize<BuildReview>(json.ToJsonString())!;
        await Assert.That(review.Edits!.Changes.SequenceEqual([
            new Edits.AddPackage(Edits.Targets.Home, "modules/apps.nix", "pkgs.btop"),
            new Edits.SetOption(Edits.Targets.Home, "modules/apps.nix", "programs.git.enable", JsonValue.Create(true)),
        ])).IsTrue();
    }

    [Test]
    public async Task A_review_kept_for_the_next_boot_loads_and_writes_back_identically()
    {
        var text = File.ReadAllText(Recorded("after-boot"));
        var review = ManagerJson.Deserialize<BuildReview>(text)!;
        await Assert.That(review.AfterBoot!.System).IsEqualTo("/home/user/my-flakes/system-output");
        await Assert.That(review.System).IsNull();
        await Assert.That(ReviewStore.Stringify(review, indented: false)).IsEqualTo(text);
    }

    [Test]
    public async Task The_details_of_recorded_reviews_read_as_the_dashboard_shows_them()
    {
        var edits = ManagerJson.Deserialize<BuildReview>(File.ReadAllText(Recorded("edits")))!;
        await Assert.That(edits.Edits!.Changes.Select(c => c.GetType())).IsEquivalentTo([typeof(Edits.AddPackage), typeof(Edits.SetOption)]);
        await Assert.That(edits.Edits.Effects[0].Added.Single().Name).IsEqualTo("btop");
        await Assert.That(edits.Edits.Effects[0].Options.Single().After.GetBoolean()).IsTrue();
        // Prepared while change lists were hashed in the order their members were read: it no longer matches its
        // own edits, so the draft needs preparing again.
        await Assert.That(edits.Edits.Hash).IsEqualTo("ec5aa5fd906b9124431c8e47713d30adc49af0940e4b8d5772d93274aae4ea99");
        await Assert.That(edits.Edits.Hash).IsNotEqualTo(Edits.Change.Hash(edits.Edits.Changes));
        var undo = ManagerJson.Deserialize<BuildReview>(File.ReadAllText(Recorded("undo")))!;
        await Assert.That(undo.Undo!.Point.Generations.Home).IsEqualTo(5);
        await Assert.That(undo.Source).IsEqualTo(ReviewSources.Generation);
        var generation = ManagerJson.Deserialize<BuildReview>(File.ReadAllText(Recorded("generation")))!;
        await Assert.That(generation.Generation).IsEqualTo(5);
        var inputs = ManagerJson.Deserialize<BuildReview>(File.ReadAllText(Recorded("inputs")))!;
        await Assert.That(inputs.System!.Changes.Select(c => (c.Name, c.Kind))).IsEquivalentTo([("btop", "added"), ("git", "changed")]);
        await Assert.That(inputs.System.Version!.After).IsEqualTo("26.11.20261014.55bfb4d");
    }

    [Test]
    public async Task An_undo_review_is_prepared_from_a_change_the_Bun_version_applied()
    {
        var config = TestConfig.Create(_fixtures.Root) with { StateDir = _fixtures.StateDir };
        var applied = Path.Combine(RecordedFixtures.Directory, "recorded-reviews", "applied");
        foreach (var file in Directory.EnumerateFiles(applied))
            File.WriteAllText(Path.Combine(_fixtures.Operations, Path.GetFileName(file)),
                File.ReadAllText(file).Replace(RecordedFixtures.RecordedFlake, _fixtures.Root, StringComparison.Ordinal));
        var id = Path.GetFileNameWithoutExtension(Directory.EnumerateFiles(applied, "*.json").Single());
        await using var service = new OperationService(config, new FakeRunner(), new FakeUpdates(new UpdateReport { CheckedAt = "" }));

        var plan = service.Plan(Request.Create() with { Action = Actions.ReviewBuild, Undo = id });

        await Assert.That(plan.Steps.Select(s => s.Label)).IsEquivalentTo(["Retain home generation 5", "Compare with the active configuration"]);
        await Assert.That(plan.Notes[0]).IsEqualTo("Applying the undo also restores the configuration files the change wrote.");
        // The Bun version's real .undo contents read too; that change was applied on the host desktop.
        var host = config.Hosts["test"];
        await using var desktop = new OperationService(config with { Hosts = new Dictionary<string, Host> { ["test"] = host, ["desktop"] = host with { NixosHostName = "desktop" } } },
            new FakeRunner(), new FakeUpdates(new UpdateReport { CheckedAt = "" }));
        var real = desktop.Plan(Request.Create() with { Action = Actions.ReviewBuild, Undo = "1791817192010-c45c9d14-1d7b-4b9c-81d7-f0ced48b0947" });
        await Assert.That(real.Steps.Select(s => s.Label)).IsEquivalentTo(
            ["Retain home generation 99", "Retain system generation 81", "Compare with the active configuration"]);
    }
}
