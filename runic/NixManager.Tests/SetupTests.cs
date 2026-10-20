using NixManager.Configuration;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Setup;
using NixManager.ViewModels;
using Runic.Navigation;

namespace NixManager.Tests;

/// <summary>The setup assistant (<c>test/setup.test.ts</c>), its checkout creation with a fake runner, and its state machine.</summary>
public sealed class SetupTests : IDisposable
{
    private readonly string _root = Operations.Native.RealPath(Directory.CreateTempSubdirectory("nix-manager-setup-").FullName)!;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly SetupInfo Info = new()
    {
        User = "alice",
        Home = "/home/alice",
        HostName = "alice-pc",
        System = "x86_64-linux",
        Release = "25.05",
        Unstable = false,
        Revision = "abc",
        StateVersion = "24.11",
        Flakes = false,
        Files = ["configuration.nix", "hardware-configuration.nix"],
        Suggested = "/home/alice/nixos-config",
    };

    private string Home => Path.Combine(_root, "home");
    private string Source => Path.Combine(_root, "etc-nixos");

    private ManagerEnvironment Environment(Dictionary<string, string?>? variables = null)
    {
        variables ??= [];
        variables.TryAdd("NIX_MANAGER_STATE_DIR", Path.Combine(_root, "state"));
        return new ManagerEnvironment(name => variables.GetValueOrDefault(name), "alice-pc", "alice", Home);
    }

    [Test]
    public async Task The_new_flake_is_found_again_by_host_detection()
    {
        var flake = SetupAssistant.FlakeTemplate(Info, SetupAssistant.Branches(Info.Release, Info.Unstable)[0]);
        await Assert.That(flake).Contains("nixpkgs.url = \"github:NixOS/nixpkgs/nixos-25.05\"");
        await Assert.That(flake).Contains("url = \"github:nix-community/home-manager/release-25.05\"");
        await Assert.That(flake).Contains("nixosConfigurations.alice-pc = ");
        await Assert.That(flake).Contains("homeConfigurations.\"alice@alice-pc\" = ");
        await Assert.That(flake).Contains("\"flakes\"");
        await Assert.That(flake).Contains("nixpkgs.hostPlatform = nixpkgs.lib.mkDefault \"x86_64-linux\";");
        await Assert.That(flake).Contains("pkgs = nixpkgs.legacyPackages.x86_64-linux;");
        await Assert.That(flake).StartsWith("{\n  description = \"alice-pc: system and home configuration\";\n");
        await Assert.That(flake).EndsWith("    };\n}\n");
        await Assert.That(ConfigLoader.PickSystem(Info.HostName, ["alice-pc"])).IsEqualTo("alice-pc");
        await Assert.That(ConfigLoader.PickHome(Info.User, [Info.HostName], ["alice@alice-pc"])).IsEqualTo("alice@alice-pc");
        var home = SetupAssistant.HomeTemplate(Info);
        await Assert.That(home).Contains("home.stateVersion = \"24.11\";");
        await Assert.That(home).Contains("home.homeDirectory = \"/home/alice\";");
        await Assert.That(home).IsEqualTo("""
            {
              home.username = "alice";
              home.homeDirectory = "/home/alice";
              # The release this home configuration started on; leave it as it is.
              home.stateVersion = "24.11";

              programs.home-manager.enable = true;
            }

            """);
    }

