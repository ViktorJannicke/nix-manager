using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Updates;

namespace NixManager.Tests;

/// <summary>
/// A fake Nix for input updates that move the root nixpkgs input under the OpenCode pin, as in my-flakes: the root's
/// nixpkgs is the lock node <c>nixpkgs_2</c>, and the node <c>nixpkgs</c> belongs to another input.
/// </summary>
internal sealed class PinNix(string flake, string home, Func<Command, bool>? fails = null)
{
    public const string OldRev = "02c087f3fe673c9e317f206a627cb6b910336dbe";
    public const string NewRev = "3ec636360000000000000000000000000000000a";
    public static readonly string Hash = "sha256-Izk3QL8xQ6alLvGGAyMUpEDz8kM+FhSD0mev7iWscJs=";
    public static readonly string Evaluated = $"/nix/store/{new string('i', 32)}-opencode-node_modules-2.1.2.drv";
    public static readonly string Resolved = $"/nix/store/{new string('p', 32)}-opencode-node_modules-2.1.2.drv";

    public static string Lock(string rev) => """
        {"nodes":{"nix-manager":{"inputs":{"nixpkgs":"nixpkgs"},"locked":{"rev":"cccccccccccccccccccccccccccccccccccccccc","type":"github"}},
        "nixpkgs":{"locked":{"rev":"151fa4e9b0c0d1a2b3c4d5e6f708192a3b4c5d6e","type":"github","owner":"NixOS","repo":"nixpkgs"}},
        "nixpkgs_2":{"locked":{"rev":"REV","type":"github","owner":"NixOS","repo":"nixpkgs"}},
        "root":{"inputs":{"nix-manager":"nix-manager","nixpkgs":"nixpkgs_2"}}},"root":"root","version":7}
        """.Replace("REV", rev, StringComparison.Ordinal);

    public string Proposed { get; set; } = Lock(NewRev);
    public string NixpkgsVersion { get; set; } = "2.1.1";
    public List<Command> Calls { get; } = [];
    /// <summary>The checkout's pin when each configuration build ran.</summary>
    public List<string> PinAtBuild { get; } = [];

    public FakeRunner Runner => new((command, output, _) =>
    {
        lock (Calls) Calls.Add(command);
        var argv = command.Argv.ToList();
        if (fails?.Invoke(command) == true) throw new ManagerException(DisplayText.Plain($"{command.Label.English} failed (exit 1)."));
        var stdout = "";
        if (argv.IndexOf("--output-lock-file") is var lockIndex and >= 0) File.WriteAllText(argv[lockIndex + 1], Proposed);
        else if (argv is ["nix", "flake", "metadata", ..]) stdout = $$"""{"path":"{{flake}}"}""";
        else if (argv[0] == "cp") Copy(argv[^2], argv[^1]);
        else if (argv[^1].EndsWith(".drvPath", StringComparison.Ordinal)) stdout = Evaluated;
        else if (argv[1] == "eval" && argv[^1].EndsWith(".version", StringComparison.Ordinal)) stdout = NixpkgsVersion;
        else if (argv[1] == "build" && command.AllowFailure == true)
            return Task.FromResult(new CommandResult(1, "",
                $"error: hash mismatch in fixed-output derivation '{Resolved}':\n         specified: {PackageUpdater.FakeHash}\n            got:    {Hash}\n"));
        else if (argv.IndexOf("--out-link") is var link and >= 0)
        {
            PinAtBuild.Add(File.ReadAllText(Path.Combine(flake, "pkgs/opencode/source.json")));
            File.CreateSymbolicLink(argv[link + 1], home);
        }
        return Task.FromResult(new CommandResult(0, stdout, ""));
    });

    /// <summary>The flake's tracked files, as <c>cp -rT</c> copies the flake's source.</summary>
    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in new[] { "flake.nix", "flake.lock" }) File.Copy(Path.Combine(source, file), Path.Combine(target, file), overwrite: true);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "pkgs"), "*", SearchOption.AllDirectories))
        {
            var copied = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
            File.Copy(file, copied, overwrite: true);
        }
    }
}

