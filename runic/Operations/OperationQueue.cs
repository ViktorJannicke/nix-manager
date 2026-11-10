using NixManager.Domain;

namespace NixManager.Operations;

// The operation queue (Submit): requests that wait for the running operation, and the ones before them, to end, so
// several pin updates, preparing changes and applying them can be asked for at once. One operation still runs at a
// time; the next one starts when the running one ends.
//
// - A request runs at once when nothing runs and nothing waits its turn before it, and is queued otherwise.
// - When a queued operation fails, is stopped, or prepared changes that should have been applied wait for a review,
//   the entries still waiting are held: they wait for ContinueQueue or to be removed, since they were asked for
//   after it. Requests submitted later are not held, and start in their turn.
// - The daemon and the dashboard keep the queue in queue.json (RunQueue), so a restarted tray continues it. An entry
//   whose operation did not succeed when the process ended holds the entries after it.
public sealed partial class OperationService
{
    /// <summary>How long a queued request waits before trying again while another process's operation runs.</summary>
    public static readonly TimeSpan QueueRetry = TimeSpan.FromSeconds(3);

    private readonly Lock _queueGate = new();
    private readonly List<QueuedOperation> _queue = [];
    // This process keeps the queue in the state directory (RunQueue); a command line's service does not.
    private bool _queueKept;
    // The entry whose operation, or its follow-up, runs.
    private string? _queueRunning;
    // Operations that are ending and start their follow-up: the queue waits for that to start.
    private int _handingOver;
    private ITimer? _queueRetry;

    private string QueuePath => Path.Combine(config.StateDir, "queue.json");

    public IReadOnlyList<QueuedOperation> Queue
    {
        get { lock (_queueGate) return [.. _queue]; }
    }

    public bool Pending
    {
        get { lock (_queueGate) return _queue.Any(entry => entry.Held is null); }
    }

    public Submission Submit(Request request)
    {
        // Refused now rather than when its turn comes; the request is resolved and planned again when it starts.
        var resolved = Resolve(request).Request;
        Plan(resolved);
        QueuedOperation entry;
        lock (_queueGate)
        {
            // Asking twice for the same thing, such as updating a pin, queues it once.
            if (_queue.FirstOrDefault(queued => queued.Held is null && Same(queued.Request, resolved)) is { } existing)
                return new Submission(null, existing);
            entry = new QueuedOperation { Id = Guid.NewGuid().ToString("D"), Request = resolved, QueuedAt = Timestamps.Now(_clock) };
            _queue.Add(entry);
            SaveQueue();
        }
        PublishQueue();
        var started = Advance();
        return started?.Request.Queued == entry.Id ? new Submission(started, null) : new Submission(null, entry);
    }

    public bool Unqueue(string id)
    {
        bool removed;
        lock (_queueGate)
        {
            removed = _queue.RemoveAll(entry => entry.Id == id) > 0;
            if (removed) SaveQueue();
        }
        if (removed) PublishQueue();
        return removed;
    }

    public void ContinueQueue()
    {
        lock (_queueGate)
        {
            for (var index = 0; index < _queue.Count; index++) _queue[index] = _queue[index] with { Held = null };
            SaveQueue();
        }
        PublishQueue();
        Advance();
    }

    public void ClearQueue()
    {
        lock (_queueGate)
        {
            _queue.Clear();
            SaveQueue();
        }
        PublishQueue();
    }

