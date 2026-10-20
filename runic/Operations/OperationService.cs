using System.Text;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Observation;
using NixManager.Reviews;
using NixManager.Updates;

namespace NixManager.Operations;

/// <summary>
/// Runs one operation at a time and publishes its progress.
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
    /// Keeps edits being prepared in one step (<c>then: "apply"</c>) from being applied, so they wait for review
    /// (<c>hold</c>). False when <paramref name="id"/> is not such a running operation.
    /// </summary>
    bool Hold(string id);
    /// <summary>
    /// Applies the home part of a review whose system part was applied on next boot, once the computer has started
    /// into that system (<c>resumeAfterBoot</c>); null when there is none, or an operation runs.
    /// </summary>
    Task<OperationRun?> ResumeAfterBootAsync(string booted = "/run/booted-system");
    /// <summary>
    /// Receives every event in order, synchronously on the thread that produced it; keep the listener short and
    /// hand work to another context. Dispose the result to stop.
    /// </summary>
    IDisposable Subscribe(Action<ManagerEvent> listener);
}

/// <param name="Operation">The operation's first snapshot.</param>
/// <param name="Completion">Its final record; it never fails.</param>
public sealed record OperationRun(Operation Operation, Task<Operation> Completion);

/// <param name="network">
/// Answers the package pin updates' release lookups; without it they share the update checker's, or ask GitHub.
/// </param>
public sealed partial class OperationService(Config config, ICommandRunner runner, IUpdateChecker updates, TimeProvider? clock = null, INetwork? network = null)
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

    public Plan Plan(Request request)
    {
        var resolved = Resolve(request);
        return Planner.Plan(config, resolved.Request, resolved.Review, resolved.Undo?.Point);
    }

    /// <summary>The change an undo review reverses: its operation, undo point and the earlier file contents.</summary>
    private sealed record UndoSource(Operation Operation, UndoPoint Point, Dictionary<string, string> Contents);

    private sealed record Resolved(Request Request, BuildReview? Review = null, UndoSource? Undo = null);

    /// <summary>The undo point an applied change recorded, with the earlier file contents (<c>undoPoint</c>).</summary>
    private UndoSource UndoPointOf(string id)
    {
        var operation = History().FirstOrDefault(op => op.Id == id);
        if (operation?.Undo is not { } point)
            throw new ManagerException("This change cannot be undone: it recorded nothing to go back to.");
        var path = _store.PathOf(id, "undo");
        var contents = File.Exists(path)
            ? ManagerJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
            : [];
        return new UndoSource(operation, point, contents);
    }

    /// <summary>Completes a request: new review IDs, the stored review to apply, and the change to undo (<c>resolve</c>).</summary>
    private Resolved Resolve(Request r)
    {
        if (r.Action == Actions.ReviewBuild && r.Undo is { } undoId)
        {
            var undo = UndoPointOf(undoId);
            // An undo covers what the change applied, on the host it applied to.
            return new Resolved(r with
            {
                Scope = undo.Operation.Request.Scope,
                Host = undo.Operation.Request.Host,
                ReviewId = r.ReviewId ?? Guid.NewGuid().ToString("D"),
            }, Undo: undo);
        }
        if (r.Action == Actions.ReviewBuild && r.ReviewId is null) return new Resolved(r with { ReviewId = Guid.NewGuid().ToString("D") });
        if (r.Action == Actions.ReviewApply && r.ReviewId is { } reviewId) return new Resolved(r, ReviewStore.Get(config, reviewId));
        return new Resolved(r);
    }

    public bool Hold(string id)
    {
        lock (_gate)
        {
            var operation = _current?.Operation;
            if (operation?.Id != id || operation.Request.Then != "apply") return false;
            operation.Held = "You chose to review it first.";
            Changed(operation);
            return true;
        }
    }

    public Task<OperationRun?> ResumeAfterBootAsync(string booted = "/run/booted-system")
    {
        var system = Native.RealPath(booted);
        var review = ReviewStore.List(config).FirstOrDefault(item =>
            item.AfterBoot is { } afterBoot && afterBoot.System == system && item.Host == config.HostKey);
        if (review is null || Busy) return Task.FromResult<OperationRun?>(null);
        ReviewStore.ForgetBoot(config, review);
        return Task.FromResult<OperationRun?>(Start(Request.Create() with
        {
            Action = Actions.ReviewApply, Scope = Scopes.Home, Host = review.Host, ReviewId = review.Id, Activation = "switch",
        }));
    }

    /// <summary>What applying a review changes, in words (<c>describeReview</c>).</summary>
    private static List<string> DescribeReview(BuildReview review)
    {
        var changes = new List<string>();
        if (review.Edits is { } edits) changes.AddRange(edits.Changes.Select(change => ChangeRules.Describe(change).Title));
        if (review.Inputs is { } inputs) changes.Add(inputs.Names.Count > 0 ? $"Update {string.Join(", ", inputs.Names)}" : "Update all inputs");
        if (review.Undo is { } undo) changes.Add($"Undo: {string.Join("; ", undo.Point.Changes)}");
        if (review.Source == ReviewSources.Generation && review.Generation is { } generation && review.Undo is null)
            changes.Add(review.Scope == Scopes.Home
                ? $"Go back to restore point {generation} of your user"
                : $"Go back to restore point {generation} of the system");
        return changes.Count > 0 ? changes : ["Apply the configuration"];
    }

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

    public OperationRun Start(Request input)
    {
        var (request, review, undo) = Resolve(input);
        var plan = Planner.Plan(config, request, review, undo?.Point);
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
            if (request.Action == Actions.ReviewBuild)
                OperationStore.CreatePrivateDirectory(Paths.ReviewRoot(config, request.ReviewId!));
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
        var run = new Run(this, operation, plan, review, undo, cancellation.Token);
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
    private sealed class Run(OperationService service, Operation operation, Plan plan, BuildReview? review, UndoSource? undo, CancellationToken token)
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

        /// <summary>Set when an activation succeeds, so the change can be undone.</summary>
        private (UndoGenerations Generations, List<UndoFile> Files, Dictionary<string, string> Contents)? _undoable;

        /// <summary>Runs a command quietly: without a step of its own, output and log.</summary>
        private Task<CommandResult> Quiet(Command command) => service.Runner.RunAsync(command with { Quiet = true }, _ => { }, token);

        public async Task<Operation> ExecuteAsync(IDisposable release, CancellationTokenSource cancellation)
        {
            var r = operation.Request;
            // Set when prepared edits are applied right after, in one step.
            Request? follow = null;
            try
            {
                try
                {
                    await ProgramAsync(r).ConfigureAwait(false);
                    lock (service._gate)
                    {
                        operation.Status = OperationStatuses.Succeeded;
                        operation.Step = r.DryRun ? "Preview complete" : "Complete";
                        if (review is not null && _undoable is { } undoable)
                        {
                            operation.Undo = new UndoPoint { Changes = DescribeReview(review), Generations = undoable.Generations, Files = undoable.Files };
                            OperationStore.AtomicJson(_store.PathOf(operation.Id, "undo"),
                                JsJson.Indented(System.Text.Json.JsonSerializer.SerializeToNode(undoable.Contents, ManagerJson.Default.DictionaryStringString)));
                        }
                    }
                    if (r.Then == "apply" && !r.DryRun)
                    {
                        string? held;
                        lock (service._gate) held = operation.Held;
                        try
                        {
                            held ??= ChangeRules.OneStepReviewProblem(ReviewStore.Get(service.Config, r.ReviewId!));
                        }
                        catch (Exception error)
                        {
                            held = error.Message;
                        }
                        lock (service._gate) operation.Held = held;
                        if (held is not null) Log($"Waiting for your review: {held}");
                        else follow = Request.Create() with
                        {
                            Action = Actions.ReviewApply, Scope = Scopes.Home, Host = r.Host, ReviewId = r.ReviewId, Activation = "switch",
                        };
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
                    await CleanUpReviewAsync(r).ConfigureAwait(false);
                    if (r.Action is Actions.Switch or Actions.Boot)
                    {
                        var root = Paths.BuildRoot(service.Config);
                        if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception e)
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
            if (follow is not null)
            {
                try
                {
                    service.Start(follow);
                }
                catch (Exception error)
                {
                    lock (service._gate)
                    {
                        operation.Held = $"It could not be applied: {error.Message}";
                        Persist();
                        service.Changed(operation);
                    }
                }
            }
            lock (service._gate) return operation.Clone();
        }

        /// <summary>
        /// Unfinished reviews would otherwise retain partial outputs, and a review's GC roots are redundant once its
        /// profiles reference them; applied edits leave the draft.
        /// </summary>
        private async Task CleanUpReviewAsync(Request r)
        {
            if (r.ReviewId is not { } reviewId || r.DryRun) return;
            string status;
            lock (service._gate) status = operation.Status;
            if (r.Action == Actions.ReviewBuild && status != OperationStatuses.Succeeded) ReviewStore.Remove(service.Config, reviewId);
            if (r.Action == Actions.ReviewApply && status == OperationStatuses.Succeeded)
            {
                if (review?.Edits is { } edits)
                    Drafts.Write(service.Config, SourceEdits.Remaining(Drafts.Read(service.Config), edits.Changes));
                if (r.Activation == "boot" && review?.Home is not null && review.System is not null && r.Scope != Scopes.System)
                    await ReviewStore.KeepForBootAsync(service.Config, review).ConfigureAwait(false);
                else ReviewStore.Remove(service.Config, reviewId);
            }
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
                var outcome = StartStep($"Update the {PackageName(r.Action)} package pin");
                // Each command is a step of its own in the log.
                await service.Packages.UpdateAsync(service.Config, r, RunStepAsync, Log, token).ConfigureAwait(false);
                FinishStep(outcome, StepStatuses.Succeeded);
                return;
            }
            var config = service.Config;
            var applying = r.Action == Actions.ReviewApply && !r.DryRun;
            // The checkout state a review is evaluated from; captured before any command reads it, so later edits
            // make the review visibly stale.
            ReviewBase? reviewBase = null;
            // The draft as it was when the review started; later edits wait for the next one.
            var draft = r.Edits == true ? Drafts.Read(config) : [];
            Staged? staged = null;
            ReviewEdits? edits = null;
            // What applying replaces, recorded so the change can be undone.
            var replacedFiles = new List<UndoFile>();
            var replacedContents = new Dictionary<string, string>(StringComparer.Ordinal);
            void Record(IReadOnlyDictionary<string, string?> before)
            {
                var (files, contents) = SourceEdits.UndoFiles(config, before);
                replacedFiles.AddRange(files);
                foreach (var (path, text) in contents) replacedContents[path] = text;
            }
            int? homeGeneration = null, systemGeneration = null;
            if (applying)
            {
                if (r.Scope != Scopes.System && review?.Home is not null) homeGeneration = Repository.ProfileGeneration(config.HomeProfile);
                if (r.Scope != Scopes.Home && review?.System is not null) systemGeneration = Repository.ProfileGeneration(Paths.SystemProfile);
            }
            UndoGenerations Generations() => new() { Home = homeGeneration, System = systemGeneration };

            if (r.Action == Actions.ReviewBuild && !r.DryRun)
                reviewBase = new ReviewBase((await Repository.StateAsync(config.Flake, token).ConfigureAwait(false)).Fingerprint, Repository.ReadLock(config.Flake));

            async Task StepsAsync()
            {
                foreach (var step in plan.Steps)
                {
                    token.ThrowIfCancellationRequested();
                    if (r.DryRun)
                    {
                        var outcome = StartStep(step.Label);
                        Log(Planner.Format(step));
                        FinishStep(outcome, StepStatuses.Succeeded);
                    }
                    else if (step.Internal is { } kind)
                    {
                        var outcome = StartStep(step.Label);
                        switch (kind)
                        {
                            case InternalSteps.Compare:
                            {
                                long downloaded;
                                lock (service._gate) downloaded = operation.Steps?.Sum(s => s.Progress?.Bytes.Done ?? 0) ?? 0;
                                var result = await ReviewStore.CompareAsync(config, r, reviewBase!, operation.Id, Quiet, edits,
                                    undo is null ? null : new ReviewUndo { OperationId = undo.Operation.Id, AppliedAt = undo.Operation.FinishedAt ?? undo.Operation.StartedAt, Point = undo.Point },
                                    downloaded, service._clock).ConfigureAwait(false);
                                var packages = (result.Home?.Changes.Count ?? 0) + (result.System?.Changes.Count ?? 0);
                                Log($"{packages} package {(packages == 1 ? "change is" : "changes are")} ready for review.");
                                break;
                            }
                            case InternalSteps.StageSources:
                                staged = await SourceEdits.StageSourcesAsync(config, r, draft, Quiet).ConfigureAwait(false);
                                foreach (var file in staged.Files) Log($"Edited {file.Path}");
                                break;
                            case InternalSteps.VerifyEdits:
                                edits = await SourceEdits.VerifyEditsAsync(config, r, staged!, draft, Quiet).ConfigureAwait(false);
                                foreach (var effect in edits.Effects)
                                {
                                    foreach (var item in effect.Added) Log($"{effect.Scope}: adds {item.Name} ({item.File})");
                                    foreach (var item in effect.Removed) Log($"{effect.Scope}: removes {item.Name} ({item.File})");
                                    foreach (var option in effect.Options)
                                        Log($"{effect.Scope}: {option.Option} = {(option.After.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "default" : JsJson.Stringify(option.After))}");
                                }
                                break;
                            case InternalSteps.WriteSources:
                            {
                                var before = SourceEdits.ReadSources(config, review!.Edits!.Files.Select(file => file.Path));
                                await SourceEdits.WriteSourcesAsync(config, review.Id, review.Edits, Quiet).ConfigureAwait(false);
                                Record(before);
                                break;
                            }
                            case InternalSteps.RestoreSources:
                            {
                                var point = review!.Undo!.Point;
                                var contents = service.UndoPointOf(review.Undo.OperationId).Contents;
                                var before = SourceEdits.ReadSources(config, point.Files.Select(file => file.Path));
                                await SourceEdits.RestoreSourcesAsync(config, point, contents, Quiet).ConfigureAwait(false);
                                Record(before);
                                break;
                            }
                            default:
                            {
                                var before = SourceEdits.ReadSources(config, ["flake.lock"]);
                                ReviewStore.WriteReviewedLock(config, review!);
                                Record(before);
                                break;
                            }
                        }
                        FinishStep(outcome, StepStatuses.Succeeded);
                    }
                    else await RunStepAsync(step).ConfigureAwait(false);
                }
            }

            // After a failed apply the earlier generations become active again and the written files are put back,
            // so the computer is as it was before.
            async Task GoBackAsync(ManagerException error)
            {
                StepOutcome? failed;
                bool home, activatedSystem;
                lock (service._gate)
                {
                    failed = operation.Steps!.FirstOrDefault(step => step.Status == StepStatuses.Running);
                    bool Touched(string prefix) => operation.Steps!.Any(step => step.Label.StartsWith(prefix, StringComparison.Ordinal) && step.Status != StepStatuses.Pending);
                    home = Touched("Select Home Manager");
                    activatedSystem = Touched("Activate system");
                }
                var back = Planner.GoBackSteps(config, home: home ? homeGeneration : null, system: activatedSystem ? systemGeneration : null);
                var files = replacedFiles.Count > 0;
                if (back.Count == 0 && !files) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
                FinishStep(failed, StepStatuses.Failed);
                const string putBack = "Put back the configuration files";
                var labels = back.Select(step => step.Label).Concat(files ? [putBack] : []).ToList();
                Mutate(() =>
                {
                    var count = operation.Steps!.Count;
                    operation.Steps.AddRange(labels.Select((label, index) =>
                        new StepOutcome { Id = StepId(count + index, label), Label = label, Status = StepStatuses.Pending }));
                });
                Log("Going back to how things were before.");
                // Each part goes back on its own: files are put back even when a generation could not be activated.
                ManagerException? problem = null;
                foreach (var step in back)
                {
                    try { await RunStepAsync(step).ConfigureAwait(false); }
                    catch (ManagerException e) { problem ??= e; }
                }
                if (files)
                {
                    var step = StartStep(putBack);
                    try
                    {
                        await SourceEdits.RestoreSourcesAsync(config, new UndoPoint { Changes = [], Generations = Generations(), Files = replacedFiles }, replacedContents, Quiet)
                            .ConfigureAwait(false);
                        FinishStep(step, StepStatuses.Succeeded);
                    }
                    catch (ManagerException e)
                    {
                        FinishStep(step, StepStatuses.Failed);
                        problem ??= e;
                    }
                }
                // Without an earlier home generation the new one stays selected, so only the rest went back.
                var first = home && homeGeneration is null;
                lock (service._gate)
                    operation.Rollback = problem is null ? new RollbackOutcome { Done = !first } : new RollbackOutcome { Done = false, Error = problem.Message };
                var what = error.Explanation?.Body ?? error.Message;
                throw new ManagerException(error.Message, new Explanation
                {
                    Title = error.Explanation?.Title ?? (failed is not null ? $"{failed.Label} failed" : "Applying failed"),
                    Body = problem is null
                        ? first
                            ? files
                                ? $"{what} The files were put back, but there was no earlier restore point of your user to go back to, so the new version stays in use."
                                : $"{what} There was no earlier restore point of your user to go back to, so the new version stays in use."
                            : files
                                ? $"{what} Everything went back to how it was before, files included."
                                : $"{what} Everything went back to how it was before."
                        : $"{what} Going back did not work either: {problem.Message} The Restore points page can take you back.",
                });
            }

            if (applying)
            {
                try { await StepsAsync().ConfigureAwait(false); }
                catch (ManagerException error) { await GoBackAsync(error).ConfigureAwait(false); }
            }
            else await StepsAsync().ConfigureAwait(false);
            if (applying && (r.Activation ?? "switch") == "switch" && (homeGeneration is not null || systemGeneration is not null))
                _undoable = (Generations(), replacedFiles, replacedContents);
            var preferences = Preferences.Read(config);
            if (applying && replacedFiles.Count > 0 && preferences["history"] is System.Text.Json.Nodes.JsonValue history
                && history.TryGetValue<bool>(out var keep) && keep)
            {
                var backup = preferences["backup"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var push) && push;
                var kept = await ChangeHistory.RecordChangeAsync(config, replacedFiles.Select(file => file.Path).ToList(), DescribeReview(review!), backup, Quiet)
                    .ConfigureAwait(false);
                if (kept is not null) Log(kept);
            }
        }
    }

    // Primary constructor parameters are not visible in the nested Run class.
    private ICommandRunner Runner => runner;
    private IUpdateChecker UpdateChecker => updates;
    private PackageUpdater Packages => field ??= new PackageUpdater(network ?? (updates as UpdateChecker)?.Network ?? new HttpNetwork());
}
