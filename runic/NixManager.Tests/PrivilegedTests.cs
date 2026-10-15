using System.Collections.Immutable;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Privileged;
using Runic.Platform;

namespace NixManager.Tests;

internal sealed class FakePrivilegedActions(Func<PrivilegedActionRequest, PlatformResult<PrivilegedActionCompletion>> answer) : IDesktopPrivilegedActions
{
    public List<PrivilegedActionRequest> Requests { get; } = [];

    public ValueTask<PlatformResult<PrivilegedActionCompletion>> RunAsync(PrivilegedActionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        request.Output?.Invoke(new PrivilegedActionOutput(PrivilegedOutputChannel.StandardOutput, "switching to the new configuration"));
        request.Output?.Invoke(new PrivilegedActionOutput(PrivilegedOutputChannel.StandardError, "warning: something"));
        return ValueTask.FromResult(answer(request));
    }
}

/// <summary>Records the helper's commands instead of running them.</summary>
internal sealed class RecordingTools
{
    public List<string[]> Calls { get; } = [];

    public Task Run(string[] argv)
    {
        Calls.Add(argv);
        return Task.CompletedTask;
    }
}

/// <summary>The privileged steps: the declaration, the Polkit request the application makes, and the helper's checks.</summary>
public sealed class PrivilegedTests
{
    private const string StorePath = "/nix/store/0123456789abcdfghijklmnpqrsvwxyz-nixos-system-desktop-26.05.20261012.abcdef0";

    private static Config Config() => TestConfig.Create() with { Privileged = CliPrefix };

    private static readonly IReadOnlyList<string> CliPrefix = Cli.CliSession.PrivilegedPrefix;

    [Test]
    public async Task One_action_keeps_the_Bun_version_policy()
    {
        var action = PrivilegedActions.Privileged;
        await Assert.That(PrivilegedActions.All.Count()).IsEqualTo(1);
        await Assert.That(action.Id).IsEqualTo("org.nixos.nix-manager");
        await Assert.That(action.Verb).IsEqualTo("--privileged");
        await Assert.That(action.Program).EndsWith("/bin/nix-manager");
        await Assert.That(action.AcceptsArguments(["activate-output", "switch", StorePath])).IsTrue();
        await Assert.That(action.AcceptsArguments(["activate-generation", "81"])).IsTrue();
        await Assert.That(action.AcceptsArguments(["rollback"])).IsTrue();
        await Assert.That(action.AcceptsArguments(["gc", "3"])).IsTrue();
        // No paths outside the store, no options, no shell words, at most three arguments.
        await Assert.That(action.AcceptsArguments(["activate-output", "switch", "/home/user/.local/state/nix-manager/build/system"])).IsFalse();
        await Assert.That(action.AcceptsArguments(["gc", "--max-freed"])).IsFalse();
        await Assert.That(action.AcceptsArguments(["gc", "3; rm -rf /"])).IsFalse();
        await Assert.That(action.AcceptsArguments(["activate-output", "switch", StorePath, "boot"])).IsFalse();
        var policy = File.ReadAllText(Directory.EnumerateFiles(AppContext.BaseDirectory, "org.nixos.nix-manager.policy", SearchOption.AllDirectories).Single());
        await Assert.That(policy).Contains("<allow_active>auth_admin_keep</allow_active>");
        await Assert.That(policy).Contains("<allow_any>auth_admin</allow_any>");
        await Assert.That(policy).DoesNotContain("allow_gui");
    }

    [Test]
    public async Task A_privileged_step_runs_through_Polkit_with_its_output()
    {
        var actions = new FakePrivilegedActions(_ => new PlatformResult<PrivilegedActionCompletion>.Success(new PrivilegedActionCompletion(0)));
        var inner = new FakeRunner();
        var runner = new PrivilegedCommandRunner(Config(), actions, PrivilegedActions.Privileged, inner);
        var output = new List<OutputEvent>();

        await runner.RunAsync(new Command { Label = "Keep 3 system generations and collect garbage", Argv = [.. CliPrefix, "gc", "3"] }, output.Add, CancellationToken.None);
        await runner.RunAsync(new Command { Label = "Collect unreferenced store paths", Argv = ["nix-store", "--gc"] }, output.Add, CancellationToken.None);

        await Assert.That(actions.Requests.Single().Arguments).IsEquivalentTo(ImmutableArray.Create("gc", "3"));
        await Assert.That(inner.Calls.Single().Label).IsEqualTo("Collect unreferenced store paths");
        await Assert.That(output.OfType<TextOutput>().Select(o => (o.Stream, o.Text)))
            .IsEquivalentTo([("stdout", "switching to the new configuration\n"), ("stderr", "warning: something\n")]);
    }

