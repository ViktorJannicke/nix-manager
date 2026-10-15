using NixManager.Configuration;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Updates;
using NixManager.ViewModels;
using Runic.Navigation;

namespace NixManager.Tests;

/// <summary>Host resolution and the Home Manager profile (<c>test/config.test.ts</c>).</summary>
public sealed class ConfigTests
{
    [Test]
    public async Task The_NixOS_configuration_named_like_the_host_is_picked_as_nixos_rebuild_does()
    {
        await Assert.That(ConfigLoader.PickSystem("laptop", ["desktop", "laptop"])).IsEqualTo("laptop");
        await Assert.That(ConfigLoader.PickSystem("whatever", ["only"])).IsEqualTo("only");
        await Assert.That(ConfigLoader.PickSystem("my-laptop", ["desktop", "laptop"])).IsEqualTo(ConfigLoader.Evaluate);
        await Assert.That(ConfigLoader.PickSystem("my-laptop", ["desktop", "laptop"],
            new Dictionary<string, string> { ["desktop"] = "my-desktop", ["laptop"] = "my-laptop" })).IsEqualTo("laptop");
        await Assert.That(() => ConfigLoader.PickSystem("other", ["desktop", "laptop"], new Dictionary<string, string> { ["desktop"] = "a", ["laptop"] = "b" }))
            .Throws<ManagerException>().WithMessageContaining("Cannot tell which NixOS configuration");
        await Assert.That(() => ConfigLoader.PickSystem("other", [])).Throws<ManagerException>().WithMessageContaining("no nixosConfigurations");
    }

    [Test]
    public async Task The_Home_Manager_configuration_is_picked_as_home_manager_does_then_by_words()
    {
        await Assert.That(ConfigLoader.PickHome("alice", ["laptop"], ["alice@laptop", "alice"])).IsEqualTo("alice@laptop");
        await Assert.That(ConfigLoader.PickHome("alice", ["laptop"], ["alice", "bob"])).IsEqualTo("alice");
        await Assert.That(ConfigLoader.PickHome("alice", ["laptop"], ["main"])).IsEqualTo("main");
        string[] names = ["alice-desktop", "alice-laptop", "bob-laptop"];
        var users = new Dictionary<string, string> { ["alice-desktop"] = "alice", ["alice-laptop"] = "alice", ["bob-laptop"] = "bob" };
        await Assert.That(ConfigLoader.PickHome("alice", ["alices-nixos-laptop"], names)).IsEqualTo(ConfigLoader.Evaluate);
        await Assert.That(ConfigLoader.PickHome("alice", ["alices-nixos-laptop"], names, users)).IsEqualTo("alice-laptop");
        await Assert.That(ConfigLoader.PickHome("bob", ["anything"], names, users)).IsEqualTo("bob-laptop");
        await Assert.That(() => ConfigLoader.PickHome("alice", ["server"], names, users)).Throws<ManagerException>().WithMessageContaining("alice-desktop, alice-laptop");
        await Assert.That(() => ConfigLoader.PickHome("alice", ["laptop"], [])).Throws<ManagerException>().WithMessageContaining("standalone Home Manager");
    }

    [Test]
    public async Task An_existing_profile_stays_where_it_is_and_a_new_user_gets_the_modern_folder()
    {
        const string modern = "/home/a/.local/state/nix/profiles";
        const string legacy = "/nix/var/nix/profiles/per-user/a";
        string Choose(params string[] present) => ConfigLoader.ChooseHomeProfile(modern, legacy, present.Contains);
        await Assert.That(Choose()).IsEqualTo($"{modern}/home-manager");
        await Assert.That(Choose(modern)).IsEqualTo($"{modern}/home-manager");
        await Assert.That(Choose(legacy)).IsEqualTo($"{legacy}/home-manager");
        await Assert.That(Choose(modern, legacy, $"{legacy}/home-manager")).IsEqualTo($"{legacy}/home-manager");
        await Assert.That(Choose(modern, legacy, $"{legacy}/home-manager", $"{modern}/home-manager")).IsEqualTo($"{modern}/home-manager");
    }

