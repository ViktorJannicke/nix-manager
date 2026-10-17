using NixManager.Configuration;
using NixManager.Domain;
using NixManager.Operations;
using Runic.CommandLine;

namespace NixManager.Cli;

/// <summary>The command line's modes (<c>main</c> and <c>execute</c> in <c>src/main.ts</c>).</summary>
internal static class CliHandler
{
    private static CommandOutcome<string> Ok() => CommandOutcome.Success("");

    public static async Task<CommandOutcome<string>> ExecuteAsync(CliInvocation cli, CliSession session, CommandExecutionContext context, CancellationToken cancellationToken)
    {
        // The dashboard and the daemon start the Desktop host, which must own the main thread before any await: the
        // command hands them back to Main (NixManagerCli.Run), which runs them once the command line has returned.
        if (cli.Mode is CliMode.Ui)
        {
            if (cli.Json || cli.Interval != 360) throw new ManagerException("The dashboard takes only --flake, --host, --no-open and --browser.");
            var dashboard = new Dashboard.DashboardCommand.Invocation(cli.Flake, cli.Host, cli.NoOpen, cli.Browser);
            context.ContinueOnCallingThread(() => session.Dashboard(dashboard));
            return Ok();
        }
        if (cli.Mode is CliMode.Tray)
        {
            if (cli.Json || cli.Browser || cli.NoOpen) throw new ManagerException("The tray takes only --flake, --host and --interval.");
            var daemon = new Daemon.DaemonCommand.Invocation(cli.Flake, cli.Host, cli.Interval);
            context.ContinueOnCallingThread(() => session.Daemon(daemon));
            return Ok();
        }
        Setup.SetupAssistant.AllowFlakes(session.Environment);
        var config = await new ConfigLoader(session.Runner, session.Environment)
            .LoadAsync(cli.Flake, cli.Host, CliSession.PrivilegedPrefix).ConfigureAwait(false);
        var client = session.CreateClient(config, session.Runner);
        if (cli.Mode is CliMode.History or CliMode.Status)
        {
            var history = client.History();
            if (cli.Json)
                session.Output.Json = cli.Mode == CliMode.History
                    ? ManagerJson.Serialize(history.ToList())
                    : ManagerJson.Serialize(new StatusDocument(config.HostKey, config.Flake, config.Host, client.Updates(), history.FirstOrDefault()));
            else
                session.Output.Human = cli.Mode == CliMode.History
                    ? CliText.History(history, config.HostKey, Console.IsOutputRedirected ? 0 : SafeWidth())
                    : CliText.Status(config, client.Updates(), history.FirstOrDefault());
            return Ok();
        }
        return await RunAsync(cli, config, client, session, context, cancellationToken).ConfigureAwait(false);
    }

    private static int SafeWidth()
    {
        try { return Console.WindowWidth; }
        catch (IOException) { return 0; }
    }

    /// <summary>Runs one operation in the terminal and sets the exit code from its result (<c>execute</c>).</summary>
    internal static async Task<CommandOutcome<string>> RunAsync(CliInvocation cli, Config config, IOperationClient client, CliSession session,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var r = cli.Request with { Host = cli.Request.Host is { Length: > 0 } ? cli.Request.Host : config.HostKey };
        // A normal command preview is entirely read-only, including operation state.
        if (r.DryRun && !Actions.IsPackage(r.Action))
        {
            var plan = client.Plan(r);
            if (cli.Json) session.Output.Json = ManagerJson.Serialize(plan);
            else session.Output.Human = CliText.Plan(plan);
            return Ok();
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
            await terminal.FailAsync(error, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
        if (cli.Json)
            session.Output.Json = r.Action == Actions.Check
                ? ManagerJson.Serialize(new CheckDocument(result, client.Updates()))
                : ManagerJson.Serialize(result);
        else await terminal.FinishAsync(result, CancellationToken.None).ConfigureAwait(false);
        session.Output.Presented = true;
        return result.Status switch
        {
            OperationStatuses.Succeeded => Ok(),
            OperationStatuses.Cancelled => CommandOutcome.Failure<string>(CommandExitCategory.Cancelled, new CommandFault("NM0130", result.Error ?? "Cancelled.")),
            _ => CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, new CommandFault("NM0001", result.Error ?? result.Status)),
        };
    }
}
