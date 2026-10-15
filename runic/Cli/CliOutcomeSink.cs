using NixManager.Operations;
using NixManager.Domain;
using Runic.CommandLine;

namespace NixManager.Cli;

/// <summary>
/// Writes an invocation's outcome as the Bun version did, so scripts keep working: with <c>--json</c> the bare
/// document (a plan, an operation, <c>{operation, updates}</c>, the history or the status) on standard output
/// instead of a <c>runic.commandline/1</c> envelope, and errors as <c>Error: MESSAGE</c> on standard error.
/// </summary>
public sealed class CliOutcomeSink(CliOutput output) : ICommandOutcomeSink
{
    public async ValueTask WriteAsync<TResult>(CommandDescriptor command, CommandExecutionContext context, CommandOutcome<TResult> outcome,
        ICommandResultCodec<TResult> codec, int exitCode, IReadOnlyList<CommandDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var console = context.Console;
        if (context.OutputMode == CommandOutputMode.Json)
        {
            if (output.Json is { } json) await console.WriteOutAsync((json + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        else if (output.Human is { } human) await console.WriteOutAsync(human.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (outcome.IsSuccess || output.Presented) return;
        var message = outcome.Fault?.Message ?? "The command failed.";
        await console.WriteErrorAsync($"Error: {message}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (output.Explanation is { } explanation)
            await console.WriteErrorAsync($"\n{explanation.Title}.\n{explanation.Body}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