    [Test]
    public async Task Without_a_registry_the_host_is_read_from_the_flake()
    {
        var flake = TestConfig.TemporaryFlake();
        try
        {
            var runner = new FakeRunner((command, _, _) => Task.FromResult(new CommandResult(0, command.Argv switch
            {
                [.., var installable, "--apply", "builtins.attrNames"] when installable.EndsWith("#nixosConfigurations") => """["desktop","laptop"]""",
                [.., var installable, "--apply", "builtins.attrNames"] when installable.EndsWith("#homeConfigurations") => """["alice@laptop","alice@desktop"]""",
                _ => throw new InvalidOperationException(string.Join(' ', command.Argv)),
            }, "")));
            var environment = new ManagerEnvironment(name => name == "NIX_MANAGER_FLAKE" ? flake : null, "laptop", "alice", flake);

            var config = await new ConfigLoader(runner, environment).LoadAsync(null, null, ["pkexec", "nix-manager", "--privileged"]);

            await Assert.That(config.HostKey).IsEqualTo("laptop");
            await Assert.That(config.Host.HomeConfiguration).IsEqualTo("alice@laptop");
            await Assert.That(config.Host.NixosHostName).IsEqualTo("laptop");
            await Assert.That(config.StateDir).IsEqualTo(Path.Combine(flake, ".local/state/nix-manager"));
            await Assert.That(runner.Calls.All(call => call.Quiet == true)).IsTrue();
        }
        finally
        {
            Directory.Delete(flake, recursive: true);
        }
    }
}

internal sealed class FakeNetwork(Dictionary<string, string> answers) : INetwork
{
    public Task<string> TextAsync(string url, CancellationToken cancellationToken) =>
        answers.TryGetValue(url, out var text) ? Task.FromResult(text) : throw new ManagerException($"Fetch failed: HTTP 404 from {url}");

    public Task<NetworkResponse> GetAsync(string url, bool followRedirects, CancellationToken cancellationToken) =>
        throw new NotSupportedException(url);
}

/// <summary>Update checks (<c>test/updates.test.ts</c>): inputs from a proposed lock, package releases by channel.</summary>
public sealed class UpdateCheckerTests
{
    [Test]
    public async Task Versions_order_nightlies_by_date_and_run_before_their_stable_release()
    {
        await Assert.That(UpdateChecker.CompareVersions("1.10.0", "1.9.99")).IsEqualTo(1);
        await Assert.That(UpdateChecker.CompareVersions("1.0.0", "1.0.0")).IsEqualTo(0);
        await Assert.That(() => UpdateChecker.CompareVersions("1.0.0-beta", "1.0.0")).Throws<ManagerException>();
        string[] ordered = ["0.0.45", "0.0.46-nightly.20261012.99", "0.0.46-nightly.20261013.9", "0.0.46-nightly.20261013.10", "0.0.46", "0.0.47-nightly.20261001.1"];
        for (var i = 1; i < ordered.Length; i++)
        {
            await Assert.That(UpdateChecker.CompareVersions(ordered[i], ordered[i - 1])).IsEqualTo(1);
            await Assert.That(UpdateChecker.CompareVersions(ordered[i - 1], ordered[i])).IsEqualTo(-1);
        }
    }

