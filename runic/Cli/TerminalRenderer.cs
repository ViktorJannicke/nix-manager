using System.Globalization;
using System.Text;
using NixManager.Domain;
using NixManager.Operations;
using Runic.CommandLine;

namespace NixManager.Cli;

/// <summary>
/// Presents an operation's events on the terminal (<c>TerminalRenderer</c> in <c>src/terminal.ts</c>). On an
/// interactive terminal a Runic task log keeps the last lines under the action's title; with <c>--plain</c>,
/// without a terminal or with <c>--json</c> every line is written as it arrives, and with <c>--json</c> the
/// handler console sends it to standard error so standard output stays machine-readable.
/// </summary>
public sealed class TerminalRenderer(CommandExecutionContext context, CliInvocation cli, Func<string, string?> environment)
{
    private readonly ICommandConsole _console = context.Console;
    private CommandTaskLog? _task;
    private BuildProgress? _progress;

    public bool Dynamic { get; } =
        !cli.Json && !cli.Plain && environment("NO_COLOR") is null && !context.Console.IsErrorRedirected && environment("TERM") != "dumb";

    public static string DescribeProgress(BuildProgress progress)
    {
        if (progress.Evaluating) return "evaluating";
        var parts = new List<string>();
        if (progress.Builds.Expected > 0) parts.Add($"{progress.Builds.Done}/{progress.Builds.Expected} built");
        if (progress.Downloads.Expected > 0) parts.Add($"{progress.Downloads.Done}/{progress.Downloads.Expected} downloaded");
        if (progress.Bytes.Expected > 0)
            parts.Add($"{(progress.Bytes.Done / 1048576.0).ToString("F0", CultureInfo.InvariantCulture)}/{(progress.Bytes.Expected / 1048576.0).ToString("F0", CultureInfo.InvariantCulture)} MiB");
        return parts.Count > 0 ? string.Join(", ", parts) : "preparing";
    }

