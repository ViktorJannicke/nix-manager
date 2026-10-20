using NixManager.Configuration;
using NixManager.Domain;
using NixManager.Operations;
using Runic.CommandLine;

namespace NixManager.Cli;

/// <summary>Runs a resolved command line: the dashboard, the tray, the history, the status or an operation.</summary>
internal static class CliHandler
{
    public static async Task<CommandOutcome<RunResult>> ExecuteAsync(CliInvocation cli, CliSession session, CommandExecutionContext context, CancellationToken cancellationToken)
    {
        // The dashboard and the daemon start the Desktop host, which must own the main thread before any await: the
        // command hands them back to Main (NixManagerCli.Run), which runs them once the command line has returned.
        if (cli.Mode is CliMode.Ui)
        {
            if (cli.Json || cli.Interval != 360)
                return CliFaults.Failure<RunResult>(CommandExitCategory.Usage, CliFaults.InvalidCommandLine, new ManagerException("The dashboard takes only --flake, --host, --no-open and --browser."));
            var dashboard = new Dashboard.DashboardCommand.Invocation(cli.Flake, cli.Host, cli.NoOpen, cli.Browser);
            context.ContinueOnCallingThread(() => session.Dashboard(dashboard));
            return CommandOutcome.Success(new RunResult { Kind = RunResult.DashboardKind });
        }
        if (cli.Mode is CliMode.Tray)
        {
            if (cli.Json || cli.Browser || cli.NoOpen)
                return CliFaults.Failure<RunResult>(CommandExitCategory.Usage, CliFaults.InvalidCommandLine, new ManagerException("The tray takes only --flake, --host and --interval."));
            var daemon = new Daemon.DaemonCommand.Invocation(cli.Flake, cli.Host, cli.Interval);
            context.ContinueOnCallingThread(() => session.Daemon(daemon));
            return CommandOutcome.Success(new RunResult { Kind = RunResult.TrayKind });
        }
        Config config;
        try
        {
            config = await LoadAsync(cli.Flake, cli.Host, session).ConfigureAwait(false);
        }
        catch (ManagerException error)
        {
            return CliFaults.Failure<RunResult>(CommandExitCategory.Unavailable, CliFaults.ConfigurationUnavailable, error);
        }
        var client = session.CreateClient(config, session.Runner);
        if (cli.Mode is CliMode.History or CliMode.Status)
        {
            try
            {
                var history = client.History();
                return CommandOutcome.Success(cli.Mode == CliMode.History
                    ? new RunResult { Kind = RunResult.HistoryKind, Host = config.HostKey, History = history.ToList() }
                    : new RunResult
                    {
                        Kind = RunResult.StatusKind,
                        Host = config.HostKey,
                        Status = new StatusDocument(config.HostKey, config.Flake, config.Host, client.Updates(), history.FirstOrDefault()),
                    });
            }
            catch (ManagerException error)
            {
                return CliFaults.Failure<RunResult>(CommandExitCategory.CommandFailure, CliFaults.HistoryUnavailable, error);
            }
        }
        return await RunAsync(cli, config, client, session, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the configuration a command uses. A configuration that does not load makes the command unavailable.</summary>
    public static async Task<Config> LoadAsync(string? flake, string? host, CliSession session)
    {
        Setup.SetupAssistant.AllowFlakes(session.Environment);
        return await new ConfigLoader(session.Runner, session.Environment)
            .LoadAsync(flake, host, CliSession.PrivilegedPrefix).ConfigureAwait(false);
    }

    /// <summary>
    /// Previews a request, or runs it as an operation presented in the terminal while it runs; the operation's status
    /// selects the outcome.
    /// </summary>
    internal static async Task<CommandOutcome<RunResult>> RunAsync(CliInvocation cli, Config config, IOperationClient client, CliSession session,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var r = cli.Request with { Host = cli.Request.Host is { Length: > 0 } ? cli.Request.Host : config.HostKey };
        // A normal command preview is entirely read-only, including operation state.
        if (r.DryRun && !Actions.IsPackage(r.Action))
        {
            try
            {
                return CommandOutcome.Success(new RunResult { Kind = RunResult.PlanKind, Host = r.Host, Plan = client.Plan(r) });
            }
            catch (ManagerException error)
            {
                return CliFaults.Failure<RunResult>(CommandExitCategory.CommandFailure, CliFaults.OperationNotStarted, error);
            }
        }
        var terminal = new TerminalRenderer(context, cli, session.Environment.Variable);
        await terminal.BeginAsync(r, config, cancellationToken).ConfigureAwait(false);
        Operation? latest = null;
        var gate = new SemaphoreSlim(1, 1);
        async ValueTask OnEvent(ManagerEvent e, CancellationToken token)
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (e is OperationChanged changed) latest = changed.Operation;
                await terminal.OnEventAsync(e, token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(session.HeartbeatInterval, session.Clock);
            try
            {
                while (await timer.WaitForNextTickAsync(heartbeatStop.Token).ConfigureAwait(false))
                {
                    await gate.WaitAsync(heartbeatStop.Token).ConfigureAwait(false);
                    try
                    {
                        if (latest is { Status: OperationStatuses.Running } active)
                            await terminal.HeartbeatAsync(active, session.Clock.GetUtcNow(), heartbeatStop.Token).ConfigureAwait(false);
                    }
                    finally { gate.Release(); }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        Operation result;
        try
        {
            result = await client.RunAsync(r, OnEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (ManagerException error)
        {
            await terminal.FailAsync(CancellationToken.None).ConfigureAwait(false);
            return CliFaults.Failure<RunResult>(CommandExitCategory.CommandFailure, CliFaults.OperationFailed, error);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
        await terminal.FinishAsync(result, CancellationToken.None).ConfigureAwait(false);
        return CliFaults.Operation(new RunResult
        {
            Kind = RunResult.OperationKind,
            Host = r.Host,
            Operation = result,
            Updates = r.Action == Actions.Check ? client.Updates() : null,
        });
    }
}
