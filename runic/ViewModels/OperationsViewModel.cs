using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NixManager.Domain;
using NixManager.Operations;
using Runic.Application.Views;
using Runic.Navigation;

namespace NixManager.ViewModels;

/// <summary>
/// The operations of one checkout as the dashboard shows them: the running operation with its steps, progress and
/// live log, the recent history, the update report, and commands to start and cancel operations.
/// </summary>
/// <remarks>
/// <para>
/// The ViewModel is a view of <see cref="IOperationService"/>; it neither owns nor ends operations. Closing a
/// Window disposes it and leaves a running operation running, as D025.1 decides, and a new Window shows it again
/// from <see cref="IOperationService.Active"/> and the stored log. W300-030 declares the Window that binds it,
/// W300-031 hosts the service in the daemon.
/// </para>
/// <para>
/// Service events arrive on the service's threads and are committed in model-context turns with
/// <see cref="IRunicModelContext.TryPost"/>, so state changes reach the frontend in order.
/// </para>
/// </remarks>
public sealed partial class OperationsViewModel : ObservableObject, IDisposable
{
    /// <summary>The live log keeps this much text, as the dashboard does.</summary>
    public const int LogCharacters = 128_000;

    private readonly IOperationService _service;
    private readonly IRunicModelContext _context;
    private readonly IDisposable _subscription;
    private readonly StringBuilder _log = new();
    private long _logEnd;

    public OperationsViewModel(IOperationService service, IRunicModelContext context)
    {
        _service = service;
        _context = context;
        _subscription = service.Subscribe(OnEvent);
        Active = service.Active;
        Operations = service.History();
        Updates = service.Updates();
        SelectedOperationId = Active?.Id ?? Operations.FirstOrDefault()?.Id;
        LoadLog();
    }

    /// <summary>The operation running in the service, with its steps and their progress.</summary>
    [ObservableProperty]
    public partial Operation? Active { get; private set; }

    /// <summary>The latest operations of this checkout, newest first; records of the Bun version included.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Operation> Operations { get; private set; }

    [ObservableProperty]
    public partial UpdateReport? Updates { get; private set; }

    /// <summary>The operation whose log <see cref="Log"/> shows.</summary>
    [ObservableProperty]
    public partial string? SelectedOperationId { get; set; }

    /// <summary>
    /// The end of the selected operation's log, growing while it runs. The page receives only what it appends, and
    /// how much it drops from its start once it keeps <see cref="LogCharacters"/>.
    /// </summary>
    [ObservableProperty, RunicAppendOnlyText]
    public partial string Log { get; private set; } = "";

    /// <summary>The stored log is longer than <see cref="Log"/>.</summary>
    [ObservableProperty]
    public partial bool LogTruncated { get; private set; }

    /// <summary>Why the last start failed, such as another operation running.</summary>
    [ObservableProperty]
    public partial string? StartError { get; private set; }

    public bool Busy => Active is not null;

    partial void OnActiveChanged(Operation? value) => OnPropertyChanged(nameof(Busy));

    partial void OnSelectedOperationIdChanged(string? value) => LoadLog();

    /// <summary>Starts an operation; it runs in the service, beyond this ViewModel.</summary>
    [RelayCommand]
    private void Start(Request request)
    {
        try
        {
            var run = _service.Start(request with { Host = request.Host is { Length: > 0 } ? request.Host : _service.Config.HostKey });
            StartError = null;
            Active = run.Operation;
            SelectedOperationId = run.Operation.Id;
        }
        catch (ManagerException error)
        {
            StartError = error.Message;
        }
    }

    /// <summary>Asks the running operation to stop; a running privileged step finishes first.</summary>
    [RelayCommand]
    private void Cancel() => _service.Cancel();

    /// <summary>Reads the history, the update report and the selected log again.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Operations = _service.History();
        Updates = _service.Updates();
        LoadLog();
    }

    private void LoadLog()
    {
        _log.Clear();
        _logEnd = 0;
        LogTruncated = false;
        if (SelectedOperationId is { } id)
        {
            try
            {
                var log = _service.Log(id, LogCharacters);
                _log.Append(log.Text);
                _logEnd = log.End;
                LogTruncated = log.Truncated;
            }
            catch (ManagerException) { }
        }
        Log = _log.ToString();
    }

    private void OnEvent(ManagerEvent e)
    {
        switch (e)
        {
            case OperationChanged changed:
                _context.TryPost(() =>
                {
                    var operation = changed.Operation;
                    if (operation.Status == OperationStatuses.Running) Active = operation;
                    else
                    {
                        if (Active?.Id == operation.Id) Active = null;
                        Operations = _service.History();
                        Updates = _service.Updates();
                    }
                });
                break;
            case OperationOutput output:
                _context.TryPost(() =>
                {
                    // Offsets splice the live stream onto the stored tail without gaps or duplicates.
                    if (output.OperationId != SelectedOperationId || output.Offset < _logEnd) return;
                    _log.Append(output.Text);
                    _logEnd = output.Offset + Encoding.UTF8.GetByteCount(output.Text);
                    if (_log.Length > LogCharacters)
                    {
                        _log.Remove(0, _log.Length - LogCharacters);
                        LogTruncated = true;
                    }
                    Log = _log.ToString();
                });
                break;
            case OperationProgress progress:
                _context.TryPost(() =>
                {
                    if (Active is not { } active || active.Id != progress.OperationId) return;
                    var copy = active.Clone();
                    if (copy.Steps?.FirstOrDefault(step => step.Id == progress.StepId) is { } step) step.Progress = progress.Progress;
                    Active = copy;
                });
                break;
        }
    }

    public void Dispose() => _subscription.Dispose();
}