    public async ValueTask BeginAsync(Request request, Config config, CancellationToken cancellationToken)
    {
        if (!Dynamic) return;
        var host = request.Host is { Length: > 0 } ? request.Host : config.HostKey;
        await _console.WriteErrorAsync($"┌  Nix Manager · {host} · {(request.Scope == Scopes.All ? "Home + system" : request.Scope)}\n│\n".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        _task = await new CommandPrompts(context).StartTaskLogAsync(Actions.Label(request.Action), visibleLines: 6, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OnEventAsync(ManagerEvent e, CancellationToken cancellationToken)
    {
        switch (e)
        {
            case OperationProgress progress:
                _progress = progress.Progress;
                break;
            case OperationOutput output when _task is not null:
                foreach (var line in output.Text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
                    if (line.Length > 0) await _task.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
                break;
            case OperationOutput output when output.Stream == "stderr":
                await _console.WriteErrorAsync(output.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
                break;
            case OperationOutput output:
                await _console.WriteOutAsync(output.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public async ValueTask HeartbeatAsync(Operation operation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var started = Timestamps.Parse(operation.StartedAt) ?? now;
        var elapsed = (long)Math.Floor((now - started).TotalSeconds);
        var running = operation.Steps?.FirstOrDefault(step => step.Status == StepStatuses.Running);
        var detail = running?.Progress is not null && _progress is not null ? $" · {DescribeProgress(_progress)}" : "";
        var message = $"{elapsed}s elapsed · {operation.Step}{detail}";
        if (_task is not null) await _task.SetStatusAsync(message, cancellationToken).ConfigureAwait(false);
        else await _console.WriteErrorAsync($"→ {message}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FinishAsync(Operation operation, CancellationToken cancellationToken)
    {
        if (_task is not null)
        {
            if (operation.Status == OperationStatuses.Succeeded)
                await _task.SucceedAsync("Complete", showLog: cli.Verbose, cancellationToken).ConfigureAwait(false);
            else await _task.FailAsync(operation.Error ?? operation.Status, showLog: true, cancellationToken).ConfigureAwait(false);
            await _task.DisposeAsync().ConfigureAwait(false);
            _task = null;
        }
        else if (!cli.Json && operation.Status == OperationStatuses.Succeeded)
            await _console.WriteOutAsync("✓ Done.\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (!cli.Json && operation.Status != OperationStatuses.Succeeded && operation.Steps is { Count: > 0 } steps)
        {
            var text = new StringBuilder();
            foreach (var step in steps) text.Append($"{Symbol(step.Status)} {step.Label} ({step.Status})\n");
            await _console.WriteErrorAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        if (!cli.Json && operation.Status == OperationStatuses.Failed && operation.Explanation is { } explanation)
            await _console.WriteErrorAsync(ExplanationText(explanation).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FailAsync(Exception error, CancellationToken cancellationToken)
    {
        if (_task is null) return;
        await _task.FailAsync(error.Message, showLog: true, cancellationToken).ConfigureAwait(false);
        await _task.DisposeAsync().ConfigureAwait(false);
        _task = null;
    }

    public static string ExplanationText(Explanation explanation) =>
        $"\n{explanation.Title}.\n{explanation.Body}\n{(explanation.Fix is { } fix ? $"To allow it: {FixCommand(fix)}\n" : "")}";

    /// <summary>The command-line equivalent of a fix (<c>fixCommand</c>).</summary>
    public static string FixCommand(Fix fix) =>
        $"nix-manager software set nixpkgs.config.allowUnfree true{(fix.Scope == "system" ? " --system" : "")}";

    private static string Symbol(string status) => status switch
    {
        StepStatuses.Running => "◐",
        StepStatuses.Succeeded => "✓",
        StepStatuses.Failed or StepStatuses.Cancelled => "×",
        _ => "·",
    };
}

/// <summary>The plan, history and status texts of <c>src/terminal.ts</c>.</summary>
public static class CliText
{
    public static string Plan(Plan plan)
    {
        var text = new StringBuilder();
        foreach (var note in plan.Notes) text.Append($"! {note}\n");
        for (var index = 0; index < plan.Steps.Count; index++)
            text.Append($"{(index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2)}  {plan.Steps[index].Label}\n    {Planner.Format(plan.Steps[index])}\n");
        return text.ToString();
    }

    private static string When(string? timestamp) =>
        Timestamps.Parse(timestamp)?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? timestamp ?? "";

    public static string History(IReadOnlyList<Operation> history, string host, int columns)
    {
        if (history.Count == 0) return "No operations recorded yet.\n";
        var width = Math.Max(60, columns > 0 ? columns : 100);
        var text = new StringBuilder();
        foreach (var operation in history)
        {
            var status = operation.Status.PadRight(11);
            var name = Actions.Label(operation.Request.Action).PadRight(29);
            var context = $"{(operation.Request.Host is { Length: > 0 } h ? h : host)}{(operation.Request.DryRun ? " · preview" : "")}";
            var line = $"{When(operation.StartedAt)}  {status} {name} {context}";
            text.Append(line.Length > width ? line[..width] : line).Append('\n');
        }
        return text.ToString();
    }

    public static string Status(Config config, UpdateReport? updates, Operation? latest)
    {
        var host = config.Host;
        var text = new StringBuilder();
        text.Append($"Nix Manager · {config.HostKey}\n");
        text.Append($"Checkout  {config.Flake}\n");
        text.Append($"Home      {host.HomeConfiguration}\n");
        text.Append($"System    {host.NixosConfiguration}\n");
        text.Append(updates is not null
            ? $"Updates   {updates.Items.Count} available · {updates.Errors.Count} errors · {When(updates.CheckedAt)}\n"
            : "Updates   Not checked · run nix-manager check\n");
        if (latest is not null)
            text.Append($"Latest    {latest.Status} · {Actions.Label(latest.Request.Action)} · {When(latest.StartedAt)}\n");
        return text.ToString();
    }
}
