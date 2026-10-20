using NixManager.Configuration;
using NixManager.Domain;
using NixManager.Execution;

namespace NixManager.Setup;

/// <summary>A step of creating the checkout, with its status: waiting, running, done or failed.</summary>
public sealed record SetupStepState(string Id, DisplayText Label, string Status);

/// <summary>Why the dashboard could not start; nothing else is offered then.</summary>
public sealed record SetupProblem(DisplayText Message, string Flake);

/// <summary>The setup assistant's state as the interface shows it.</summary>
public sealed record SetupState
{
    public SetupInfo? Info { get; init; }
    /// <summary>Where the new checkout goes, once creation started.</summary>
    public string? Target { get; init; }
    public IReadOnlyList<SetupStepState> Steps { get; init; } = [];
    public bool Running { get; init; }
    public DisplayText? Error { get; init; }
    /// <summary>The dashboard's address, once it runs.</summary>
    public string? Next { get; init; }
    public SetupProblem? Problem { get; init; }
}

/// <summary>
/// The setup assistant's state machine, without HTTP: it reads
/// the computer, refreshes on request, and creates the checkout once, reporting each step. The dashboard (W300-030)
/// shows it before there is a checkout to manage, or shows only <see cref="SetupState.Problem"/>.
/// </summary>
public sealed class SetupAssistantService
{
    public const string StatusWaiting = "waiting";
    public const string StatusRunning = "running";
    public const string StatusDone = "done";
    public const string StatusFailed = "failed";

    private readonly ICommandRunner _runner;
    private readonly ManagerEnvironment _environment;
    private readonly string? _source;
    private readonly Lock _gate = new();
    private readonly List<Action<SetupState>> _listeners = [];
    private SetupState _state;

    /// <param name="problem">Why the dashboard could not start; the assistant then only shows it.</param>
    /// <param name="source">The classic configuration; <see cref="SetupAssistant.Source"/> by default.</param>
    public SetupAssistantService(ICommandRunner runner, ManagerEnvironment? environment = null, SetupProblem? problem = null, string? source = null)
    {
        _runner = runner;
        _environment = environment ?? ManagerEnvironment.Current;
        _source = source;
        _state = new SetupState { Steps = Waiting(), Problem = problem };
        Loaded = problem is null ? ReadAsync() : Task.CompletedTask;
    }

    private static List<SetupStepState> Waiting() => SetupSteps.All.Select(step => new SetupStepState(step.Id, step.Label, StatusWaiting)).ToList();

    /// <summary>The first reading of the computer; <see cref="State"/> has its result once it completes.</summary>
    public Task Loaded { get; }

    public SetupState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>The created checkout's path, once creation succeeded (<c>onCreated</c>).</summary>
    public event Action<string>? Created;

    /// <summary>Calls <paramref name="listener"/> with each new state; dispose the result to stop.</summary>
    public IDisposable Subscribe(Action<SetupState> listener)
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

    private void Update(Func<SetupState, SetupState> change)
    {
        SetupState next;
        Action<SetupState>[] listeners;
        lock (_gate)
        {
            next = _state = change(_state);
            listeners = [.. _listeners];
        }
        foreach (var listener in listeners)
        {
            try { listener(next); }
            catch (Exception e) { Console.Error.WriteLine(e); }
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            var info = await SetupAssistant.ReadAsync(_runner, _environment, _source).ConfigureAwait(false);
            Update(state => state with { Info = info });
        }
        catch (Exception error) when (error is ManagerException or IOException or UnauthorizedAccessException)
        {
            Update(state => state with { Error = DisplayText.From(error) });
        }
    }

    private void Refuse()
    {
        if (_state.Problem is not null) throw new ManagerException(DisplayText.Of("stored_nix_manager_could_not_start"));
        if (_state.Running || _state.Next is not null) throw new ManagerException(DisplayText.Of("stored_setup_is_already_running"));
    }

    /// <summary>Reads the computer again, such as after the user enabled flakes or fixed /etc/nixos.</summary>
    /// <exception cref="ManagerException">The dashboard could not start, or setup runs.</exception>
    public async Task<SetupState> RefreshAsync()
    {
        await Loaded.ConfigureAwait(false);
        lock (_gate) Refuse();
        Update(state => state with { Error = null });
        await ReadAsync().ConfigureAwait(false);
        return State;
    }

    /// <summary>
    /// Starts creating the checkout in <paramref name="target"/> (<c>~</c> is the home folder) and returns the state;
    /// the creation goes on in the background, reported through <see cref="Subscribe"/> and <see cref="Completion"/>.
    /// </summary>
    /// <exception cref="ManagerException">The folder is not suitable, setup is blocked, or it runs already.</exception>
    public SetupState Create(string target)
    {
        SetupInfo info;
        string path;
        lock (_gate)
        {
            Refuse();
            path = target == "~" ? _environment.HomeDirectory : target.StartsWith("~/", StringComparison.Ordinal) ? _environment.HomeDirectory + target[1..] : target;
            var refused = _state.Info?.Blocked ?? SetupAssistant.TargetProblem(path, _environment.HomeDirectory);
            if (refused is not null) throw new ManagerException(refused);
            // Before the computer was read there is nothing to create from; the reading's error says why.
            info = _state.Info ?? throw new ManagerException(_state.Error ?? DisplayText.Of("stored_setup_is_still_reading_this_computer"));
            _state = _state with { Running = true, Error = null, Target = path, Steps = Waiting() };
        }
        Update(state => state);
        Completion = CreateAsync(info, path);
        return State;
    }

    /// <summary>The running or last creation; it never fails, a failure is in <see cref="SetupState.Error"/>.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    private async Task CreateAsync(SetupInfo info, string target)
    {
        static List<SetupStepState> Finish(IEnumerable<SetupStepState> steps, string status) =>
            steps.Select(step => step.Status == StatusRunning ? step with { Status = status } : step).ToList();
        void Progress(string id) => Update(state => state with
        {
            Steps = Finish(state.Steps, StatusDone).Select(step => step.Id == id ? step with { Status = StatusRunning } : step).ToList(),
        });
        await Task.Yield();
        string path;
        try
        {
            path = await SetupAssistant.CreateCheckoutAsync(info, target, Progress, _runner, _environment, _source).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Update(state => state with { Steps = Finish(state.Steps, StatusFailed), Running = false, Error = DisplayText.From(error) });
            return;
        }
        Update(state => state with { Steps = Finish(state.Steps, StatusDone), Running = false });
        Created?.Invoke(path);
    }

    /// <summary>Sends the assistant on to the dashboard at <paramref name="next"/> (<c>handOver</c>).</summary>
    public void HandOver(string next) => Update(state => state with { Next = next });
}
