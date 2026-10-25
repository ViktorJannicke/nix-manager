using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Updates;

namespace NixManager.Tests;

internal sealed class ReleaseNetwork(Func<string, string> answer) : INetwork
{
    public List<string> Urls { get; } = [];

    public Task<string> TextAsync(string url, CancellationToken cancellationToken)
    {
        lock (Urls) Urls.Add(url);
        return Task.FromResult(answer(url));
    }

    public Task<NetworkResponse> GetAsync(string url, bool followRedirects, CancellationToken cancellationToken) =>
        throw new NotSupportedException(url);
}

/// <summary>The package pin updaters with a fake network and runner.</summary>
public sealed class PackageUpdaterTests : IDisposable
{
    private static readonly string Hash = "sha256-" + new string('B', 43) + "=";
    private static readonly string Rev = new('a', 40);
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories) Directory.Delete(directory, recursive: true);
    }

    private sealed record Options
    {
        public string? Fail { get; init; }
        public string? Current { get; init; }
        public string? Locked { get; init; }
        public string? PinRev { get; init; }
        public bool Force { get; init; }
        public bool DryRun { get; init; }
        public bool ConcurrentEdit { get; init; }
        public string? Channel { get; init; }
        public IReadOnlyList<string>? Blocked { get; init; }
    }

    private sealed record Harness(Config Config, string Before, string PinFile, List<Command> Calls, List<string> Log, Func<Task> Run);

    private static string Release(string url) =>
        url.Contains("releases/latest", StringComparison.Ordinal) ? """{"tag_name":"v0.0.38"}"""
        : url.Contains("registry.npmjs.org", StringComparison.Ordinal) ? """{"latest":"0.0.38","dev":"0.0.0-dev-21430"}"""
        : url.Contains("releases?per_page", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new object[]
            {
                new { tag_name = "v0.0.39-nightly.20261013.12" },
                new { tag_name = "v0.0.39-nightly.20261013.9" },
                new { tag_name = "v0.0.40-nightly.20261014.1", draft = true },
                new { tag_name = "v0.0.39-preview.1" },
                new { tag_name = "v0.0.38" },
            })
        : url.Contains("commits/nixos-unstable", StringComparison.Ordinal) ? $$"""{"sha":"{{Rev}}"}"""
        : "version = \"0.0.38\";\n";

    private Harness Create(string name, Options? options = null)
    {
        options ??= new Options();
        var flake = TestConfig.TemporaryFlake();
        _directories.Add(flake);
        var config = TestConfig.Create(flake);
        var directory = Path.Combine(flake, "pkgs", name);
        Directory.CreateDirectory(directory);
        var pinFile = Path.Combine(directory, "source.json");
        var pin = new JsonObject { ["version"] = options.Current ?? "0.0.34" };
        if (options.Channel is not null) pin["channel"] = options.Channel;
        pin["nixpkgsRev"] = options.PinRev ?? Rev;
        File.WriteAllText(pinFile, pin.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "package.nix"), "{}");
        var before = File.ReadAllText(pinFile);
        var calls = new List<Command>();
        var log = new List<string>();
        Task<CommandResult> Run(Command c)
        {
            calls.Add(c);
            var args = string.Join(' ', c.Argv);
            string stdout = "", stderr = "";
            var code = 0;
            if (args.Contains("nixpkgs.outPath", StringComparison.Ordinal)) stdout = flake;
            else if (args.Contains("nixpkgs.rev", StringComparison.Ordinal)) stdout = Rev;
            else if (args.Contains("pkgs.${builtins.getEnv", StringComparison.Ordinal)) stdout = options.Locked ?? "0.0.34";
            else if (c.Argv[0] == "diff") stdout = "recipe comparison";
            else if (c.Argv[0] == "nix-prefetch-url") stdout = "rawhash";
            else if (c.Argv[1] == "hash") stdout = Hash;
            else if (args.EndsWith(".drvPath", StringComparison.Ordinal)) stdout = "/nix/store/expected.drv";
            else if (c.Argv[1] == "build" && c.AllowFailure == true)
            {
                if (options.Fail == "hash") throw new ManagerException(DisplayText.Plain("Hash discovery failed"));
                stderr = $"hash mismatch in fixed-output derivation '/nix/store/expected.drv':\n  got: {Hash}";
                code = 1;
            }
            else if (c.Argv[1] == "build")
            {
                if (options.Fail == "build") throw new ManagerException(DisplayText.Plain("Build failed"));
                if (options.ConcurrentEdit) File.WriteAllText(pinFile, "a concurrent edit");
            }
            else throw new InvalidOperationException($"Unhandled fixture command: {args}");
            return Task.FromResult(new CommandResult(code, stdout, stderr));
        }
        var network = new ReleaseNetwork(url => options.Fail == "network" ? throw new ManagerException(DisplayText.Plain("Network failed")) : Release(url));
        var packages = options.Blocked is null
            ? null
            : PackageUpdater.Packages.ToDictionary(pair => pair.Key, pair => pair.Value with { BlockedVersions = options.Blocked });
        var updater = new PackageUpdater(network, packages);
        var request = Request.Create() with { Action = $"{name}-update", Host = "test", DryRun = options.DryRun, Force = options.Force };
        return new Harness(config, before, pinFile, calls, log,
            () => updater.UpdateAsync(config, request, Run, log.Add, CancellationToken.None));
    }

    private static bool Built(Harness h) => h.Calls.Any(c => c.Argv.Count > 1 && c.Argv[1] == "build");

    private static JsonNode Pin(Harness h) => JsonNode.Parse(File.ReadAllText(h.PinFile))!;

    public static IEnumerable<string> Names() => ["t3code", "opencode", "pince"];

    [Test]
    public async Task The_nightly_channel_follows_the_newest_published_nightly()
    {
        var h = Create("t3code", new Options { Current = "0.0.38-nightly.20261001.1", Channel = "nightly" });
        await h.Run();
        await Assert.That(Pin(h)["version"]!.GetValue<string>()).IsEqualTo("0.0.39-nightly.20261013.12");
        await Assert.That(Pin(h)["channel"]!.GetValue<string>()).IsEqualTo("nightly");
        var stable = Create("t3code", new Options { Current = "0.0.38-nightly.20261001.1" });
        await stable.Run();
        await Assert.That(Pin(stable)["version"]!.GetValue<string>()).IsEqualTo("0.0.38");
        var caughtUp = Create("t3code", new Options { Current = "0.0.39-nightly.20261013.12", Channel = "nightly", Locked = "0.0.39" });
        await caughtUp.Run();
        await Assert.That(Built(caughtUp)).IsFalse();
    }

    [Test]
    public async Task Hash_discovery_matches_the_exact_derivation()
    {
        var log = $"hash mismatch in fixed-output derivation '/nix/store/other.drv':\n got: sha256-{new string('A', 43)}=\nhash mismatch in fixed-output derivation '/nix/store/expected.drv':\n got: {Hash}";
        await Assert.That(PackageUpdater.ExtractHash(log, "/nix/store/expected.drv", 1)).IsEqualTo(Hash);
        await Assert.That(PackageUpdater.ExtractHash("\u001b[31m" + log + "\u001b[0m", "/nix/store/expected.drv", 1)).IsEqualTo(Hash);
        // Content-addressed inputs make Nix report the resolved derivation, with another hash but the same name.
        const string evaluated = "/nix/store/xabj5w6pklyh9w32mn8b83aick4ljq6k-t3code-unwrapped-pnpm-deps.drv";
        var resolved = $"hash mismatch in fixed-output derivation '/nix/store/izdl1p5v4pdwjyn1mys7fjdjizb1hskr-t3code-unwrapped-pnpm-deps.drv':\n  specified: sha256-{new string('A', 43)}=\n     got:    {Hash}\nerror: build of resolved derivation '/nix/store/izdl1p5v4pdwjyn1mys7fjdjizb1hskr-t3code-unwrapped-pnpm-deps.drv' failed";
        await Assert.That(PackageUpdater.ExtractHash(resolved, evaluated, 1)).IsEqualTo(Hash);
        await Assert.That(() => PackageUpdater.ExtractHash(resolved.Replace("t3code-unwrapped-pnpm-deps", "other-deps", StringComparison.Ordinal), evaluated, 1))
            .Throws<ManagerException>();
        foreach (var output in new[]
                 {
                     "network failed",
                     $"got: {Hash}",
                     $"hash mismatch in fixed-output derivation '/nix/store/other.drv':\n got: {Hash}",
                     "hash mismatch in fixed-output derivation '/nix/store/expected.drv':\n got: invalid",
                 })
            await Assert.That(() => PackageUpdater.ExtractHash(output, "/nix/store/expected.drv", 1)).Throws<ManagerException>()
                .WithMessage("Could not extract the hash for /nix/store/expected.drv. See the operation log.");
        await Assert.That(() => PackageUpdater.ExtractHash(log, "/nix/store/expected.drv", 0)).Throws<ManagerException>()
            .WithMessage("Expected a fake-hash build failure, but the build succeeded.");
    }

    [Test]
    [MethodDataSource(nameof(Names))]
    public async Task A_dry_run_keeps_the_pin_and_never_fetches_archives_or_builds(string name)
    {
        var h = Create(name, new Options { DryRun = true });
        await h.Run();
        await Assert.That(File.ReadAllText(h.PinFile)).IsEqualTo(h.Before);
        await Assert.That(h.Calls.Any(c => c.Argv[0] == "nix-prefetch-url") || Built(h)).IsFalse();
        await Assert.That(h.Log[^1]).IsEqualTo($"Would fetch, hash and build {name} 0.0.38, then atomically save {h.PinFile}.");
        await Assert.That(h.Log[0]).IsEqualTo($"{name}: local pin 0.0.34 | stable release 0.0.38");
    }

    [Test]
    [MethodDataSource(nameof(Names))]
    public async Task A_failure_leaves_the_pin_intact(string name)
    {
        foreach (var fail in name == "pince" ? new[] { "network", "build" } : ["network", "build", "hash"])
        {
            var h = Create(name, new Options { Fail = fail });
            await Assert.That(h.Run).Throws<ManagerException>();
            await Assert.That(File.ReadAllText(h.PinFile)).IsEqualTo(h.Before);
        }
    }

    [Test]
    [MethodDataSource(nameof(Names))]
    public async Task The_validated_build_precedes_the_atomic_pin_replacement(string name)
    {
        var h = Create(name);
        await h.Run();
        var pin = Pin(h);
        await Assert.That(pin["version"]!.GetValue<string>()).IsEqualTo("0.0.38");
        await Assert.That(h.Calls[^1].Argv[1]).IsEqualTo("build");
        await Assert.That(h.Calls[^1].Label.English).IsEqualTo($"Build {name} 0.0.38 before saving pin");
        await Assert.That(pin[name == "t3code" ? "cargoHash" : name == "opencode" ? "nodeHash" : "hash"]!.GetValue<string>()).IsEqualTo(Hash);
        if (name != "pince") await Assert.That(pin["nixpkgsRev"]!.GetValue<string>()).IsEqualTo(Rev);
        // Written as JSON.stringify(value, null, 2), members in the recorded order.
        string[] members = name switch
        {
            "t3code" => ["version", "srcHash", "nixpkgsRev", "pnpmHash", "cargoHash"],
            "opencode" => ["version", "srcHash", "nixpkgsRev", "nodeHash"],
            _ => ["version", "hash"],
        };
        await Assert.That(pin.AsObject().Select(p => p.Key).ToArray()).IsEquivalentTo(members, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(File.ReadAllText(h.PinFile)).IsEqualTo(Nix.JsJson.Indented(pin) + "\n");
        await Assert.That(h.Log[^1]).IsEqualTo($"Updated {h.PinFile}. Apply with nix-manager home switch.");
        // The candidate expression reads the pin under test from the temporary workspace, removed afterwards.
        var env = h.Calls[^1].Env!;
        await Assert.That(env["NIX_MANAGER_HOME_FLAKE"]).IsEqualTo($"path:{h.Config.Flake}");
        await Assert.That(env["NIX_MANAGER_SYSTEM"]).IsEqualTo("x86_64-linux");
        await Assert.That(File.Exists(env["NIX_MANAGER_PACKAGE_PIN"])).IsFalse();
        await Assert.That(h.Calls[^1].Argv[^1]).IsEqualTo(name == "pince" ? PackageUpdater.PinceExpression : PackageUpdater.CandidateExpression);
    }

    [Test]
    [MethodDataSource(nameof(Names))]
    public async Task Current_pins_are_skipped_unless_forced(string name)
    {
        foreach (var force in new[] { false, true })
        {
            var h = Create(name, new Options { Current = "0.0.38", Force = force });
            await h.Run();
            await Assert.That(Built(h)).IsEqualTo(force);
            if (!force)
                await Assert.That(h.Log[^1]).IsEqualTo(name == "pince" ? "PINCE is current." : $"{name} is current. Use --force to recalculate hashes.");
        }
    }

    [Test]
    [MethodDataSource(nameof(Names))]
    public async Task Forced_downgrades_and_concurrent_pin_edits_are_refused(string name)
    {
        var h = Create(name, new Options { Current = "0.0.39", Force = true });
        await Assert.That(h.Run).Throws<ManagerException>().WithMessage($"Refusing a {name} downgrade.");
        await Assert.That(File.ReadAllText(h.PinFile)).IsEqualTo(h.Before);
        var edited = Create(name, new Options { ConcurrentEdit = true });
        await Assert.That(edited.Run).Throws<ManagerException>()
            .WithMessage("Save validated pin: Pin changed during the build; refusing to overwrite it. Retry with the new pin.");
        await Assert.That(File.ReadAllText(edited.PinFile)).IsEqualTo("a concurrent edit");
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(edited.PinFile)!, "*.tmp")).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(Names))]
    public async Task A_blocked_release_is_refused(string name)
    {
        var h = Create(name, new Options { Blocked = ["0.0.38"] });
        await Assert.That(h.Run).Throws<ManagerException>().WithMessage($"{name} 0.0.38 is blocked because of a known upstream regression.");
        await Assert.That(h.Calls).IsEmpty();
    }

    [Test]
    [Arguments("t3code")]
    [Arguments("opencode")]
    public async Task A_changed_nixpkgs_revision_rebuilds_and_caught_up_nixpkgs_skips(string name)
    {
        var changed = Create(name, new Options { Current = "0.0.38", PinRev = new string('b', 40) });
        await changed.Run();
        await Assert.That(Built(changed)).IsTrue();
        foreach (var locked in new[] { "0.0.38", "0.0.39" })
        {
            var h = Create(name, new Options { Locked = locked });
            await h.Run();
            await Assert.That(Built(h)).IsFalse();
        }
    }

    [Test]
    public async Task The_t3code_candidate_compares_its_three_recipes_and_discovers_both_dependency_hashes()
    {
        var h = Create("t3code");
        await h.Run();
        await Assert.That(h.Calls.Where(c => c.Argv[0] == "diff").Select(c => c.Label.English).ToArray()).IsEquivalentTo(
            new[] { "package.nix", "unwrapped.nix", "resource-monitor.nix" }.Select(recipe => $"Compare {recipe} against nixos-unstable {Rev}").ToArray(),
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(h.Calls.Where(c => c.AllowFailure == true && c.Argv[1] == "build").Select(c => c.Argv[^1]).ToArray()).IsEquivalentTo(
            new[] { $"({PackageUpdater.CandidateExpression}).unwrapped.pnpmDeps", $"({PackageUpdater.CandidateExpression}).resourceMonitor.cargoDeps.vendorStaging" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(h.Log).Contains($"Locked nixpkgs 0.0.34 | nixos-unstable 0.0.38. Candidate uses the locked recipe.");
        await Assert.That(h.Calls.Single(c => c.Argv[0] == "nix-prefetch-url").Argv).Contains("--unpack");
    }

    [Test]
    public async Task OpenCode_discovers_its_node_modules_hash_and_PINCE_fetches_the_AppImage_without_recipes()
    {
        var opencode = Create("opencode");
        await opencode.Run();
        await Assert.That(opencode.Calls.Single(c => c.AllowFailure == true && c.Argv[1] == "build").Label.English)
            .IsEqualTo("Compute OpenCode node_modules hash (may take several minutes)");
        var pince = Create("pince");
        await pince.Run();
        await Assert.That(pince.Calls.Any(c => c.Argv[0] == "diff" || c.Label.English == "Inspect locked nixpkgs")).IsFalse();
        var fetch = pince.Calls.Single(c => c.Argv[0] == "nix-prefetch-url").Argv;
        await Assert.That(fetch).DoesNotContain("--unpack");
        await Assert.That(fetch[^1]).IsEqualTo("https://github.com/korcankaraokcu/PINCE/releases/download/v0.0.38/PINCE-x86_64.AppImage");
    }

    [Test]
    public async Task Pins_are_validated_with_messages_that_name_the_problem()
    {
        await Assert.That(PackagePins.Parse("""{"version":"1.2.3","channel":"nightly","nixpkgsRev":"x"}""", "t3code")).IsEqualTo(new PackagePin("1.2.3", "nightly", "x"));
        await Assert.That(() => PackagePins.Parse("{}", "t3code")).Throws<ManagerException>().WithMessage("The t3code pin is not valid: version is missing.");
        await Assert.That(() => PackagePins.Parse("", "t3code")).Throws<ManagerException>().WithMessage("The t3code pin is empty.");
        await Assert.That(() => PackagePins.Parse("{", "t3code")).Throws<ManagerException>().WithMessageContaining("The t3code pin is not valid JSON: ");
        await Assert.That(() => PackagePins.Parse("null", "t3code")).Throws<ManagerException>().WithMessage("The t3code pin is not valid: it must be an object, not null.");
        await Assert.That(() => PackagePins.Parse("""{"version":1}""", "t3code")).Throws<ManagerException>().WithMessage("The t3code pin is not valid: version must be text, not 1.");
        await Assert.That(() => PackagePins.Parse("""{"version":"1.2"}""", "t3code")).Throws<ManagerException>()
            .WithMessage("The t3code pin is not valid: version must look like X.Y.Z or X.Y.Z-nightly.YYYYMMDD.N, not 1.2.");
        await Assert.That(() => PackagePins.Parse("""{"version":"1.2.3","channel":"beta"}""", "t3code")).Throws<ManagerException>()
            .WithMessage("The t3code pin is not valid: channel must be stable or nightly, not \"beta\".");
    }

    [Test]
    public async Task The_operation_runs_each_command_as_a_step_and_logs_the_decisions()
    {
        var flake = TestConfig.TemporaryFlake();
        _directories.Add(flake);
        Directory.CreateDirectory(Path.Combine(flake, "pkgs", "pince"));
        File.WriteAllText(Path.Combine(flake, "pkgs", "pince", "source.json"), """{"version":"0.0.34"}""");
        var runner = new FakeRunner();
        await using var service = new OperationService(TestConfig.Create(flake), runner, new FakeUpdates(new UpdateReport { CheckedAt = "" }),
            network: new ReleaseNetwork(Release));

        var dryRun = await service.Start(Request.Create() with { Action = Actions.PinceUpdate, Host = "test", DryRun = true }).Completion;
        await Assert.That(dryRun.Status).IsEqualTo(OperationStatuses.Succeeded);
        await Assert.That(dryRun.Step.English).IsEqualTo("Preview complete");
        await Assert.That(runner.Calls).IsEmpty();
        var log = service.Log(dryRun.Id).Text;
        await Assert.That(log).Contains("→ pince: local pin 0.0.34 | stable release 0.0.38\n");
        await Assert.That(log).Contains("→ Would fetch, hash and build pince 0.0.38");

        var run = await service.Start(Request.Create() with { Action = Actions.PinceUpdate, Host = "test" }).Completion;
        await Assert.That(run.Status).IsEqualTo(OperationStatuses.Succeeded).Because(run.Error?.English ?? "");
        await Assert.That(run.Steps!.Single().Status).IsEqualTo(StepStatuses.Succeeded);
        await Assert.That(runner.Calls.Select(c => c.Label.English).ToArray()).IsEquivalentTo(
            new[] { "Fetch release", "Convert source hash", "Build pince 0.0.38 before saving pin" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        // Each command is logged as a step with its command line.
        var text = service.Log(run.Id).Text;
        await Assert.That(text).Contains("→ Fetch release\n  nix-prefetch-url --type sha256 https://github.com/korcankaraokcu/PINCE/releases/download/v0.0.38/PINCE-x86_64.AppImage");
        await Assert.That(text).Contains("→ Convert source hash\n");
    }
}
