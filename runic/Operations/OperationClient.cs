using System.Threading.Channels;
using NixManager.Domain;

namespace NixManager.Operations;

/// <summary>
/// What a command line needs from the operation service: run a request and follow its events to the end.
/// </summary>
/// <remarks>
/// This is the seam for D025.4. Today <see cref="LocalOperationClient"/> runs the service in the command line's
/// own process. W300-031 adds a client that forwards the request to the per-user daemon over the
/// <c>ApplicationInstance</c> socket (with the replies of W300-028) and feeds the same <see cref="ManagerEvent"/>
/// sequence, serialized with <see cref="ManagerJson.Compact"/>, to the same callback; cancelling the token there
/// asks the daemon to cancel. The command line's presentation depends only on this interface.
/// </remarks>
public interface IOperationClient
{
    Config Config { get; }
    Plan Plan(Request request);
    UpdateReport? Updates();
    IReadOnlyList<Operation> History();

    /// <summary>
    /// Runs <paramref name="request"/> and passes every event of that operation to <paramref name="onEvent"/>, in
    /// order and one at a time, until it ends. Cancelling <paramref name="cancellationToken"/> asks the operation
    /// to stop and still returns its final record.
    /// </summary>
    /// <exception cref="ManagerException">The operation could not start.</exception>
    Task<Operation> RunAsync(Request request, Func<ManagerEvent, CancellationToken, ValueTask> onEvent, CancellationToken cancellationToken);
}

public sealed class LocalOperationClient(IOperationService service) : IOperationClient
{
    public Config Config => service.Config;
    public Plan Plan(Request request) => service.Plan(request);
    public UpdateReport? Updates() => service.Updates();
    public IReadOnlyList<Operation> History() => service.History();

    public async Task<Operation> RunAsync(Request request, Func<ManagerEvent, CancellationToken, ValueTask> onEvent, CancellationToken cancellationToken)
    {
        // The service publishes synchronously under its lock; the channel moves presentation off that thread.
        var events = Channel.CreateUnbounded<ManagerEvent>(new UnboundedChannelOptions { SingleReader = true });
        string? id = null;
        var early = new List<ManagerEvent>();
        using var subscription = service.Subscribe(e =>
        {
            lock (early)
            {
                if (id is null) early.Add(e);
                else if (Belongs(e, id)) events.Writer.TryWrite(e);
            }
        });
        var run = service.Start(request);
        lock (early)
        {
            id = run.Operation.Id;
            foreach (var e in early.Where(e => Belongs(e, id))) events.Writer.TryWrite(e);
        }
        using (cancellationToken.Register(service.Cancel))
        {
            // The final event is published before the completion, so nothing is left behind.
            _ = run.Completion.ContinueWith(_ => events.Writer.TryComplete(), TaskScheduler.Default);
            // Presentation is not cancelled with the operation: the final events still arrive.
            await foreach (var e in events.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                await onEvent(e, CancellationToken.None).ConfigureAwait(false);
        }
        return await run.Completion.ConfigureAwait(false);
    }

    private static bool Belongs(ManagerEvent e, string id) => e switch
    {
        OperationChanged changed => changed.Operation.Id == id,
        OperationOutput output => output.OperationId == id,
        OperationProgress progress => progress.OperationId == id,
        _ => false,
    };
}
