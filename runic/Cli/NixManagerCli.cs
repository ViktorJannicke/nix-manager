using NixManager.Configuration;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Updates;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Processes;
using Runic.Platform.Linux.Polkit;

namespace NixManager.Cli;

/// <summary>The process-wide parts an invocation runs with, replaceable in tests.</summary>
public sealed class CliSession
{
    public ManagerEnvironment Environment { get; init; } = ManagerEnvironment.Current;
    public ICommandRunner Runner { get; init; } = new ProcessCommandRunner();
    /// <summary>
    /// Creates the client for a loaded configuration; by default the running daemon's, or an operation service in this
    /// process when none runs.
    /// </summary>
    public Func<Config, ICommandRunner, IOperationClient> CreateClient { get; init; } = DefaultClient;
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Runs processes whose output goes to a file, such as the package catalog's evaluation.</summary>
    public IProcessRunner Processes { get; init; } = new ProcessRunner(new LocalExecutablePolicy());
    /// <summary>
    /// Runs <c>ui</c> on the thread that called <see cref="NixManagerCli.Run(string[], CliSession, ICommandConsole, bool, CancellationToken)"/>, once the command line has returned, and
    /// returns its exit code; the dashboard's Desktop host must start there before any <see langword="await"/>.
    /// </summary>
    public Func<Dashboard.DashboardCommand.Invocation, int> Dashboard { get; init; } = global::NixManager.Dashboard.DashboardCommand.Run;
    /// <summary>Runs <c>tray</c> like <see cref="Dashboard"/>.</summary>
    public Func<Daemon.DaemonCommand.Invocation, int> Daemon { get; init; } = global::NixManager.Daemon.DaemonCommand.Run;

    /// <summary>The privileged entry point as plans show it: <c>pkexec PROGRAM --privileged</c>.</summary>
    public static IReadOnlyList<string> PrivilegedPrefix =>
        ["/run/wrappers/bin/pkexec", PrivilegedActions.Privileged.Program, PrivilegedActions.Privileged.Verb!];

    public static IOperationClient DefaultClient(Config config, ICommandRunner runner)
    {
        var privileged = new PrivilegedCommandRunner(config,
            PolkitPrivilegedActions.Create(PrivilegedActions.All), PrivilegedActions.Privileged, runner);
        // The operation goes to the daemon when one runs for this configuration (D025.4), and runs here otherwise.
        return new Daemon.RoutingOperationClient(
            new LocalOperationClient(new OperationService(config, privileged, new UpdateChecker(new HttpNetwork()))));
    }
}

/// <summary>
/// The command line on Runic Command Line. The root command takes scope, modifier and action words in any order,
/// its options before or after them, and native arguments after <c>--</c>; <c>software</c> edits the Software
/// page's pending edits. Results are typed (<c>--output json</c> writes them in a <c>runic.commandline/1</c>
/// envelope), and exit codes are Runic Command Line's defaults.
/// </summary>
public static class NixManagerCli
{
    public const string Name = "nix-manager";

    private const string Description =
        "Manage NixOS and Home Manager together. Modifiers may appear before the action, for example: nix-manager home update cleanup switch.\n\n" +
        "All selected configurations build before activation. Home activates first; system activation uses Polkit. Arguments after -- are forwarded verbatim to native tools.";

    private const string FlakeHelp = "Use a specific flake checkout", HostHelp = "Select a registered host configuration",
        PlainHelp = "Disable dynamic terminal presentation", DryRunHelp = "Preview without making changes";

