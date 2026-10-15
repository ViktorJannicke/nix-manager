using System.Text;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Updates;

namespace NixManager.Operations;

/// <summary>
/// Runs one operation at a time and publishes its progress (<c>Manager</c> in <c>src/manager.ts</c>).
/// </summary>
/// <remarks>
/// The service is the one place operations run, whoever asked for them: the command line in its own process
/// today, and the per-user daemon of D025 later, where the dashboard, the tray and forwarded command lines share
/// one instance. Everything a client needs is in the ordered <see cref="ManagerEvent"/> stream and the stored
/// records, so a client in another process can be fed the same events (W300-031).
/// </remarks>
public interface IOperationService
{
    Config Config { get; }
    /// <summary>Whether an operation runs in this process.</summary>
    bool Busy { get; }
    /// <summary>A snapshot of the operation this process runs.</summary>
    Operation? Active { get; }
    IReadOnlyList<Operation> History();
    LogResult Log(string id, int limit = OperationStore.LogTail);
    UpdateReport? Updates();
    /// <summary>The steps a request would run, without running anything (dry run).</summary>
    Plan Plan(Request request);
    /// <summary>Starts an operation: its first snapshot, and its final record once it ends.</summary>
    /// <exception cref="ManagerException">The request is invalid, or another operation runs.</exception>
    OperationRun Start(Request request);
    /// <summary>The final record of the running operation, or null when none runs.</summary>
    Task<Operation?> WaitAsync();
    /// <summary>Asks the running operation to stop. A privileged step that already runs finishes first.</summary>
    void Cancel();
    /// <summary>
    /// Receives every event in order, synchronously on the thread that produced it; keep the listener short and
    /// hand work to another context. Dispose the result to stop.
    /// </summary>
    IDisposable Subscribe(Action<ManagerEvent> listener);
}

/// <param name="Operation">The operation's first snapshot.</param>
/// <param name="Completion">Its final record; it never fails.</param>
public sealed record OperationRun(Operation Operation, Task<Operation> Completion);