    [Test]
    public async Task Inputs_and_pins_are_compared_without_changing_the_checkout()
    {
        var flake = TestConfig.TemporaryFlake();
        try
        {
            const string before = """{"nodes":{"root":{"inputs":{"nixpkgs":"nixpkgs"}},"nixpkgs":{"locked":{"lastModified":1791700000,"narHash":"sha256-old","owner":"nixos","repo":"nixpkgs","rev":"7fd4004d0511aaaaaaaaaaaaaaaaaaaaaaaaaaaa","type":"github"}},"same":{"locked":{"type":"path","path":"/x","narHash":"sha256-x"}}},"root":"root","version":7}""";
            const string after = """{"nodes":{"root":{"inputs":{"nixpkgs":"nixpkgs"}},"nixpkgs":{"locked":{"type":"github","rev":"55bfb4d7437bbbbbbbbbbbbbbbbbbbbbbbbbbbbb","repo":"nixpkgs","owner":"nixos","narHash":"sha256-new","lastModified":1791800000}},"same":{"locked":{"narHash":"sha256-x","path":"/x","type":"path"}}},"root":"root","version":7}""";
            File.WriteAllText(Path.Combine(flake, "flake.lock"), before);
            Directory.CreateDirectory(Path.Combine(flake, "pkgs/t3code"));
            File.WriteAllText(Path.Combine(flake, "pkgs/t3code/source.json"), """{"version":"0.0.46-nightly.20261014.3118","channel":"nightly"}""");
            Directory.CreateDirectory(Path.Combine(flake, "pkgs/pince"));
            File.WriteAllText(Path.Combine(flake, "pkgs/pince/source.json"), """{"version":"0.10.2"}""");
            var network = new FakeNetwork(new()
            {
                ["https://api.github.com/repos/pingdotgg/t3code/releases?per_page=100"] =
                    """[{"tag_name":"v0.0.46-nightly.20261015.3145"},{"tag_name":"v0.0.47-nightly.20261016.1","draft":true},{"tag_name":"v0.0.45"}]""",
                ["https://api.github.com/repos/korcankaraokcu/PINCE/releases/latest"] = """{"tag_name":"v0.10.2"}""",
            });
            var commands = new List<Command>();
            Task<CommandResult> Run(Command command)
            {
                commands.Add(command);
                File.WriteAllText(command.Argv[^1], after);
                return Task.FromResult(new CommandResult(0, "", ""));
            }

            var report = await new UpdateChecker(network).CheckAsync(TestConfig.Create(flake), Run, CancellationToken.None);

            await Assert.That(commands.Single().Argv.Take(5)).IsEquivalentTo(["nix", "flake", "update", "--flake", flake]);
            await Assert.That(File.ReadAllText(Path.Combine(flake, "flake.lock"))).IsEqualTo(before);
            await Assert.That(report.Items.Select(item => (item.Name, item.Current, item.Latest, item.Kind))).IsEquivalentTo([
                ("nixpkgs", "7fd4004d0511", "55bfb4d7437b", "input"),
                ("t3code", "0.0.46-nightly.20261014.3118", "0.0.46-nightly.20261015.3145", "package"),
            ]);
            var input = report.Items[0];
            await Assert.That(input.Source).IsEqualTo("nixos/nixpkgs");
            await Assert.That(input.Url).IsEqualTo("https://github.com/nixos/nixpkgs/commit/55bfb4d7437bbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            await Assert.That(input.LatestDate).IsEqualTo("2026-10-12T10:13:20.000Z");
            await Assert.That(report.Items[1].Url).IsEqualTo("https://github.com/pingdotgg/t3code/releases/tag/v0.0.46-nightly.20261015.3145");
            // A pin that is missing is an error of its own; the other checks still report.
            await Assert.That(report.Errors.Single()).StartsWith("opencode: Read opencode pin:");
        }
        finally
        {
            Directory.Delete(flake, recursive: true);
        }
    }
}

/// <summary>The ViewModel the dashboard and the tray bind: a view of the operation service.</summary>
public sealed class OperationsViewModelTests : IAsyncDisposable
{
    private readonly string _flake = TestConfig.TemporaryFlake();
    private readonly RunicModelContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_flake, recursive: true);
    }

    [Test]
    public async Task It_follows_an_operation_with_its_live_log_and_history()
    {
        var release = new TaskCompletionSource();
        var runner = new FakeRunner(async (command, output, _) =>
        {
            output(new TextOutput("stdout", $"running {command.Label}\n"));
            await release.Task;
            return new CommandResult(0, "", "");
        });
        await using var service = new OperationService(TestConfig.Create(_flake), runner, new FakeUpdates(new UpdateReport { CheckedAt = "" }));
        var viewModel = await _context.InvokeAsync(() => new OperationsViewModel(service, _context));

        await _context.InvokeAsync(() => viewModel.StartCommand.Execute(Request.Create() with { Action = Actions.Build, Scope = Scopes.Home }));
        await Until(() => viewModel.Log.Contains("running Build home configuration"));
        await Assert.That(await _context.InvokeAsync(() => viewModel.Busy)).IsTrue();
        await Assert.That(await _context.InvokeAsync(() => viewModel.Active!.Request.Host)).IsEqualTo("test");

        // A second start while one runs is refused, and the reason shown.
        await _context.InvokeAsync(() => viewModel.StartCommand.Execute(Request.Create() with { Action = Actions.Build }));
        await Assert.That(await _context.InvokeAsync(() => viewModel.StartError)).IsEqualTo("An operation is already running.");

        release.SetResult();
        await Until(() => !viewModel.Busy);
        await Assert.That(await _context.InvokeAsync(() => viewModel.Operations[0].Status)).IsEqualTo(OperationStatuses.Succeeded);

        // Closing the view leaves the service and its operations alone.
        viewModel.Dispose();
        await Assert.That(service.History().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cancel_asks_the_service_to_stop()
    {
        var runner = new FakeRunner(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new CommandResult(0, "", "");
        });
        await using var service = new OperationService(TestConfig.Create(_flake), runner, new FakeUpdates(new UpdateReport { CheckedAt = "" }));
        using var viewModel = await _context.InvokeAsync(() => new OperationsViewModel(service, _context));

        await _context.InvokeAsync(() => viewModel.StartCommand.Execute(Request.Create() with { Action = Actions.Build }));
        await _context.InvokeAsync(() => viewModel.CancelCommand.Execute(null));
        await Until(() => !viewModel.Busy);

        await Assert.That(await _context.InvokeAsync(() => viewModel.Operations[0].Status)).IsEqualTo(OperationStatuses.Cancelled);
    }

    private async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await _context.InvokeAsync(condition))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
