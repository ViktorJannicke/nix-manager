using NixManager.Domain;
using NixManager.Operations;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace NixManager.Daemon;

/// <summary>
/// The command line's operation client (D025.4): an operation goes to the running instance, the daemon or a
/// dashboard, when one runs for the same configuration, and runs in this process otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The running instance streams the operation's <see cref="ManagerEvent"/> sequence and final record back over the
/// instance socket, and this client hands them to the same presentation as a run in this process, so the command line
/// prints the same output and exits with the same status either way. Ctrl+C asks the running instance to cancel; its
/// last events and the cancelled record still arrive. A second operation while one runs is refused with the message of
/// the operation lock, as a second process would be.
/// </para>
/// <para>
/// Plans, the history and the update report are read here: they read the stored records, which the running instance
/// writes. Without a running instance, with <c>NIX_MANAGER_DAEMON=0</c>, or for another checkout, host or state
/// directory, the operation runs here, and the shared operation lock keeps it from overlapping one in the daemon.
/// Over SSH, an operation with a privileged step runs here too, so its Polkit prompt reaches the terminal instead of
/// the desktop session the daemon runs in.
/// </para>
/// </remarks>
public sealed class RoutingOperationClient(IOperationClient local, Func<string, string?>? environment = null) : IOperationClient
{
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    public Config Config => local.Config;
    public Plan Plan(Request request) => local.Plan(request);
    public UpdateReport? Updates() => local.Updates();
    public IReadOnlyList<Operation> History() => local.History();

    public async Task<Operation> RunAsync(Request request, Func<ManagerEvent, CancellationToken, ValueTask> onEvent, CancellationToken cancellationToken)
    {
        if (!Routes(request)) return await local.RunAsync(request, onEvent, cancellationToken).ConfigureAwait(false);
        // Nothing runs (NotRunning), or the instance cannot be used: run here, without having held the instance.
        var claim = await DaemonProtocol.ForwardAsync(new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config), request), cancellationToken)
            .ConfigureAwait(false);
        if (claim is ApplicationInstanceClaim.Forwarded { Reply: { } reply })
        {
            await using (reply.ConfigureAwait(false))
            {
                if (await FollowAsync(reply, onEvent, cancellationToken).ConfigureAwait(false) is { } result) return result;
            }
        }
        return await local.RunAsync(request, onEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="request"/> goes to a running instance, if one runs.</summary>
    internal bool Routes(Request request)
    {
        if (DaemonProtocol.Disabled(_environment)) return false;
        if (_environment("SSH_CONNECTION") is not { Length: > 0 } && _environment("SSH_TTY") is not { Length: > 0 }) return true;
        try
        {
            var privileged = Config.Privileged;
            return !local.Plan(request).Steps.Any(step => step.Argv.Count >= privileged.Count && step.Argv.Take(privileged.Count).SequenceEqual(privileged));
        }
        // The running instance reports an invalid request as this process would.
        catch (ManagerException) { return true; }
    }

    /// <summary>
    /// Feeds the reply to the presentation; the final record, or null when the instance asks this process to run the
    /// request itself.
    /// </summary>
    internal static async Task<Operation?> FollowAsync(IForwardedLaunchReply reply, Func<ManagerEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        Operation? result = null;
        DaemonMessage? error = null;
        // Cancelling asks the instance to stop; its last events and the final record still arrive.
        using (cancellationToken.Register(() => _ = reply.CancelAsync().AsTask()))
        {
            await foreach (var text in reply.ReadMessagesAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var message = DaemonProtocol.ParseMessage(text);
                switch (message?.Kind)
                {
                    case "event" when message.Event is { } e:
                        await onEvent(e, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case "result":
                        result = message.Operation;
                        break;
                    case "error":
                        error = message;
                        break;
                    case "note" when message.Message is { } note:
                        await Console.Error.WriteLineAsync(note).ConfigureAwait(false);
                        break;
                    case "elsewhere":
                        return null;
                }
            }
        }
        await reply.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (error is not null)
            throw new ManagerException(error.Text ?? (error.Message is { } message ? DisplayText.Plain(message) : DisplayText.Of("stored_operation_could_not_start")),
                error.Explanation);
        return result ?? throw new ManagerException(DisplayText.Of("stored_daemon_ended_before_operation_finished"));
    }
}
