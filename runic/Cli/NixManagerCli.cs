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

/// <summary>What one invocation writes when it ends; the outcome sink writes it (<see cref="CliOutcomeSink"/>).</summary>
public sealed class CliOutput
{
    /// <summary>The JSON document for <c>--json</c>, written to standard output as it is.</summary>
    public string? Json { get; set; }
    /// <summary>Text for standard output in human mode.</summary>
    public string? Human { get; set; }
    /// <summary>The failure was presented already (an operation's own log and step list).</summary>
    public bool Presented { get; set; }
    public Explanation? Explanation { get; set; }
}

/// <summary>The process-wide parts an invocation runs with, replaceable in tests.</summary>
public sealed class CliSession
{
    public required CliOutput Output { get; init; }
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
    /// Runs <c>ui</c> on the thread that called <see cref="NixManagerCli.Run"/>, once the command line has returned, and
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
/// The command line on Runic Command Line, with the Bun version's grammar: scope, modifier and action words in
/// any order, <c>--json</c>, <c>--dry-run</c> and the other options before or after them, and native arguments
/// after <c>--</c>.
/// </summary>
public static class NixManagerCli
{
    public const string Name = "nix-manager";

    private const string Description =
        "Manage NixOS and Home Manager together. Modifiers may appear before the action, for example: nix-manager home update cleanup switch.\n\n" +
        "All selected configurations build before activation. Home activates first; system activation uses Polkit. Arguments after -- are forwarded verbatim to native tools.";

    /// <summary>The command catalog: <c>--json</c> as the output alias, and <c>--completions SHELL</c> as the Bun version's
    /// completion option instead of Runic Command Line's <c>completion</c> command.</summary>
    public static CommandCatalog Catalog() =>
        GeneratedCommandCatalog.Create(builder => builder.JsonOutputAlias("--json").CompletionOption("--completions"));

    /// <summary>
    /// Runs the command line with the process arguments and returns the exit code. Call it from <c>Main</c> before any
    /// <see langword="await"/>: <c>ui</c> and <c>tray</c> start the Desktop host on this thread once the command line has
    /// returned (<see cref="CommandExecutionContext.ContinueOnCallingThread"/>).
    /// </summary>
    public static int Run(string[] args, CliSession? session = null, ICommandConsole? console = null,
        bool handleSignals = true, CancellationToken cancellationToken = default)
    {
        var (app, arguments) = Create(args, session, console, handleSignals);
        return app.Run(arguments, cancellationToken);
    }

    /// <summary>Runs the command line as <see cref="Run"/> does, except <c>ui</c> and <c>tray</c>, which need the calling thread.</summary>
    public static async Task<int> RunAsync(string[] args, CliSession? session = null, ICommandConsole? console = null,
        bool handleSignals = true, CancellationToken cancellationToken = default)
    {
        var (app, arguments) = Create(args, session, console, handleSignals);
        return await app.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private static (CommandApp App, string[] Arguments) Create(string[] args, CliSession? session, ICommandConsole? console, bool handleSignals)
    {
        var arguments = args;
        var isSoftware = false;
        // `software ...` has its own grammar: options before `software` or after the arguments, and `--` before
        // positional arguments (src/software-cli.ts).
        if (SoftwareCli.Arguments(args) is { } software) (arguments, isSoftware) = (SoftwareCli.Normalize(software), true);
        session ??= new CliSession { Output = new CliOutput() };
        var output = session.Output;
        var app = new CommandApp(Catalog())
        {
            Name = Name,
            Version = "1.0.0",
            Console = console ?? new SystemCommandConsole(),
            ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With(session)),
            OutcomeSink = isSoftware ? new SoftwareOutcomeSink(output) : new CliOutcomeSink(output),
            ExitCodePolicy = new CliExitCodes(),
            HandleCancelKeyPress = handleSignals,
        };
        return (app, arguments);
    }

    [Command("run", Description = Description,
        Examples = ["nix-manager home update cleanup switch", "nix-manager --dry-run update cleanup switch", "nix-manager check --json"])]
    [RootCommand]
    internal static async Task<CommandOutcome<string>> Run(
        [Argument(Description = "Scope, modifier and action words, in any order",
            ValueName = "WORD", AllowMultipleValues = true, Choices = ["home", "system", "all", "update", "cleanup", "gc", "build", "switch", "boot", "rollback", "check",
                "t3code-update", "opencode-update", "pince-update", "ui", "tray", "status", "history"])]
        string[]? words,
        [TrailingArguments(Description = "Arguments forwarded verbatim to native tools", ValueName = "ARG")] string[]? native,
        [FromServices] CliSession session,
        CommandExecutionContext context,
        [Option("--flake", Description = "Use a specific flake checkout", ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = "Select a registered host configuration")] string? host = null,
        [Option("--dry-run", Description = "Preview without making changes")] bool dryRun = false,
        [Option("--force", Description = "Recalculate package hashes even when the pin is current")] bool force = false,
        [Option("--plain", Description = "Disable dynamic terminal presentation")] bool plain = false,
        [Option("--verbose", Description = "Retain detailed command output on success")] bool verbose = false,
        [Option("--browser", Description = "Open the dashboard in the default browser")] bool browser = false,
        [Option("--no-open", Description = "Serve the dashboard without opening a window")] bool noOpen = false,
        [Option("--interval", Description = "Tray update-check interval in minutes (5–10080)", ValueName = "MINUTES")] int interval = 360,
        CancellationToken cancellationToken = default)
    {
        var json = context.OutputMode == CommandOutputMode.Json;
        try
        {
            var cli = CliArguments.Resolve(words ?? [], new CliOptions(flake, host, dryRun, force, json, plain, verbose, browser, noOpen, interval), native ?? []);
            return await CliHandler.ExecuteAsync(cli, session, context, cancellationToken).ConfigureAwait(false);
        }
        catch (ManagerException error)
        {
            session.Output.Explanation = error.Explanation;
            return CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, new CommandFault("NM0001", error.Message));
        }
    }
}

/// <summary>Exit codes as the Bun version sets them: 0, 130 after cancellation, and 1 for every failure.</summary>
public sealed class CliExitCodes : IExitCodePolicy
{
    public int GetExitCode(CommandExitCategory category) => category switch
    {
        CommandExitCategory.Success => 0,
        CommandExitCategory.Cancelled => 130,
        _ => 1,
    };
}
