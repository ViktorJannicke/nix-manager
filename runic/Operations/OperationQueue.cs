using NixManager.Domain;
using NixManager.Observation;

namespace NixManager.Operations;

// The operation queue (Submit): requests that wait for the running operation, and the ones before them, to end, so
// several pin updates, preparing changes and applying them can be asked for at once. One operation still runs at a
// time; the next one starts when the running one ends.
//
// - A request runs at once when nothing runs and nothing waits its turn before it, and is queued otherwise.
// - When a queued operation fails, is stopped, or prepared changes that should have been applied wait for a review,
//   the entries still waiting are held: they wait for ContinueQueue or to be removed, since they were asked for
//   after it. Requests submitted later are not held, and start in their turn.
// - The daemon and the dashboard keep the queue of their checkout and host in the state directory (RunQueue), so a
//   restarted tray continues it; one process at a time keeps it. An entry whose operation did not finish when the
//   process ended holds the entries after it.
public sealed partial class OperationService
{
    /// <summary>How long a queued request waits before trying again while another process's operation runs.</summary>
    public static readonly TimeSpan QueueRetry = TimeSpan.FromSeconds(3);

    private readonly Lock _queueGate = new();
    private readonly List<QueuedOperation> _queue = [];
    // Held while this process keeps the queue in the state directory (RunQueue); a command line's service does not.
    private FileStream? _queueKept;
    // The entry whose operation, or its follow-up, runs.
    private string? _queueRunning;
    // Operations that are ending and start their follow-up: the queue waits for that to start.
    private int _handingOver;
    // The process ends: nothing more starts from the queue (StopAsync).
    private bool _stopping;
    private ITimer? _queueRetry;

    private string QueuePath(string extension) => QueueFile(config, extension);

    /// <summary>
    /// The file of the queue of <paramref name="config"/>'s checkout and host: the state directory is the user's, shared
    /// by every checkout.
    /// </summary>
    internal static string QueueFile(Config config, string extension = "json") =>
        Path.Combine(config.StateDir, "queues", $"{Repository.HashText($"{config.Flake}\n{config.HostKey}")[..16]}.{extension}");

    public IReadOnlyList<QueuedOperation> Queue
    {
        get { lock (_queueGate) return [.. _queue]; }
    }

    public bool Pending
    {
        get
        {
            // A follow-up about to start counts, so nothing else starts before it and the process does not end.
            lock (_queueGate) return !_stopping && (_handingOver > 0 || _queue.Any(entry => entry.Held is null));
        }
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
            PublishQueue();
        }
        var started = Advance();
        return started?.Request.Queued == entry.Id ? new Submission(started, null) : new Submission(null, entry);
    }

    public bool Unqueue(string id)
    {
        lock (_queueGate)
        {
            if (_queue.RemoveAll(entry => entry.Id == id) == 0) return false;
            SaveQueue();
            PublishQueue();
            return true;
        }
    }

    public void ContinueQueue()
    {
        lock (_queueGate)
        {
            for (var index = 0; index < _queue.Count; index++) _queue[index] = _queue[index] with { Held = null };
            SaveQueue();
            PublishQueue();
        }
        Advance();
    }

    public void ClearQueue()
    {
        lock (_queueGate)
        {
            _queue.Clear();
            SaveQueue();
            PublishQueue();
        }
    }

    public void RunQueue()
    {
        lock (_queueGate)
        {
            if (_queueKept is not null || _stopping) return;
            // One process keeps a checkout's queue, such as the tray while a dashboard of its own runs beside it.
            if (TakeQueueLock() is not { } kept)
            {
                Console.Error.WriteLine("Another Nix Manager keeps the queue of this configuration; this one runs only what it is asked for.");
                return;
            }
            _queueKept = kept;
            var saved = File.Exists(QueuePath("json")) ? Read() : null;
            var history = History();
            // An entry that started before the process ended has its operation in the history, and is not run again.
            _queue.AddRange(saved?.Entries.Where(entry => !_queue.Any(queued => queued.Id == entry.Id)
                && !history.Any(operation => operation.Request.Queued == entry.Id)) ?? []);
            if (saved?.Running is { } running && running != _queueRunning)
            {
                // History lists the newest first: a follow-up after the operation that prepared it.
                var last = history.FirstOrDefault(operation => operation.Request.Queued == running);
                // Prepared changes whose application had not started yet did not finish either.
                var unfinished = last is not { Status: OperationStatuses.Succeeded, Held: null }
                    || last.Request is { Action: Actions.ReviewBuild, Then: not null, DryRun: false };
                if (unfinished) Hold(DisplayText.Of("stored_queue_held_ended"));
            }
            SaveQueue();
            PublishQueue();
        }
        Advance();

        SavedQueue? Read()
        {
            try
            {
                return ManagerJson.Deserialize<SavedQueue>(File.ReadAllText(QueuePath("json")));
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

    public async ValueTask StopAsync()
    {
        lock (_queueGate)
        {
            // What waits stays in the queue for the next process.
            _stopping = true;
            _queueRetry?.Dispose();
            _queueRetry = null;
        }
        Cancel();
        await WaitAsync().ConfigureAwait(false);
        lock (_queueGate)
        {
            _queueKept?.Dispose();
            _queueKept = null;
        }
    }

    /// <summary>Takes the lock on this checkout's queue, which the system releases when the process ends; null when another process holds it.</summary>
    private FileStream? TakeQueueLock()
    {
        try
        {
            OperationStore.CreatePrivateDirectory(Path.GetDirectoryName(QueuePath("lock"))!);
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var stream = new FileStream(QueuePath("lock"), options);
            if (Native.TryLockExclusive(stream.SafeFileHandle)) return stream;
            stream.Dispose();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ManagerException)
        {
            Console.Error.WriteLine($"The queue could not be locked: {error.Message}");
            return null;
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
        lock (_queueGate)
        {
            if (_stopping || Busy || _handingOver > 0) return null;
            if (_queue.FirstOrDefault(entry => entry.Held is null) is not { } next) return null;
            Operation? started = null;
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
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // It cannot start any more, such as prepared changes that were discarded, or the disk is full; what
                // follows was asked for after it.
                var index = _queue.FindIndex(entry => entry.Id == next.Id);
                _queue[index] = next with { Held = DisplayText.From(error) };
                Hold(DisplayText.Of("stored_queue_held_could_not_start", ("label", Actions.Label(next.Request.Action))));
            }
            SaveQueue();
            PublishQueue();
            return started;
        }
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
                PublishQueue();
            }
        }
        Advance();
    }

    /// <summary>Holds every entry that waits its turn. The caller holds the queue's lock.</summary>
    private void Hold(DisplayText reason)
    {
        for (var index = 0; index < _queue.Count; index++)
            if (_queue[index].Held is null) _queue[index] = _queue[index] with { Held = reason };
    }

    /// <summary>Writes the queue when this process keeps it. The caller holds the queue's lock.</summary>
    private void SaveQueue()
    {
        if (_queueKept is null) return;
        try
        {
            OperationStore.AtomicJson(QueuePath("json"), ManagerJson.Serialize(new SavedQueue { Entries = [.. _queue], Running = _queueRunning }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ManagerException)
        {
            Console.Error.WriteLine($"The queue could not be saved: {error.Message}");
        }
    }

    /// <summary>
    /// Publishes the queue as it is now. The caller holds the queue's lock, so snapshots reach clients in the order the
    /// queue changed; listeners never take that lock.
    /// </summary>
    private void PublishQueue() => Publish(new QueueChanged([.. _queue]));
}