/// <summary>
/// Input updates revalidate package pins against the new nixpkgs (issue #22): the lock's root nixpkgs input, when a
/// pin is stale, the revalidated pin written with the lock, and the explanation of a hash mismatch on a pin.
/// </summary>
public sealed class PinRevalidationTests : IDisposable
{
    private const string OpenCodePin = """
        {
          "version": "2.1.2",
          "srcHash": "sha256-4/DXi+46Y7PVz6SR+O5KyEfq3Lwg2BCZeevsEtkerDc=",
          "nixpkgsRev": "02c087f3fe673c9e317f206a627cb6b910336dbe",
          "nodeHash": "sha256-uprXt0Vq/albvG20FcrohjJgS1zfRDbq4wi8yCHhGmQ="
        }

        """;

    // The failure from the issue, with the evaluated derivation; Nix may name the resolved one instead.
    private static readonly string IssueLog = """
        error: hash mismatch in fixed-output derivation '/nix/store/pviv3n1nlgjlkl9azkg3xj4qain7psqa-opencode-node_modules-2.1.2.drv':
                 specified: sha256-uprXt0Vq/albvG20FcrohjJgS1zfRDbq4wi8yCHhGmQ=
                    got:    sha256-Izk3QL8xQ6alLvGGAyMUpEDz8kM+FhSD0mev7iWscJs=
        error: 1 dependencies of derivation '/nix/store/0000000000000000000000000000000a-opencode-2.1.2.drv' failed to build
        error: Build failed due to failed dependency
        """;

    private readonly string _flake;
    private readonly string _home;

