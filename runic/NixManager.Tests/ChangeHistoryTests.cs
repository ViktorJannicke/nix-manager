using System.Diagnostics;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Updates;

namespace NixManager.Tests;

/// <summary>The change history in Git (<c>test/history.test.ts</c>), with real Git in temporary repositories.</summary>
[NotInParallel(nameof(ChangeHistoryTests))]
public sealed class ChangeHistoryTests : IDisposable
{
    // Neither the global nor the system Git configuration, such as commit signing, applies here.
    private static readonly Dictionary<string, string> Isolated = new() { ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1" };
    private readonly List<string> _directories = [];
    private readonly Dictionary<string, string?> _saved = Isolated.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

    public ChangeHistoryTests()
    {
        // readHistory starts Git itself; it inherits the isolation.
        foreach (var (key, value) in Isolated) Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _saved) Environment.SetEnvironmentVariable(key, value);
        foreach (var directory in _directories) Directory.Delete(directory, recursive: true);
    }

    private string Temporary()
    {
        var directory = Native.RealPath(TestConfig.TemporaryFlake())!;
        _directories.Add(directory);
        return directory;
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (key, value) in Isolated) start.Environment[key] = value;
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(cwd);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(errors);
        return output.Trim();
    }

    private static readonly ProcessCommandRunner Runner = new();

    private static async Task<string?> Record(Config config, string[] paths, string[] changes, bool backup = false) =>
        (await ChangeHistory.RecordChangeAsync(config, paths, changes.Select(DisplayText.Plain).ToArray(), backup,
            command => Runner.RunAsync(command with { Quiet = true, Env = new Dictionary<string, string>(command.Env ?? new Dictionary<string, string>()).Concat(Isolated).ToDictionary() }, _ => { }, CancellationToken.None)))?.English;

    [Test]
    public async Task A_message_names_the_first_change_and_lists_all_of_them()
    {
        await Assert.That(ChangeHistory.ChangeMessage([DisplayText.Plain("Install steam")])).IsEqualTo("Install steam");
        await Assert.That(ChangeHistory.ChangeMessage([DisplayText.Plain("Install steam"), DisplayText.Plain("Bluetooth: hardware.bluetooth.enable true")]))
            .IsEqualTo("Install steam and 1 more\n\n- Install steam\n- Bluetooth: hardware.bluetooth.enable true");
        await Assert.That(ChangeHistory.ChangeMessage([])).IsEqualTo("Apply the configuration");
    }

    [Test]
    public async Task An_applied_change_is_committed_alone_with_a_readable_message_and_backed_up()
    {
        var flake = Temporary();
        var config = TestConfig.Create(flake);
        await Assert.That(await Record(config, ["apps.nix"], ["Install steam"])).Contains("not in a Git repository");

        Git(flake, "init", "--quiet", "--initial-branch=main");
        Git(flake, "-c", "user.name=Someone", "-c", "user.email=someone@example.org", "commit", "--quiet", "--allow-empty", "-m", "Start");
        File.WriteAllText(Path.Combine(flake, "apps.nix"), "{ }\n");
        File.WriteAllText(Path.Combine(flake, "notes.txt"), "work in progress\n");
        // Without a Git identity the commit is made in Nix Manager's name.
        await Assert.That(await Record(config, ["apps.nix"], ["Install steam", "Remove vlc"])).IsEqualTo("Kept \"Install steam and 1 more\" in the history.");
        await Assert.That(Git(flake, "show", "--name-only", "--format=%an", "HEAD").Split('\n')).IsEquivalentTo(["Nix Manager", "", "apps.nix"]);
        // Other work stays uncommitted.
        await Assert.That(Git(flake, "status", "--porcelain").Split('\n')).Contains("?? notes.txt");
        var history = await ChangeHistory.ReadAsync(flake);
        await Assert.That(history[0] with { Revision = "", Date = "" }).IsEqualTo(new HistoryEntry
        {
            Revision = "", Date = "", Subject = "Install steam and 1 more", Body = "- Install steam\n- Remove vlc", Author = "Nix Manager", Applied = true,
        });
        await Assert.That((history[1].Subject, history[1].Author, history[1].Applied)).IsEqualTo(("Start", "Someone", false));
        await Assert.That(history[0].Revision).Matches("^[0-9a-f]{40}$");

        // Files the change left as they were need no commit.
        await Assert.That(await Record(config, ["apps.nix"], ["Install steam"])).IsNull();

        File.WriteAllText(Path.Combine(flake, "apps.nix"), "{ programs.steam.enable = true; }\n");
        await Assert.That(await Record(config, ["apps.nix"], ["Install steam"], backup: true)).Contains("the branch has no remote");
        var remote = Temporary();
        Git(remote, "init", "--quiet", "--bare");
        Git(flake, "remote", "add", "origin", remote);
        Git(flake, "push", "--quiet", "--set-upstream", "origin", "main");
        File.WriteAllText(Path.Combine(flake, "apps.nix"), "{ }\n");
        await Assert.That(await Record(config, ["apps.nix"], ["Remove steam"], backup: true)).IsEqualTo("Kept \"Remove steam\" in the history. Backed up to origin/main.");
        await Assert.That(Git(remote, "log", "-1", "--format=%s", "main")).IsEqualTo("Remove steam");
    }

    [Test]
    public async Task Outside_a_Git_repository_there_is_no_history()
    {
        await Assert.That(await ChangeHistory.ReadAsync(Temporary())).IsEmpty();
    }

    [Test]
    public async Task Applying_a_review_keeps_its_files_in_the_history_when_that_is_turned_on()
    {
        var flake = Temporary();
        var config = TestConfig.Create(flake);
        var home = Directory.CreateDirectory(Path.Combine(flake, "home-output")).FullName;
        var calls = new List<Command>();
        var runner = new FakeRunner((command, _, _) =>
        {
            lock (calls) calls.Add(command);
            var argv = command.Argv.ToList();
            if (argv.IndexOf("--output-lock-file") is var lockIndex and >= 0)
                File.WriteAllText(argv[lockIndex + 1], """{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"b"}}}}""");
            if (argv.IndexOf("--out-link") is var link and >= 0) File.CreateSymbolicLink(argv[link + 1], home);
            // The staged lock differs from the last commit.
            return Task.FromResult(new CommandResult(argv[0] == "git" && argv[3] == "diff" ? 1 : 0, "", ""));
        });
        await using var service = new OperationService(config, runner, new FakeUpdates(new UpdateReport { CheckedAt = "" }));
        async Task<List<string>> Apply(string id)
        {
            File.WriteAllText(Path.Combine(flake, "flake.lock"), """{"nodes":{"root":{},"nixpkgs":{"locked":{"rev":"a"}}}}""");
            var built = await service.Start(Request.Create() with { Action = Actions.ReviewBuild, ReviewId = id, Scope = Scopes.Home, Inputs = ["nixpkgs"] }).Completion;
            await Assert.That(built.Status).IsEqualTo(OperationStatuses.Succeeded);
            lock (calls) calls.Clear();
            var applied = await service.Start(Request.Create() with { Action = Actions.ReviewApply, ReviewId = id, Scope = Scopes.Home }).Completion;
            await Assert.That(applied.Status).IsEqualTo(OperationStatuses.Succeeded);
            lock (calls) return calls.Where(c => c.Argv[0] == "git").Select(c => string.Join(' ', c.Argv.Skip(3))).ToList();
        }
        Directory.CreateDirectory(config.StateDir);
        File.WriteAllText(Path.Combine(config.StateDir, "preferences.json"), """{"detail":"detailed"}""");
        await Assert.That(await Apply("aaaaaaaa")).IsEmpty();

        File.WriteAllText(Path.Combine(config.StateDir, "preferences.json"), """{"detail":"detailed","history":true}""");
        var commands = await Apply("bbbbbbbb");
        await Assert.That(commands).Contains(c => c.Contains("commit --quiet --only --message Update nixpkgs --trailer Applied-with: Nix Manager -- flake.lock", StringComparison.Ordinal));
    }
}