    [Test]
    public async Task A_dismissed_authentication_is_explained()
    {
        var actions = new FakePrivilegedActions(_ => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.UserDismissed));
        var runner = new PrivilegedCommandRunner(Config(), actions, PrivilegedActions.Privileged, new FakeRunner());

        var error = await Assert.That(async () => _ = await runner.RunAsync(new Command { Label = "Roll back system configuration", Argv = [.. CliPrefix, "rollback"] }, _ => { }, CancellationToken.None))
            .Throws<ManagerException>();

        await Assert.That(error!.Message).StartsWith("Roll back system configuration failed (exit 126).");
        await Assert.That(error.Explanation!.Title).IsEqualTo("Authentication was cancelled");
    }

    [Test]
    public async Task A_failing_helper_fails_the_step_with_its_error_output()
    {
        var actions = new FakePrivilegedActions(_ => new PlatformResult<PrivilegedActionCompletion>.Success(new PrivilegedActionCompletion(1)));
        var runner = new PrivilegedCommandRunner(Config(), actions, PrivilegedActions.Privileged, new FakeRunner());

        var error = await Assert.That(async () => _ = await runner.RunAsync(new Command { Label = "Roll back system configuration", Argv = [.. CliPrefix, "rollback"] }, _ => { }, CancellationToken.None))
            .Throws<ManagerException>();

        await Assert.That(error!.Message).IsEqualTo("Roll back system configuration failed (exit 1). warning: something");
    }

    [Test]
    public async Task Garbage_collection_keeps_the_boot_menu_to_the_kept_generations()
    {
        var runner = new RecordingTools();
        var present = new HashSet<string> { $"{Paths.SystemProfile}/bin/switch-to-configuration" };

        await PrivilegedHelper.ExecuteAsync(["gc", "5"], runner.Run, present.Contains, _ => StorePath);

        await Assert.That(runner.Calls.Select(call => string.Join(' ', call))).IsEquivalentTo([
            $"nix-env --profile {Paths.SystemProfile} --delete-generations +5",
            $"{StorePath}/bin/switch-to-configuration boot",
            "nix-store --gc",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Activation_uses_only_the_resolved_system_store_path()
    {
        var runner = new RecordingTools();
        var present = new HashSet<string> { "/home/user/build/system", $"{StorePath}/bin/switch-to-configuration" };

        await PrivilegedHelper.ExecuteAsync(["activate-output", "switch", "/home/user/build/system"], runner.Run, present.Contains, _ => StorePath);

        await Assert.That(runner.Calls[0]).IsEquivalentTo(["nix-env", "--profile", Paths.SystemProfile, "--set", StorePath]);
        await Assert.That(runner.Calls[1]).IsEquivalentTo([$"{StorePath}/bin/switch-to-configuration", "switch"]);
    }

    [Test]
    [Arguments(new[] { "gc", "0" }, "Invalid generation retention.")]
    [Arguments(new[] { "gc", "101" }, "Invalid generation retention.")]
    [Arguments(new[] { "activate-output", "test", StorePath }, "Invalid activation mode.")]
    [Arguments(new[] { "activate-output", "switch", "/nix/store/0123456789abcdfghijklmnpqrsvwxyz-hello" }, "Invalid NixOS system output.")]
    [Arguments(new[] { "activate-generation", "12" }, "Invalid system generation.")]
    [Arguments(new[] { "rollback", "now" }, "Unknown privileged operation or unexpected arguments.")]
    [Arguments(new[] { "switch" }, "Unknown privileged operation or unexpected arguments.")]
    public async Task The_helper_rejects_what_the_Bun_helper_rejects(string[] arguments, string message)
    {
        var runner = new RecordingTools();
        var present = new HashSet<string> { $"{StorePath}/bin/switch-to-configuration", "/nix/store/0123456789abcdfghijklmnpqrsvwxyz-hello" };

        await Assert.That(() => PrivilegedHelper.ExecuteAsync(arguments, runner.Run, present.Contains, path => path))
            .Throws<ManagerException>().WithMessage(message);
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task The_helper_refuses_to_run_without_pkexec()
    {
        // Not root: PolkitHelper.Match fails closed before anything runs.
        await Assert.That(await PrivilegedHelper.RunAsync(["--privileged", "gc", "3"])).IsEqualTo(1);
    }
}