    public void RunQueue()
    {
        lock (_queueGate)
        {
            if (_queueKept) return;
            _queueKept = true;
            var saved = File.Exists(QueuePath) ? Read() : null;
            _queue.AddRange(saved?.Entries.Where(entry => !_queue.Any(queued => queued.Id == entry.Id)) ?? []);
            if (saved?.Running is { } running && running != _queueRunning)
            {
                // History lists the newest first: a follow-up after the operation that prepared it.
                var last = History().FirstOrDefault(operation => operation.Request.Queued == running);
                if (last is not { Status: OperationStatuses.Succeeded, Held: null })
                    Hold(DisplayText.Of("stored_queue_held_ended"));
            }
            SaveQueue();
        }
        PublishQueue();
        Advance();

        SavedQueue? Read()
        {
            try
            {
                return ManagerJson.Deserialize<SavedQueue>(File.ReadAllText(QueuePath));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"The queue could not be read, so it starts empty: {error.Message}");
                return null;
            }
        }
    }

    public async Task IdleAsync()
    {
        while (Busy || Pending)
        {
            if (Busy) await WaitAsync().ConfigureAwait(false);
            // Waiting for another process's operation, which the queue tries again after.
            else await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }

    /// <summary>The same request, apart from the review a new preparation gets and the entry it runs for.</summary>
    private static bool Same(Request a, Request b)
    {
        static string Key(Request r) => ManagerJson.Serialize(r with
        {
            ReviewId = r.Action == Actions.ReviewBuild ? null : r.ReviewId,
            Queued = null,
        }, ManagerJson.Compact);
        return Key(a) == Key(b);
    }

    /// <summary>Starts the first entry that waits its turn, when nothing runs; returns the operation it started.</summary>
    private Operation? Advance()
    {
        Operation? started = null;
        lock (_queueGate)
        {
            if (Busy || _handingOver > 0) return null;
            if (_queue.FirstOrDefault(entry => entry.Held is null) is not { } next) return null;
            try
            {
                started = Start(next.Request with { Queued = next.Id }).Operation;
                _queue.Remove(next);
                _queueRunning = next.Id;
            }
            catch (ManagerException) when (Busy)
            {
                // Something else started meanwhile, such as a forwarded command line; its end starts the next.
                return null;
            }
            catch (ManagerException error) when (error.Text.Reference == OperationStore.Busy().Text.Reference)
            {
                // Another Nix Manager process runs an operation on this checkout.
                _queueRetry ??= _clock.CreateTimer(_ =>
                {
                    lock (_queueGate)
                    {
                        _queueRetry?.Dispose();
                        _queueRetry = null;
                    }
                    Advance();
                }, null, QueueRetry, Timeout.InfiniteTimeSpan);
                return null;
            }
            catch (ManagerException error)
            {
                // It cannot start any more, such as prepared changes that were discarded; what follows was asked for after it.
                Hold(DisplayText.Of("stored_queue_held_could_not_start", ("label", Actions.Label(next.Request.Action))));
                _queue[_queue.IndexOf(next)] = next with { Held = error.Text };
            }
            SaveQueue();
        }
        PublishQueue();
        return started;
    }

    /// <summary>
    /// After an operation ended and its follow-up, if any, started: when it was queued and did not succeed, or its
    /// prepared changes wait for a review, the entries still waiting are held; then the next one starts.
    /// </summary>
    private void QueueAfter(Operation operation, bool followed)
    {
        if (operation.Request.Queued is { } id && !followed)
        {
            lock (_queueGate)
            {
                if (_queueRunning == id) _queueRunning = null;
                var label = Actions.Label(operation.Request.Action);
                var reason = operation.Status switch
                {
                    OperationStatuses.Succeeded when operation.Held is not null => DisplayText.Of("stored_queue_held_review", ("label", label)),
                    OperationStatuses.Succeeded => null,
                    OperationStatuses.Cancelled => DisplayText.Of("stored_queue_held_stopped", ("label", label)),
                    _ => DisplayText.Of("stored_queue_held_failed", ("label", label)),
                };
                if (reason is not null) Hold(reason);
                SaveQueue();
            }
            PublishQueue();
        }
        Advance();
    }

    /// <summary>Holds every entry that waits its turn. The caller holds the queue's lock.</summary>
    private void Hold(DisplayText reason)
    {
        for (var index = 0; index < _queue.Count; index++)
            if (_queue[index].Held is null) _queue[index] = _queue[index] with { Held = reason };
    }

    /// <summary>Writes queue.json when this process keeps the queue. The caller holds the queue's lock.</summary>
    private void SaveQueue()
    {
        if (!_queueKept) return;
        try
        {
            _store.EnsureDirectory();
            OperationStore.AtomicJson(QueuePath, ManagerJson.Serialize(new SavedQueue { Entries = [.. _queue], Running = _queueRunning }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ManagerException)
        {
            Console.Error.WriteLine($"The queue could not be saved: {error.Message}");
        }
    }

    private void PublishQueue() => Publish(new QueueChanged(Queue));
}