public sealed partial class OperationService(Config config, ICommandRunner runner, IUpdateChecker updates, TimeProvider? clock = null)
    : IOperationService, IAsyncDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly OperationStore _store = new(config);
    private readonly Lock _gate = new();
    private readonly List<Action<ManagerEvent>> _listeners = [];
    private long _sequence;
    private Running? _current;

    private sealed record Running(Operation Operation, CancellationTokenSource Cancellation, Task<Operation> Completion);

    public Config Config => config;
    public OperationStore Store => _store;

    public bool Busy
    {
        get { lock (_gate) return _current is not null; }
    }

    public Operation? Active
    {
        get { lock (_gate) return _current?.Operation.Clone(); }
    }

    public IReadOnlyList<Operation> History()
    {
        lock (_gate) return _store.History(_current?.Operation.Clone());
    }

    public LogResult Log(string id, int limit = OperationStore.LogTail) => _store.Log(id, limit);

    public UpdateReport? Updates() => _store.Updates();

    public Plan Plan(Request request) => Planner.Plan(config, request);

    public IDisposable Subscribe(Action<ManagerEvent> listener)
    {
        lock (_gate) _listeners.Add(listener);
        return new Subscription(() => { lock (_gate) _listeners.Remove(listener); });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }

    public Task<Operation?> WaitAsync()
    {
        Running? current;
        lock (_gate) current = _current;
        return current is null ? Task.FromResult<Operation?>(null) : current.Completion.ContinueWith(task => (Operation?)task.Result, TaskScheduler.Default);
    }

    public void Cancel()
    {
        Running? current;
        lock (_gate)
        {
            current = _current;
            if (current is null) return;
            current.Operation.Step = current.Operation.FinishingPrivilegedStep == true ? "Stopping after privileged step" : "Cancelling";
            Changed(current.Operation);
        }
        // Outside the lock: cancellation callbacks run synchronously and may wait for output that needs it.
        try { current.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await WaitAsync().ConfigureAwait(false);
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("(^-|-$)")]
    private static partial Regex OuterDash();

    [GeneratedRegex("\r(?!\n)")]
    private static partial Regex LoneCarriageReturn();

    private static string StepId(int index, string label) =>
        $"{index + 1}-{OuterDash().Replace(NonAlphanumeric().Replace(label.ToLowerInvariant(), "-"), "")}";

    private static string PackageName(string action) => action.Replace("-update", "", StringComparison.Ordinal);

    public OperationRun Start(Request request)
    {
        var plan = Planner.Plan(config, request);
        if (Busy) throw new ManagerException("An operation is already running.");
        var release = _store.AcquireLock();
        Operation operation;
        try
        {
            var labels = plan.Steps.Count > 0
                ? plan.Steps.Select(step => step.Label).ToList()
                : [request.Action == Actions.Check ? "Check inputs and package releases" : $"Update the {PackageName(request.Action)} package pin"];
            operation = new Operation
            {
                Id = OperationStore.NewId(_clock),
                Pid = Environment.ProcessId,
                Flake = config.Flake,
                Request = request,
                Status = OperationStatuses.Running,
                StartedAt = Timestamps.Now(_clock),
                Step = "Preparing",
                Steps = labels.Select((label, index) => new StepOutcome { Id = StepId(index, label), Label = label, Status = StepStatuses.Pending }).ToList(),
            };
            // A new user's first preferences are decided before their first operation is recorded, or that
            // record would count as earlier use.
            Preferences.Read(config);
            _store.EnsureDirectory();
            if (request.Action is Actions.Switch or Actions.Boot)
            {
                var root = Paths.BuildRoot(config);
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
                OperationStore.CreatePrivateDirectory(root);
            }
            _store.Persist(operation);
        }
        catch
        {
            release.Dispose();
            throw;
        }
        var cancellation = new CancellationTokenSource();
        var run = new Run(this, operation, plan, cancellation.Token);
        lock (_gate)
        {
            // The task starts only after _current is set, so its first event already sees the operation.
            var completion = new Task<Task<Operation>>(() => run.ExecuteAsync(release, cancellation));
            _current = new Running(operation, cancellation, completion.Unwrap());
            Changed(operation);
            completion.Start(TaskScheduler.Default);
            return new OperationRun(operation.Clone(), _current.Completion);
        }
    }

    private void Changed(Operation operation) => Publish(new OperationChanged(operation.Clone()));

    private void Publish(ManagerEvent body)
    {
        lock (_gate)
        {
            var next = body with { Sequence = ++_sequence, Timestamp = Timestamps.Now(_clock) };
            foreach (var listener in _listeners.ToArray())
            {
                try { listener(next); }
                catch (Exception e) { Console.Error.WriteLine(e); }
            }
        }
    }

    /// <summary>One operation's execution: its steps, log, progress and final record.</summary>
    private sealed class Run(OperationService service, Operation operation, Plan plan, CancellationToken token)
    {
        private readonly OperationStore _store = service._store;
        private long _logBytes;
        private DateTimeOffset _lastPersist;
        private StepOutcome? _runningStep;

        private void Persist()
        {
            _lastPersist = service._clock.GetUtcNow();
            _store.Persist(operation);
        }

        private void Mutate(Action change)
        {
            lock (service._gate)
            {
                change();
                Persist();
                service.Changed(operation);
            }
        }

        private void Output(OutputEvent output)
        {
            lock (service._gate)
            {
                if (output is ProgressOutput progress)
                {
                    if (_runningStep is not { } step) return;
                    step.Progress = progress.Progress;
                    service.Publish(new OperationProgress(operation.Id, step.Id, progress.Progress));
                    if (service._clock.GetUtcNow() - _lastPersist > TimeSpan.FromSeconds(2)) Persist();
                    return;
                }
                var text = (TextOutput)output;
                // Keep bounded logs on disk. Byte offsets let clients splice the live stream onto a fetched log
                // tail without gaps or duplicates.
                if (_logBytes >= OperationStore.LogLimit) return;
                var line = LoneCarriageReturn().Replace(NixText.StripAnsi(text.Text), "\n");
                if (_logBytes + Encoding.UTF8.GetByteCount(line) >= OperationStore.LogLimit) line += "\n[Log limit reached: 16 MiB]\n";
                _store.AppendLog(operation.Id, line);
                var offset = _logBytes;
                _logBytes += Encoding.UTF8.GetByteCount(line);
                service.Publish(new OperationOutput(operation.Id, text.Stream, line, offset));
            }
        }

        private void Log(string text) => Output(new TextOutput("stdout", $"→ {text}\n"));

        private StepOutcome? StartStep(string label)
        {
            StepOutcome? step = null;
            Mutate(() =>
            {
                step = operation.Steps?.FirstOrDefault(candidate => candidate.Label == label && candidate.Status == StepStatuses.Pending);
                if (step is not null)
                {
                    step.Status = StepStatuses.Running;
                    step.StartedAt = Timestamps.Now(service._clock);
                }
                _runningStep = step;
                operation.Step = label;
            });
            return step;
        }

        private void FinishStep(StepOutcome? step, string status) => Mutate(() =>
        {
            if (step is not null)
            {
                step.Status = status;
                step.FinishedAt = Timestamps.Now(service._clock);
                if (step.Progress is not null) step.Progress = step.Progress with { Current = [] };
            }
            _runningStep = null;
        });

        /// <summary>Runs a command as a step of this operation: its label, its command line in the log, its output.</summary>
        private async Task<CommandResult> RunStepAsync(Command command)
        {
            var outcome = StartStep(command.Label);
            Log($"{command.Label}\n  {Planner.Format(command)}");
            var privileged = PrivilegedCommandRunner.IsPrivileged(service.Config, command);
            if (privileged) Mutate(() => operation.FinishingPrivilegedStep = true);
            CommandResult result;
            try
            {
                result = await service.Runner.RunAsync(command, Output, token).ConfigureAwait(false);
            }
            finally
            {
                if (privileged) lock (service._gate) operation.FinishingPrivilegedStep = false;
            }
            FinishStep(outcome, StepStatuses.Succeeded);
            return result;
        }

        public async Task<Operation> ExecuteAsync(IDisposable release, CancellationTokenSource cancellation)
        {
            var r = operation.Request;
            try
            {
                try
                {
                    await ProgramAsync(r).ConfigureAwait(false);
                    lock (service._gate)
                    {
                        operation.Status = OperationStatuses.Succeeded;
                        operation.Step = r.DryRun ? "Preview complete" : "Complete";
                    }
                }
                catch (Exception error)
                {
                    var cancelled = error is OperationCanceledException && token.IsCancellationRequested;
                    lock (service._gate)
                    {
                        operation.Status = cancelled ? OperationStatuses.Cancelled : OperationStatuses.Failed;
                        if (!cancelled && error is ManagerException { Explanation: { } explanation }) operation.Explanation = explanation;
                        operation.Error = cancelled
                            ? "Cancelled. Any completed activation or input update remains applied."
                            : error is ManagerException ? error.Message : error.ToString();
                        foreach (var step in operation.Steps ?? [])
                        {
                            if (step.Status == StepStatuses.Running)
                            {
                                step.Status = cancelled ? StepStatuses.Cancelled : StepStatuses.Failed;
                                step.FinishedAt = Timestamps.Now(service._clock);
                            }
                            else if (step.Status == StepStatuses.Pending) step.Status = StepStatuses.Skipped;
                        }
                    }
                    Log(operation.Error);
                }
            }
            finally
            {
                lock (service._gate) operation.FinishedAt = Timestamps.Now(service._clock);
                try
                {
                    if (r.Action is Actions.Switch or Actions.Boot)
                    {
                        var root = Paths.BuildRoot(service.Config);
                        if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(e.Message);
                }
                try
                {
                    lock (service._gate) Persist();
                }
                finally
                {
                    release.Dispose();
                    lock (service._gate)
                    {
                        service._current = null;
                        service.Changed(operation);
                    }
                    cancellation.Dispose();
                }
            }
            lock (service._gate) return operation.Clone();
        }

        private async Task ProgramAsync(Request r)
        {
            foreach (var note in plan.Notes) Log(note);
            if (r.Action == Actions.Check)
            {
                var outcome = StartStep("Check inputs and package releases");
                if (r.DryRun)
                {
                    Log("Would check inputs and package releases without changing the checkout.");
                    FinishStep(outcome, StepStatuses.Succeeded);
                    return;
                }
                var report = await service.UpdateChecker.CheckAsync(service.Config, RunStepAsync, token).ConfigureAwait(false);
                _store.SaveUpdates(report);
                foreach (var item in report.Items) Log($"{item.Name}: {item.Current} → {item.Latest}");
                foreach (var error in report.Errors) Log($"Check failed: {error}");
                Log($"{report.Items.Count} updates found{(report.Errors.Count > 0 ? $"; {report.Errors.Count} checks failed" : "")}.");
                if (report.Errors.Count > 0)
                    throw new ManagerException("Some update checks failed. The report contains the successful results and errors.");
                FinishStep(outcome, StepStatuses.Succeeded);
                return;
            }
            if (Actions.IsPackage(r.Action))
            {
                StartStep($"Update the {PackageName(r.Action)} package pin");
                throw new ManagerException("Package pin updates run in the Bun version until their updaters are ported (W300-029).");
            }
            foreach (var step in plan.Steps)
            {
                token.ThrowIfCancellationRequested();
                if (r.DryRun)
                {
                    var outcome = StartStep(step.Label);
                    Log(Planner.Format(step));
                    FinishStep(outcome, StepStatuses.Succeeded);
                }
                else await RunStepAsync(step).ConfigureAwait(false);
            }
        }
    }

    // Primary constructor parameters are not visible in the nested Run class.
    private ICommandRunner Runner => runner;
    private IUpdateChecker UpdateChecker => updates;
}