    [Test]
    public async Task Unstable_systems_follow_the_unstable_branches_and_releases_fall_back_to_them()
    {
        await Assert.That(SetupAssistant.Branches("25.11", true)).IsEquivalentTo(new[] { new SetupBranch("nixos-unstable", "master") });
        await Assert.That(SetupAssistant.Branches("25.05", false).Select(branch => branch.Nixpkgs).ToArray())
            .IsEquivalentTo(new[] { "nixos-25.05", "nixos-unstable" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_checkout_goes_into_a_new_or_empty_folder_in_the_home_folder()
    {
        var home = Home;
        Directory.CreateDirectory(home);
        await Assert.That(SetupAssistant.TargetProblem(Path.Combine(home, "config"), home)).IsNull();
        Directory.CreateDirectory(Path.Combine(home, "empty"));
        await Assert.That(SetupAssistant.TargetProblem(Path.Combine(home, "empty"), home)).IsNull();
        await Assert.That(SetupAssistant.TargetProblem(Path.Combine(home, "empty") + "/", home)).IsNull();
        Directory.CreateDirectory(Path.Combine(home, "full"));
        File.WriteAllText(Path.Combine(home, "full", "x"), "");
        await Assert.That(SetupAssistant.TargetProblem(Path.Combine(home, "full"), home)?.English).IsEqualTo("This folder is not empty.");
        await Assert.That(SetupAssistant.TargetProblem("/etc/nixos", home)?.English).IsEqualTo("Choose a folder inside your home folder, so you own the files.");
        await Assert.That(SetupAssistant.TargetProblem(home, home)?.English).IsEqualTo("Choose a new folder inside your home folder.");
        await Assert.That(SetupAssistant.TargetProblem(home + "/", home)?.English).IsEqualTo("Choose a new folder inside your home folder.");
        await Assert.That(SetupAssistant.TargetProblem("config", home)?.English).IsEqualTo("Use a full path, such as ~/nixos-config.");
        await Assert.That(SetupAssistant.TargetProblem($"{home}/../elsewhere", home)?.English).IsEqualTo("Choose a folder inside your home folder, so you own the files.");
        await Assert.That(SetupAssistant.TargetProblem($"{home}x/config", home)?.English).IsEqualTo("Choose a folder inside your home folder, so you own the files.");
    }

    [Test]
    public async Task Channel_imports_are_found_in_code_not_in_comments()
    {
        const string text = """
            { imports = [ <home-manager/nixos> ./hardware.nix ];
              # networking.interfaces.<interface>.useDHCP
              /* <nixos-hardware> */ x = import <nixpkgs> { }; }
            """;
        await Assert.That(SetupAssistant.ChannelPaths(text)).IsEquivalentTo(new[] { "<home-manager/nixos>", "<nixpkgs>" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    private void WriteSource(bool flake = false)
    {
        Directory.CreateDirectory(Path.Combine(Source, "modules", ".git"));
        File.WriteAllText(Path.Combine(Source, "configuration.nix"),
            "{ imports = [ ./hardware-configuration.nix <home-manager/nixos> ]; system.stateVersion = \"24.11\"; }");
        File.WriteAllText(Path.Combine(Source, "hardware-configuration.nix"), "{ x = import <nixpkgs> { }; }");
        File.WriteAllText(Path.Combine(Source, "modules", "extra.nix"), "{ y = <nixpkgs>; }");
        File.WriteAllText(Path.Combine(Source, "modules", ".git", "HEAD"), "");
        File.WriteAllText(Path.Combine(Source, "README"), "notes");
        File.CreateSymbolicLink(Path.Combine(Source, "result"), "/nix/store/whatever");
        if (flake) File.WriteAllText(Path.Combine(Source, "flake.nix"), "{}");
    }

    private static FakeRunner Computer(string version = """{"nixosVersion":"25.05.20250101.abc (Warbler)","nixpkgsRevision":"0123abc"}""",
        string features = "fetch-tree flakes nix-command", Func<Command, CommandResult?>? more = null) =>
        new((command, _, _) => Task.FromResult(command.Argv[0] switch
        {
            "nixos-version" => new CommandResult(0, version, ""),
            "nix" when command.Argv[1] == "config" => new CommandResult(0, features + "\n", ""),
            _ => more?.Invoke(command) ?? throw new InvalidOperationException($"Unexpected {string.Join(' ', command.Argv)}"),
        }));

    [Test]
    public async Task The_assistant_reads_the_classic_configuration_and_this_computer()
    {
        WriteSource();
        // Files the user cannot read stay behind, and are named.
        File.WriteAllText(Path.Combine(Source, "secrets.nix"), "");
        File.SetUnixFileMode(Path.Combine(Source, "secrets.nix"), UnixFileMode.None);
        Directory.CreateDirectory(Path.Combine(Source, "private"));
        File.SetUnixFileMode(Path.Combine(Source, "private"), UnixFileMode.None);
        var runner = Computer();
        SetupInfo info;
        try { info = await SetupAssistant.ReadAsync(runner, Environment(), Source); }
        finally { File.SetUnixFileMode(Path.Combine(Source, "private"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        await Assert.That(info.Files).IsEquivalentTo(new[] { "README", "configuration.nix", "hardware-configuration.nix", "modules/extra.nix" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        if (System.Environment.UserName != "root")
            await Assert.That(info.Unreadable).IsEquivalentTo(new[] { "private/", "secrets.nix" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(info.ChannelImports).IsEquivalentTo(new[] { "<home-manager/nixos>", "<nixpkgs>" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(info.Release).IsEqualTo("25.05");
        await Assert.That(info.Unstable).IsFalse();
        await Assert.That(info.Revision).IsEqualTo("0123abc");
        await Assert.That(info.StateVersion).IsEqualTo("24.11");
        await Assert.That(info.Flakes).IsTrue();
        await Assert.That(info.Blocked).IsNull();
        await Assert.That(info.User).IsEqualTo("alice");
        await Assert.That(info.HostName).IsEqualTo("alice-pc");
        await Assert.That(info.Suggested).IsEqualTo(Path.Combine(Home, "nixos-config"));
        // The flakes check asks the user's own configuration, without what this process added.
        await Assert.That(runner.Calls.Single(c => c.Label.English == "Check flakes").Env!["NIX_CONFIG"]).IsEqualTo(SetupAssistant.UserNixConfig ?? "");

        var unstable = await SetupAssistant.ReadAsync(Computer("""{"nixosVersion":"25.11pre123.abc"}""", "nix-command"), Environment(), Source);
        await Assert.That(unstable.Unstable).IsTrue();
        await Assert.That(unstable.Flakes).IsFalse();
        await Assert.That(unstable.Revision).IsNull();
    }

    [Test]
    public async Task Setup_is_blocked_without_NixOS_a_configuration_or_with_a_flake()
    {
        var missing = await SetupAssistant.ReadAsync(Computer(), Environment(), Source);
        await Assert.That(missing.Blocked?.English).IsEqualTo($"There is no {Source}/configuration.nix to start from.");
        await Assert.That(missing.Files).IsEmpty();
        var notNixos = new FakeRunner((command, _, _) => Task.FromResult(new CommandResult(command.Argv[0] == "nixos-version" ? 127 : 1, "", "")));
        await Assert.That((await SetupAssistant.ReadAsync(notNixos, Environment(), Source)).Blocked?.English).IsEqualTo("This does not look like NixOS: nixos-version is missing.");
        WriteSource(flake: true);
        await Assert.That((await SetupAssistant.ReadAsync(Computer(), Environment(), Source)).Blocked?.English).IsEqualTo(
            $"{Source} is a flake already. Copy it to a folder you own and add a homeConfigurations entry for your user, then choose that folder with --flake.");
    }

    /// <summary>A runner for creating the checkout; the first lock fails when <paramref name="lockFailures"/> says so.</summary>
    private static FakeRunner Creation(int lockFailures = 0, string? failing = null)
    {
        var locks = 0;
        return new FakeRunner((command, _, _) =>
        {
            if (command.Label.English == failing) return Task.FromResult(new CommandResult(1, "", "warning\nerror: it broke\n"));
            if (command.Argv is ["nix", "flake", "lock", ..])
            {
                if (locks++ < lockFailures) return Task.FromResult(new CommandResult(1, "", $"error: no branch {locks}\n"));
                File.WriteAllText(Path.Combine(command.Cwd!, "flake.lock"), "{}");
            }
            if (command.Argv is ["nix", "eval", "--json", ..])
                return Task.FromResult(new CommandResult(0, command.Argv[3].EndsWith("#nixosConfigurations", StringComparison.Ordinal) ? """["alice-pc"]""" : """["alice@alice-pc"]""", ""));
            return Task.FromResult(new CommandResult(0, "", ""));
        });
    }

    private SetupInfo SourceInfo() => Info with
    {
        Home = Home,
        Files = ["configuration.nix", "hardware-configuration.nix", "modules/extra.nix"],
        Suggested = Path.Combine(Home, "nixos-config"),
    };

    [Test]
    public async Task Creating_the_checkout_copies_writes_pins_checks_and_remembers_it()
    {
        WriteSource();
        var target = Path.Combine(Home, "nixos-config");
        var runner = Creation(lockFailures: 1);
        var steps = new List<string>();
        var environment = Environment();

        var path = await SetupAssistant.CreateCheckoutAsync(SourceInfo(), target, steps.Add, runner, environment, Source);

        await Assert.That(path).IsEqualTo(target);
        await Assert.That(steps).IsEquivalentTo(SetupSteps.All.Select(step => step.Id).ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(File.ReadAllText(Path.Combine(target, "modules", "extra.nix"))).IsEqualTo("{ y = <nixpkgs>; }");
        // The release branch could not be locked, so the flake follows nixos-unstable.
        await Assert.That(File.ReadAllText(Path.Combine(target, "flake.nix"))).IsEqualTo(SetupAssistant.FlakeTemplate(SourceInfo(), new SetupBranch("nixos-unstable", "master")));
        await Assert.That(File.ReadAllText(Path.Combine(target, "home.nix"))).IsEqualTo(SetupAssistant.HomeTemplate(SourceInfo()));
        var locks = runner.Calls.Where(c => c.Label.English == "Pin the inputs").ToList();
        await Assert.That(locks.Count).IsEqualTo(2);
        await Assert.That(locks[0].Argv).IsEquivalentTo(new[] { "nix", "flake", "lock", "--override-input", "nixpkgs", "github:NixOS/nixpkgs/abc" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(runner.Calls.Single(c => c.Label.English == "Add the files to Git").Argv).IsEquivalentTo(
            new[] { "git", "add", "--", "configuration.nix", "hardware-configuration.nix", "modules/extra.nix", "flake.nix", "home.nix" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(runner.Calls.Single(c => c.Label.English == "Evaluate the system").Argv[^1])
            .IsEqualTo($"{target}#nixosConfigurations.alice-pc.config.system.build.toplevel.drvPath");
        await Assert.That(runner.Calls.Single(c => c.Label.English == "Evaluate the home configuration").Argv[^1])
            .IsEqualTo($"{target}#homeConfigurations.\"alice@alice-pc\".activationPackage.drvPath");
        await Assert.That(runner.Calls.Single(c => c.Label.English == "Commit").Argv[^1]).IsEqualTo($"Start from {Source}");
        await Assert.That(runner.Calls.Where(c => c.Cwd is not null).All(c => c.Cwd == target)).IsTrue();
        await Assert.That(File.ReadAllText(new ConfigLoader(runner, environment).RememberedCheckout)).IsEqualTo($"{target}\n");
    }

    [Test]
    public async Task A_failed_creation_leaves_nothing_behind_and_says_why()
    {
        WriteSource();
        var target = Path.Combine(Home, "nixos-config");
        await Assert.That(() => SetupAssistant.CreateCheckoutAsync(SourceInfo(), target, _ => { }, Creation(lockFailures: 2), Environment(), Source))
            .Throws<ManagerException>().WithMessage("Could not pin the inputs: error: no branch 2");
        await Assert.That(Directory.Exists(target)).IsFalse();
        await Assert.That(() => SetupAssistant.CreateCheckoutAsync(SourceInfo(), target, _ => { }, Creation(failing: "Evaluate the system"), Environment(), Source))
            .Throws<ManagerException>().WithMessage("Evaluate the system: error: it broke");
        await Assert.That(Directory.Exists(target)).IsFalse();
        await Assert.That(() => SetupAssistant.CreateCheckoutAsync(SourceInfo() with { Blocked = DisplayText.Plain("No.") }, target, _ => { }, Creation(), Environment(), Source))
            .Throws<ManagerException>().WithMessage("No.");
        await Assert.That(() => SetupAssistant.CreateCheckoutAsync(SourceInfo(), "relative", _ => { }, Creation(), Environment(), Source))
            .Throws<ManagerException>().WithMessage("Use a full path, such as ~/nixos-config.");
    }

    [Test]
    public async Task The_service_reports_each_step_and_refuses_while_it_runs()
    {
        WriteSource();
        var release = new TaskCompletionSource();
        var computer = Computer();
        var creation = Creation();
        var runner = new FakeRunner(async (command, output, token) =>
        {
            if (command.Label.English is "Read NixOS version" or "Check flakes") return await computer.RunAsync(command, output, token);
            if (command.Label.English == "Start Git") await release.Task;
            return await creation.RunAsync(command, output, token);
        });
        var service = new SetupAssistantService(runner, Environment(), source: Source);
        await service.Loaded;
        var created = new TaskCompletionSource<string>();
        service.Created += created.SetResult;
        await Assert.That(service.State.Info!.Files.Count).IsEqualTo(4);
        await Assert.That(service.State.Steps.All(step => step.Status == SetupAssistantService.StatusWaiting)).IsTrue();
        await Assert.That(() => service.Create(Path.Combine(_root, "elsewhere"))).Throws<ManagerException>()
            .WithMessage("Choose a folder inside your home folder, so you own the files.");

        var state = service.Create("~/nixos-config");
        await Assert.That(state.Running).IsTrue();
        await Assert.That(state.Target).IsEqualTo(Path.Combine(Home, "nixos-config"));
        await Assert.That(() => service.Create("~/other")).Throws<ManagerException>().WithMessage("Setup is already running.");
        await Assert.That(service.RefreshAsync).Throws<ManagerException>().WithMessage("Setup is already running.");
        for (var i = 0; i < 500 && service.State.Steps[2].Status != SetupAssistantService.StatusRunning; i++) await Task.Delay(10);
        await Assert.That(service.State.Steps.Select(step => step.Status).ToArray()).IsEquivalentTo(
            new[] { "done", "done", "running", "waiting", "waiting", "waiting" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        release.SetResult();
        await service.Completion;

        await Assert.That(await created.Task).IsEqualTo(Path.Combine(Home, "nixos-config"));
        await Assert.That(service.State.Running).IsFalse();
        await Assert.That(service.State.Error).IsNull();
        await Assert.That(service.State.Steps.All(step => step.Status == SetupAssistantService.StatusDone)).IsTrue();
        service.HandOver("http://127.0.0.1:1234/");
        await Assert.That(service.State.Next).IsEqualTo("http://127.0.0.1:1234/");
        await Assert.That(() => service.Create("~/again")).Throws<ManagerException>().WithMessage("Setup is already running.");
    }

    [Test]
    public async Task A_failed_step_is_marked_and_a_problem_offers_nothing_else()
    {
        WriteSource();
        var computer = Computer(more: command => command.Label.English == "Start Git" ? new CommandResult(128, "", "fatal: no git\n") : new CommandResult(0, "", ""));
        var service = new SetupAssistantService(computer, Environment(), source: Source);
        await service.Loaded;
        service.Create("~/nixos-config");
        await service.Completion;
        await Assert.That(service.State.Error?.English).IsEqualTo("Start Git: fatal: no git");
        await Assert.That(service.State.Steps.Select(step => step.Status).Take(3).ToArray()).IsEquivalentTo(
            new[] { "done", "done", "failed" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Directory.Exists(Path.Combine(Home, "nixos-config"))).IsFalse();
        // Reading again clears the error.
        await Assert.That((await service.RefreshAsync()).Error).IsNull();

        var problem = new SetupAssistantService(new FakeRunner(), Environment(), new SetupProblem(DisplayText.Plain("Broken flake."), "/home/alice/flake"), Source);
        await Assert.That(problem.State.Info).IsNull();
        await Assert.That(problem.State.Problem!.Message.English).IsEqualTo("Broken flake.");
        await Assert.That(() => problem.Create("~/nixos-config")).Throws<ManagerException>().WithMessage("Nix Manager could not start.");
        await Assert.That(problem.RefreshAsync).Throws<ManagerException>().WithMessage("Nix Manager could not start.");
    }

    [Test]
    public async Task The_setup_ViewModel_suggests_the_folder_and_follows_the_service()
    {
        WriteSource();
        await using var context = new RunicModelContext();
        var service = new SetupAssistantService(Computer(more: _ => new CommandResult(0, "", "")), Environment(), source: Source);
        await service.Loaded;
        using var viewModel = await context.InvokeAsync(() => new SetupViewModel(service, context));
        await Assert.That(await context.InvokeAsync(() => viewModel.Target)).IsEqualTo(Path.Combine(Home, "nixos-config"));
        await Assert.That(await context.InvokeAsync(() => viewModel.CanCreate)).IsTrue();
        await context.InvokeAsync(() => viewModel.Target = "relative");
        await context.InvokeAsync(() => viewModel.CreateCommand.Execute(null));
        await Assert.That(await context.InvokeAsync(() => viewModel.RequestError?.English)).IsEqualTo("Use a full path, such as ~/nixos-config.");
    }

    [Test]
    public async Task Ui_starts_with_the_assistant_only_without_any_checkout()
    {
        // Not in a configuration's Git checkout, nothing remembered: ~/my-flakes, which has no flake.nix yet.
        var notGit = new FakeRunner((_, _, _) => Task.FromResult(new CommandResult(128, "", "fatal: not a git repository")));
        var checkout = SetupLaunch.Checkout(null, notGit, Environment());
        await Assert.That(checkout).IsEqualTo((Path.Combine(Home, "my-flakes"), false));
        await Assert.That(SetupLaunch.NeedsSetup(null, notGit, Environment())).IsTrue();
        // Nor is there one without Git, as on a computer the assistant has not set up yet.
        var noGit = new FakeRunner((_, _, _) => throw new ManagerException(DisplayText.Of("stored_cannot_start", ("command", "git"), ("error", "No such file or directory"))));
        await Assert.That(SetupLaunch.NeedsSetup(null, noGit, Environment())).IsTrue();
        // One asked for is used, whether it exists or not: loading it says what is wrong.
        await Assert.That(SetupLaunch.NeedsSetup("/nowhere", notGit, Environment())).IsFalse();
        await Assert.That(SetupLaunch.NeedsSetup(null, notGit, Environment(new() { ["NIX_MANAGER_FLAKE"] = "/nowhere" }))).IsFalse();
        // The checkout the assistant created and remembered.
        var created = Path.Combine(_root, "created");
        Directory.CreateDirectory(created);
        File.WriteAllText(Path.Combine(created, "flake.nix"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        File.WriteAllText(Path.Combine(_root, "state", "checkout"), created + "\n");
        await Assert.That(SetupLaunch.NeedsSetup(null, notGit, Environment())).IsFalse();
    }

    [Test]
    public async Task A_configuration_that_does_not_load_is_shown_in_a_Window_only_when_started_from_the_menu()
    {
        await Assert.That(SetupLaunch.ShowsProblem(noOpen: false, outputRedirected: true)).IsTrue();
        await Assert.That(SetupLaunch.ShowsProblem(noOpen: false, outputRedirected: false)).IsFalse();
        await Assert.That(SetupLaunch.ShowsProblem(noOpen: true, outputRedirected: true)).IsFalse();
    }
}