    /// <summary>
    /// The command catalog. The options every command uses are global, so they may also precede the command
    /// (<c>nix-manager --host laptop software pending</c>). The other options belong to their command and follow its
    /// name: a global <c>--dry-run</c> would be accepted, and ignored, by the software commands that edit the draft.
    /// </summary>
    public static CommandCatalog Catalog() =>
        GeneratedCommandCatalog.Create(builder => CliPresenters.Register(builder
            .GlobalOption("flake", "--flake", CommandArity.ExactlyOne, new CommandHelp(FlakeHelp, "PATH"))
            .GlobalOption("host", "--host", CommandArity.ExactlyOne, new CommandHelp(HostHelp))
            .GlobalOption("plain", "--plain", CommandArity.Zero, new CommandHelp(PlainHelp))));

    /// <summary>
    /// Runs the command line with the process arguments and returns the exit code. Call it from <c>Main</c> before any
    /// <see langword="await"/>: <c>ui</c> and <c>tray</c> start the Desktop host on this thread once the command line has
    /// returned (<see cref="CommandExecutionContext.ContinueOnCallingThread"/>).
    /// </summary>
    public static int Run(string[] args, CliSession? session = null, ICommandConsole? console = null,
        bool handleSignals = true, CancellationToken cancellationToken = default) =>
        Create(session, console, handleSignals).Run(args, cancellationToken);

    /// <summary>Runs the command line as <see cref="Run(string[], CliSession, ICommandConsole, bool, CancellationToken)"/> does, except <c>ui</c> and <c>tray</c>, which need the calling thread.</summary>
    public static async Task<int> RunAsync(string[] args, CliSession? session = null, ICommandConsole? console = null,
        bool handleSignals = true, CancellationToken cancellationToken = default) =>
        await Create(session, console, handleSignals).RunAsync(args, cancellationToken).ConfigureAwait(false);

    private static CommandApp Create(CliSession? session, ICommandConsole? console, bool handleSignals) =>
        new(Catalog())
        {
            Name = Name,
            Version = "1.0.0",
            Console = console ?? new SystemCommandConsole(),
            ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With(session ?? new CliSession())),
            HandleCancelKeyPress = handleSignals,
        };

    [Command("run", Description = Description,
        Examples = ["nix-manager home update cleanup switch", "nix-manager --dry-run update cleanup switch", "nix-manager check --output json"])]
    [RootCommand]
    [CommandResult(RunResult.PayloadType, typeof(ManagerJsonContext))]
    internal static async Task<CommandOutcome<RunResult>> Run(
        [Argument(Description = "Scope, modifier and action words, in any order",
            ValueName = "WORD", AllowMultipleValues = true, Choices = ["home", "system", "all", "update", "cleanup", "gc", "build", "switch", "boot", "rollback", "check",
                "t3code-update", "opencode-update", "pince-update", "ui", "tray", "status", "history"])]
        string[]? words,
        [TrailingArguments(Description = "Arguments forwarded verbatim to native tools", ValueName = "ARG")] string[]? native,
        [FromServices] CliSession session,
        CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--dry-run", Description = DryRunHelp)] bool dryRun = false,
        [Option("--force", Description = "Recalculate package hashes even when the pin is current")] bool force = false,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--verbose", Description = "Retain detailed command output on success")] bool verbose = false,
        [Option("--browser", Description = "Open the dashboard in the default browser")] bool browser = false,
        [Option("--no-open", Description = "Serve the dashboard without opening a window")] bool noOpen = false,
        [Option("--interval", Description = "Tray update-check interval in minutes (5–10080)", ValueName = "MINUTES")] int interval = 360,
        CancellationToken cancellationToken = default)
    {
        var json = context.OutputMode == CommandOutputMode.Json;
        CliInvocation cli;
        try
        {
            cli = CliArguments.Resolve(words ?? [], new CliOptions(flake, host, dryRun, force, json, plain, verbose, browser, noOpen, interval), native ?? []);
        }
        catch (ManagerException error)
        {
            return CliFaults.Failure<RunResult>(CommandExitCategory.Usage, CliFaults.InvalidCommandLine, error);
        }
        return await CliHandler.ExecuteAsync(cli, session, context, cancellationToken).ConfigureAwait(false);
    }
}