    public PinRevalidationTests()
    {
        _flake = Native.RealPath(TestConfig.TemporaryFlake())!;
        _home = Directory.CreateDirectory(Path.Combine(_flake, "home-output")).FullName;
        File.WriteAllText(Path.Combine(_flake, "flake.lock"), PinNix.Lock(PinNix.OldRev));
        Directory.CreateDirectory(Path.Combine(_flake, "pkgs/opencode"));
        File.WriteAllText(PinFile, OpenCodePin);
        File.WriteAllText(Path.Combine(_flake, "pkgs/opencode/package.nix"), "{ prev, pin, forceOverride ? false }: prev.opencode\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Config.HomeProfile)!);
    }

    public void Dispose() => Directory.Delete(_flake, recursive: true);

    private Config Config => TestConfig.Create(_flake);
    private string PinFile => Path.Combine(_flake, "pkgs/opencode/source.json");
    private string Lock => File.ReadAllText(Path.Combine(_flake, "flake.lock"));

    private OperationService Service(ICommandRunner runner) =>
        new(Config, runner, new FakeUpdates(new UpdateReport { CheckedAt = "2026-10-25T00:00:00.000Z" }));

    private static Request Review(string id) =>
        Request.Create() with { Action = Actions.ReviewBuild, ReviewId = id, Scope = Scopes.Home, Inputs = ["nixpkgs"] };

    [Test]
    public async Task An_update_of_the_root_nixpkgs_checks_the_pins_before_the_builds()
    {
        var config = Config;
        foreach (var request in new[] { Review("aaaaaaaa"), Review("aaaaaaaa") with { Inputs = null, Update = true } })
        {
            var steps = Planner.Plan(config, request).Steps;
            var check = steps.ToList().FindIndex(step => step.Internal == InternalSteps.RevalidatePins);
            await Assert.That(check).IsEqualTo(1);
            await Assert.That(steps[check].Label.English).IsEqualTo("Check package pins against the new nixpkgs");
            await Assert.That(steps[0].Argv).Contains("--output-lock-file");
            await Assert.That(steps.Skip(check + 1).Any(step => step.Argv is [_, "build", ..])).IsTrue();
        }
        // Other inputs leave the root nixpkgs as it is.
        var other = Planner.Plan(config, Review("aaaaaaaa") with { Inputs = ["home-manager"] });
        await Assert.That(other.Steps.Any(step => step.Internal == InternalSteps.RevalidatePins)).IsFalse();
        await Assert.That(Planner.Plan(config, Request.Create() with { Action = Actions.Switch, Scope = Scopes.Home }).Steps
            .Any(step => step.Internal is InternalSteps.RevalidatePins or InternalSteps.WriteUpdate)).IsFalse();

        // Without a review the lock is resolved beside the checkout, and written with the pins before the builds.
        var update = Planner.Plan(config, Request.Create() with { Action = Actions.Switch, Scope = Scopes.Home, Update = true }).Steps;
        await Assert.That(update.Take(4).Select(step => step.Internal ?? step.Argv[1])).IsEquivalentTo(
            ["flake", InternalSteps.RevalidatePins, InternalSteps.WriteUpdate, "build"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(update[0].Argv).IsEquivalentTo(["nix", "flake", "update", "--flake", config.Flake, "--output-lock-file",
            $"{Paths.BuildRoot(config)}/flake.lock", "--log-format", "internal-json"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(update[3].Argv).Contains($"{config.Flake}#homeConfigurations.user-test.activationPackage");
    }

    [Test]
    public async Task The_root_nixpkgs_input_is_found_through_the_root_node_and_its_follows()
    {
        // .nodes.nixpkgs belongs to nix-manager; the root's nixpkgs is nixpkgs_2.
        await Assert.That(PinRevalidation.RootNixpkgsRevision(PinNix.Lock(PinNix.OldRev))).IsEqualTo(PinNix.OldRev);
        const string follows = """
            {"nodes":{"hm":{"inputs":{"nixpkgs":["upstream","nixpkgs"]}},"upstream":{"inputs":{"nixpkgs":"nixpkgs_3"}},
            "nixpkgs":{"locked":{"rev":"wrong"}},"nixpkgs_3":{"locked":{"rev":"followed"}},
            "base":{"inputs":{"nixpkgs":"nixpkgs"}},
            "top":{"inputs":{"hm":"hm","upstream":"upstream","nixpkgs":["hm","nixpkgs"]}}},"root":"top","version":7}
            """;
        await Assert.That(PinRevalidation.RootNixpkgsRevision(follows)).IsEqualTo("followed");
        foreach (var text in new[]
                 {
                     """{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a"}}}}""",
                     """{"nodes":{"root":{"inputs":{"nixpkgs":["root"]}}}}""",
                     """{"nodes":{"a":{"inputs":{"nixpkgs":["b","nixpkgs"]}},"b":{"inputs":{"nixpkgs":["a","nixpkgs"]}},"root":{"inputs":{"a":"a","b":"b","nixpkgs":["a","nixpkgs"]}}}}""",
                     """{"nodes":{"root":{"inputs":{"nixpkgs":"nixpkgs"}},"nixpkgs":{"locked":{"type":"path","path":"/x"}}}}""",
                     "", "{", "[]",
                 })
            await Assert.That(PinRevalidation.RootNixpkgsRevision(text)).IsNull();
    }

    [Test]
    public async Task A_pin_is_stale_while_in_use_and_superseded_once_nixpkgs_catches_up()
    {
        var pin = new PackagePin("2.1.2", null, PinNix.OldRev);
        await Assert.That(PinRevalidation.Status(pin, PinNix.NewRev, "2.1.1")).IsEqualTo(PinStatus.Stale);
        await Assert.That(PinRevalidation.Status(pin, PinNix.NewRev, "2.1.2")).IsEqualTo(PinStatus.Superseded);
        await Assert.That(PinRevalidation.Status(pin, PinNix.NewRev, "2.2.0")).IsEqualTo(PinStatus.Superseded);
        await Assert.That(PinRevalidation.Status(pin, PinNix.OldRev, "2.1.1")).IsEqualTo(PinStatus.Current);
        // A nightly precedes its release; a version that cannot be compared keeps the pin in use.
        await Assert.That(PinRevalidation.Status(new PackagePin("0.1.2-nightly.20261021.3473", "nightly", PinNix.OldRev), PinNix.NewRev, "0.1.2"))
            .IsEqualTo(PinStatus.Superseded);
        await Assert.That(PinRevalidation.Status(pin with { NixpkgsRev = null }, PinNix.NewRev, "0-unstable-2026-10-01")).IsEqualTo(PinStatus.Stale);
    }

    [Test]
    public async Task A_hash_mismatch_is_traced_to_the_pin_by_the_derivation_name()
    {
        var pins = new Dictionary<string, PackagePin>
        {
            ["opencode"] = new("2.1.2", null, PinNix.OldRev),
            ["t3code"] = new("0.1.2-nightly.20261021.3473", "nightly", PinNix.OldRev),
        };
        await Assert.That(PinRevalidation.MismatchedPin(IssueLog, pins)).IsEqualTo("opencode");
        // The resolved (content-addressed) derivation has another store hash and the same name.
        await Assert.That(PinRevalidation.MismatchedPin(IssueLog.Replace("pviv3n1nlgjlkl9azkg3xj4qain7psqa", "i71kyrhl00000000000000000000000a", StringComparison.Ordinal), pins))
            .IsEqualTo("opencode");
        await Assert.That(PinRevalidation.MismatchedPin("\u001b[31;1m" + IssueLog + "\u001b[0m", pins)).IsEqualTo("opencode");
        await Assert.That(PinRevalidation.MismatchedPin(
            $"hash mismatch in fixed-output derivation '/nix/store/{new string('x', 32)}-t3code-unwrapped-pnpm-deps.drv':", pins)).IsEqualTo("t3code");
        await Assert.That(PinRevalidation.MismatchedPin(
            $"hash mismatch in fixed-output derivation '/nix/store/{new string('x', 32)}-t3code-resource-monitor-0.1.2-nightly.20261021.3473-vendor-staging.drv':", pins))
            .IsEqualTo("t3code");
        // nixpkgs' own OpenCode, another version, and other derivations are not the pin's.
        await Assert.That(PinRevalidation.MismatchedPin(IssueLog.Replace("node_modules-2.1.2", "node_modules-2.1.1", StringComparison.Ordinal), pins)).IsNull();
        await Assert.That(PinRevalidation.MismatchedPin($"hash mismatch in fixed-output derivation '/nix/store/{new string('x', 32)}-source.drv':", pins)).IsNull();
        await Assert.That(PinRevalidation.MismatchedPin("error: build failed", pins)).IsNull();

        var explanation = PinRevalidation.ExplainMismatch(IssueLog, _flake, PinNix.Lock(PinNix.NewRev))!;
        await Assert.That(explanation.Title.English).IsEqualTo("The opencode pin was validated against nixpkgs 02c087f, this build used 3ec6363");
        await Assert.That(explanation.Body.English).Contains("nix-manager opencode-update");
        await Assert.That(explanation.Body.English).Contains("Do not copy the reported hash into the pin");
        // The nixpkgs the pin was validated against fails for another reason.
        await Assert.That(PinRevalidation.ExplainMismatch(IssueLog, _flake, PinNix.Lock(PinNix.OldRev))).IsNull();
    }

    [Test]
    [Arguments("opencode")]
    [Arguments("t3code")]
    public async Task Revalidation_evaluates_the_given_flake_copy_and_keeps_the_release(string name)
    {
        var copy = Directory.CreateDirectory(Path.Combine(_flake, "copy")).FullName;
        var calls = new List<Command>();
        Task<CommandResult> Run(Command command)
        {
            calls.Add(command);
            if (command.Argv[^1].EndsWith(".drvPath", StringComparison.Ordinal)) return Task.FromResult(new CommandResult(0, "/nix/store/expected.drv", ""));
            if (command.AllowFailure == true)
                return Task.FromResult(new CommandResult(1, "", $"hash mismatch in fixed-output derivation '/nix/store/expected.drv':\n  got: {PinNix.Hash}"));
            return Task.FromResult(new CommandResult(0, "", ""));
        }
        var pin = name == "opencode"
            ? OpenCodePin
            : $$"""{"version":"0.1.2-nightly.20261021.3473","channel":"nightly","srcHash":"sha256-src","nixpkgsRev":"{{PinNix.OldRev}}","pnpmHash":"sha256-old","cargoHash":"sha256-old"}""";

        var revalidated = await PackageUpdater.RevalidateAsync(copy, name, pin, "x86_64-linux", PinNix.NewRev, Run);

        foreach (var command in calls)
        {
            await Assert.That(command.Env!["NIX_MANAGER_HOME_FLAKE"]).IsEqualTo($"path:{copy}");
            await Assert.That(command.Env["NIX_MANAGER_RECIPE"]).IsEqualTo(Path.Combine(copy, "pkgs", name, "package.nix"));
        }
        await Assert.That(calls.Any(command => command.Argv[0] == "nix-prefetch-url")).IsFalse();
        await Assert.That(calls[^1].Label.English).StartsWith($"Build {name} ");
        await Assert.That(calls[^1].Argv[^1]).IsEqualTo(PackageUpdater.CandidateExpression);
        var before = JsonNode.Parse(pin)!.AsObject();
        await Assert.That(revalidated["version"]!.GetValue<string>()).IsEqualTo(before["version"]!.GetValue<string>());
        await Assert.That(revalidated["srcHash"]!.GetValue<string>()).IsEqualTo(before["srcHash"]!.GetValue<string>());
        await Assert.That(revalidated["nixpkgsRev"]!.GetValue<string>()).IsEqualTo(PinNix.NewRev);
        foreach (var field in name == "opencode" ? new[] { "nodeHash" } : ["pnpmHash", "cargoHash"])
            await Assert.That(revalidated[field]!.GetValue<string>()).IsEqualTo(PinNix.Hash);
        await Assert.That(revalidated.Select(pair => pair.Key)).IsEquivalentTo(before.Select(pair => pair.Key), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_reviewed_update_stages_the_revalidated_pin_and_applying_writes_it_with_the_lock()
    {
        var nix = new PinNix(_flake, _home);
        await using var service = Service(nix.Runner);
        var root = Paths.ReviewRoot(Config, "aaaaaaaa");

        var built = await service.Start(Review("aaaaaaaa")).Completion;

        await Assert.That(built.Status).IsEqualTo(OperationStatuses.Succeeded).Because(built.Error?.English ?? "");
        // Nothing in the checkout changes before the review is applied.
        await Assert.That(File.ReadAllText(PinFile)).IsEqualTo(OpenCodePin);
        await Assert.That(Lock).IsEqualTo(PinNix.Lock(PinNix.OldRev));
        var revalidation = nix.Calls.Where(c => c.Env?.ContainsKey("NIX_MANAGER_PACKAGE_PIN") == true).ToList();
        await Assert.That(revalidation).IsNotEmpty();
        await Assert.That(revalidation.All(c => c.Env!["NIX_MANAGER_HOME_FLAKE"] == $"path:{root}/source")).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(root, "source/flake.lock"))).IsEqualTo(nix.Proposed);
        // The configuration is built from the copy holding the revalidated pin, with the reviewed lock.
        var build = nix.Calls.Single(c => c.Label.English == "Build home configuration");
        await Assert.That(build.Argv).Contains($"path:{root}/source#homeConfigurations.user-test.activationPackage");
        await Assert.That(build.Argv).Contains("--reference-lock-file");
        var review = ReviewStore.Get(Config, "aaaaaaaa");
        var pin = review.Inputs!.Pins!.Single();
        await Assert.That((pin.Name, pin.Path, pin.NixpkgsRev, pin.BaseHash)).IsEqualTo(("opencode", "pkgs/opencode/source.json", PinNix.NewRev, Repository.HashText(OpenCodePin)));
        var text = JsonNode.Parse(pin.Text)!;
        await Assert.That(text["nodeHash"]!.GetValue<string>()).IsEqualTo(PinNix.Hash);
        await Assert.That(text["nixpkgsRev"]!.GetValue<string>()).IsEqualTo(PinNix.NewRev);
        await Assert.That(text["version"]!.GetValue<string>()).IsEqualTo("2.1.2");
        await Assert.That(File.ReadAllText(Path.Combine(root, "source/pkgs/opencode/source.json"))).IsEqualTo(pin.Text);
        await Assert.That(service.Log(built.Id).Text).Contains("opencode: the 2.1.2 pin was validated against nixpkgs 02c087f; revalidating it against 3ec6363.");

        // An earlier home generation, so the applied change can be undone.
        File.CreateSymbolicLink(Config.HomeProfile, "home-manager-1-link");
        var apply = service.Plan(Request.Create() with { Action = Actions.ReviewApply, ReviewId = review.Id, Scope = Scopes.Home });
        await Assert.That(apply.Steps[0].Label.English).IsEqualTo("Write the reviewed flake.lock and package pins");
        var applied = await service.Start(Request.Create() with { Action = Actions.ReviewApply, ReviewId = review.Id, Scope = Scopes.Home }).Completion;

        await Assert.That(applied.Status).IsEqualTo(OperationStatuses.Succeeded).Because(applied.Error?.English ?? "");
        await Assert.That(Lock).IsEqualTo(nix.Proposed);
        await Assert.That(File.ReadAllText(PinFile)).IsEqualTo(pin.Text);
        await Assert.That(applied.Undo!.Files.Select(file => file.Path)).IsEquivalentTo(["flake.lock", "pkgs/opencode/source.json"]);
        await Assert.That(applied.Undo.Changes.Select(change => change.English)).Contains("Revalidate the opencode pin against nixpkgs 3ec6363");
    }

    [Test]
    public async Task A_pin_changed_after_the_review_keeps_the_lock_and_the_pin()
    {
        var nix = new PinNix(_flake, _home);
        await using var service = Service(nix.Runner);
        await service.Start(Review("aaaaaaaa")).Completion;
        File.WriteAllText(PinFile, OpenCodePin.Replace("2.1.2", "2.1.3", StringComparison.Ordinal));

        await Assert.That(() => ReviewStore.WriteReviewedLock(Config, ReviewStore.Get(Config, "aaaaaaaa"))).Throws<ManagerException>()
            .WithMessage("pkgs/opencode/source.json changed after it was revalidated, so nothing was written. Prepare the update again.");
        await Assert.That(Lock).IsEqualTo(PinNix.Lock(PinNix.OldRev));
    }

    [Test]
    public async Task A_failed_revalidation_fails_the_update_and_changes_neither_pin_nor_lock()
    {
        var nix = new PinNix(_flake, _home, command => command.Label.English == "Build opencode 2.1.2 before saving pin");
        await using var service = Service(nix.Runner);

        var built = await service.Start(Review("aaaaaaaa")).Completion;

        await Assert.That(built.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(built.Error!.English).StartsWith(
            "Could not revalidate the opencode 2.1.2 pin against nixpkgs 3ec6363 (it was validated against 02c087f), so neither the pin nor flake.lock changes.");
        await Assert.That(built.Error.English).Contains($"https://github.com/NixOS/nixpkgs/compare/{PinNix.OldRev}...{PinNix.NewRev} (pkgs/by-name/op/opencode/package.nix)");
        await Assert.That(nix.Calls.Any(c => c.Label.English == "Build home configuration")).IsFalse();
        await Assert.That(Directory.Exists(Paths.ReviewRoot(Config, "aaaaaaaa"))).IsFalse();
        await Assert.That(File.ReadAllText(PinFile)).IsEqualTo(OpenCodePin);
        await Assert.That(Lock).IsEqualTo(PinNix.Lock(PinNix.OldRev));

        var update = await service.Start(Request.Create() with { Action = Actions.Switch, Scope = Scopes.Home, Update = true }).Completion;

        await Assert.That(update.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(File.ReadAllText(PinFile)).IsEqualTo(OpenCodePin);
        await Assert.That(Lock).IsEqualTo(PinNix.Lock(PinNix.OldRev));
        await Assert.That(Directory.Exists(Paths.BuildRoot(Config))).IsFalse();
    }

    [Test]
    public async Task Unchanged_or_caught_up_nixpkgs_leaves_the_pins_alone()
    {
        foreach (var (proposed, version) in new[] { (PinNix.Lock(PinNix.OldRev), "2.1.1"), (PinNix.Lock(PinNix.NewRev), "2.1.2") })
        {
            var nix = new PinNix(_flake, _home) { Proposed = proposed, NixpkgsVersion = version };
            await using var service = Service(nix.Runner);
            var id = version == "2.1.1" ? "aaaaaaaa" : "bbbbbbbb";

            var built = await service.Start(Review(id)).Completion;

            await Assert.That(built.Status).IsEqualTo(OperationStatuses.Succeeded);
            await Assert.That(nix.Calls.Any(c => c.Env?.ContainsKey("NIX_MANAGER_PACKAGE_PIN") == true)).IsFalse();
            await Assert.That(nix.Calls.Single(c => c.Label.English == "Build home configuration").Argv).Contains($"{_flake}#homeConfigurations.user-test.activationPackage");
            await Assert.That(ReviewStore.Get(Config, id).Inputs!.Pins).IsNull();
            await Assert.That(service.Log(built.Id).Text).Contains(version == "2.1.1"
                ? "nixpkgs stays at 02c087f; the package pins need no revalidation."
                : "opencode: nixpkgs 3ec6363 has 2.1.2, not older than the 2.1.2 pin, so the package uses nixpkgs' own; the pin is superseded.");
        }
    }

    [Test]
    public async Task An_update_switch_writes_the_revalidated_pin_with_the_lock_before_building()
    {
        var nix = new PinNix(_flake, _home);
        await using var service = Service(nix.Runner);

        var operation = await service.Start(Request.Create() with { Action = Actions.Switch, Scope = Scopes.Home, Update = true }).Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Succeeded).Because(operation.Error?.English ?? "");
        await Assert.That(Lock).IsEqualTo(nix.Proposed);
        var pin = JsonNode.Parse(File.ReadAllText(PinFile))!;
        await Assert.That(pin["nixpkgsRev"]!.GetValue<string>()).IsEqualTo(PinNix.NewRev);
        await Assert.That(pin["nodeHash"]!.GetValue<string>()).IsEqualTo(PinNix.Hash);
        // The configuration is built from the checkout, which has the new pin by then.
        await Assert.That(nix.PinAtBuild.Single()).IsEqualTo(File.ReadAllText(PinFile));
        await Assert.That(nix.Calls.Single(c => c.Label.English == "Build home configuration").Argv).Contains($"{_flake}#homeConfigurations.user-test.activationPackage");
        await Assert.That(nix.Calls.Where(c => c.Env?.ContainsKey("NIX_MANAGER_PACKAGE_PIN") == true)
            .All(c => c.Env!["NIX_MANAGER_HOME_FLAKE"] == $"path:{Paths.BuildRoot(Config)}/source")).IsTrue();
        await Assert.That(Directory.Exists(Paths.BuildRoot(Config))).IsFalse();
    }

    [Test]
    public async Task A_build_that_fails_on_a_pinned_hash_names_the_pin_and_both_revisions()
    {
        File.WriteAllText(Path.Combine(_flake, "flake.lock"), PinNix.Lock(PinNix.NewRev));
        var runner = new FakeRunner((command, output, _) =>
        {
            if (command.Argv is not [_, "build", ..]) return Task.FromResult(new CommandResult(0, "", ""));
            output(new TextOutput("stderr", IssueLog));
            throw new ManagerException(DisplayText.Plain("Build home configuration failed (exit 1)."));
        });
        await using var service = Service(runner);

        var operation = await service.Start(Request.Create() with { Action = Actions.Build, Scope = Scopes.Home }).Completion;

        await Assert.That(operation.Status).IsEqualTo(OperationStatuses.Failed);
        await Assert.That(operation.Error!.English).IsEqualTo("Build home configuration failed (exit 1).");
        await Assert.That(operation.Explanation!.Title.English).IsEqualTo("The opencode pin was validated against nixpkgs 02c087f, this build used 3ec6363");
        // The pin keeps its hashes; only revalidation changes them.
        await Assert.That(File.ReadAllText(PinFile)).IsEqualTo(OpenCodePin);
    }
}
