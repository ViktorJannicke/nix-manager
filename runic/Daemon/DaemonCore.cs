using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using NixManager.Dashboard;
using NixManager.Domain;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using Runic.Platform;

namespace NixManager.Daemon;

/// <summary>How the running instance came to be.</summary>
public enum DaemonMode
{
    /// <summary><c>nix-manager tray</c>, the systemd user service: windowless with the status icon until it is stopped.</summary>
    Tray,
    /// <summary>
    /// <c>nix-manager ui</c> while no daemon runs: the same process with its Window open, which ends once the Window is
    /// closed and nothing runs, as the Bun version's dashboard did.
    /// </summary>
    Ui,
}

/// <summary>How the running instance behaves.</summary>
public sealed record DaemonSettings
{
    public DaemonMode Mode { get; init; } = DaemonMode.Tray;
    /// <summary>Between read-only update checks (<c>--interval</c>).</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(360);
    /// <summary>Before the first update check after start.</summary>
    public TimeSpan FirstCheck { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>The dashboard Window as the running instance opens it.</summary>
public interface IDashboardWindows
{
    bool IsOpen { get; }

    /// <summary>
    /// Opens the dashboard, or brings the open one forward with <paramref name="context"/>; true when it opened.
    /// <paramref name="closed"/> is called, on any thread, once a Window it opened closes.
    /// </summary>
    Task<bool> ShowAsync(DesktopActivationContext? context, Action closed);

    /// <summary>Closes the open dashboard.</summary>
    ValueTask CloseAsync();
}

/// <summary>
/// The per-user daemon of D025: one process that owns every operation, opens and raises the dashboard, answers later
/// launches over the instance socket, shows the status icon and notifications, and schedules the work the Bun tray
/// did (<c>src/tray.ts</c>, <c>startTray</c> and <c>main</c> in <c>src/main.ts</c>).
/// </summary>
/// <remarks>
/// <para>
/// Launches, icon and notification activations and the service's events arrive on worker threads and are posted to
/// one loop (<see cref="RunAsync"/>), which runs on the Desktop host's event loop: Windows open and close only there.
/// Forwarded operations are served on their own tasks; they need only the operation service.
/// </para>
/// <para>
/// Closing the Window leaves a running operation running (D025.1). The daemon ends on Quit or a termination signal;
/// it then cancels a running operation, lets its final events reach the command lines that follow it, and closes the
/// Window.
/// </para>
/// </remarks>
public sealed class DaemonCore
{
    private readonly IOperationService _service;
    private readonly BackgroundTasks _background;
    private readonly Observer _observer;
    private readonly IDesktopStatusIcon _icon;
    private readonly IDesktopNotifications _notifications;
    private readonly DaemonSettings _settings;
    private readonly TimeProvider _clock;
    private readonly TextWriter _log;
    private readonly Func<long?> _freeSpace;
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Task, byte> _serving = new();
    // Operations a command line asked for: its terminal shows how they ended.
    private readonly ConcurrentDictionary<string, byte> _forwarded = new();
    private readonly Lock _startGate = new();
    private readonly ConcurrentDictionary<string, BuildReview> _noticeReviews = new();
    private IDashboardWindows? _windows;
    private volatile bool _ending;
    private string _notified = "";
    private string _announced = "";
    private string _iconKey = "";
    private bool _iconShown;
    private (DateTime Written, TrayText Text)? _text;

    private sealed record Show(DesktopActivationContext? Context)
    {
        public IApplicationLaunchReply? Reply { get; init; }
    }

    private sealed record WindowClosed;
    private sealed record Changed;
    private sealed record Ended(Operation Operation);
    private sealed record ServeEnded;
    private sealed record CheckDue;

    public DaemonCore(IOperationService service, BackgroundTasks background, Observer observer, IDesktopStatusIcon icon,
        IDesktopNotifications notifications, DaemonSettings settings, TimeProvider? clock = null, TextWriter? log = null,
        Func<long?>? freeSpace = null)
    {
        _service = service;
        _background = background;
        _observer = observer;
        _icon = icon;
        _notifications = notifications;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? Console.Error;
        _freeSpace = freeSpace ?? (() => BackgroundTasks.FreeSpace());
        _icon.Activated += (_, activation) => _inbox.Writer.TryWrite(activation);
        _notifications.Activated += (_, activation) => _inbox.Writer.TryWrite(activation);
    }

    private Config Config => _service.Config;

    /// <summary>
    /// The dashboard's language for the icon and the notifications: the one chosen in Settings, or the desktop's
    /// (<c>resolveLocale</c>). Read again when <c>preferences.json</c> changes, so a new choice reaches the menu with the
    /// next refresh.
    /// </summary>
    private TrayText Text
    {
        get
        {
            var path = DashboardPreferences.PathOf(Config);
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_text is { } cached && cached.Written == written) return cached.Text;
            var locale = DashboardPreferences.ResolveLocale(DashboardPreferences.Read(Config).Language, Environment.GetEnvironmentVariable);
            var text = TrayText.For(locale);
            _text = (written, text);
            return text;
        }
    }

    /// <summary>
    /// Handles a later launch; subscribe it to <c>IApplicationInstance.Launched</c> right after claiming. It returns at
    /// once: an operation is served on its own task, and a dashboard request waits for the loop.
    /// </summary>
    public void OnLaunch(ApplicationLaunch launch)
    {
        var request = DaemonProtocol.Parse(launch);
        var reply = launch.Reply;
        if (request is null)
        {
            // A launch that only hands over its arguments, such as an older build's, opens the dashboard.
            if (reply is null) _inbox.Writer.TryWrite(new Show(launch.PlatformContext));
            else Track(AnswerAsync(reply, (DaemonMessage.Note("Nix Manager does not understand this request."), 2)));
            return;
        }
        switch (request.Kind)
        {
            case DaemonRequestKinds.Operation when reply is not null:
                Track(ServeOperationAsync(request, reply));
                break;
            case DaemonRequestKinds.Daemon when reply is not null:
                Track(AnswerAsync(reply, _settings.Mode == DaemonMode.Tray
                    ? (DaemonMessage.Note("Nix Manager is already running in the tray."), 1)
                    : (DaemonMessage.Elsewhere("Nix Manager's dashboard runs; the tray starts once it closes."), DaemonProtocol.RunHere)));
                break;
            case DaemonRequestKinds.Ui when !Matches(request.Identity) || _ending:
                if (reply is not null) Track(AnswerAsync(reply, (DaemonMessage.Elsewhere(_ending ? "Nix Manager is ending." : ElsewhereText), DaemonProtocol.RunHere)));
                break;
            case DaemonRequestKinds.Ui:
                if (!_inbox.Writer.TryWrite(new Show(launch.PlatformContext) { Reply = reply }) && reply is not null)
                    Track(AnswerAsync(reply, (DaemonMessage.Elsewhere("Nix Manager is ending."), DaemonProtocol.RunHere)));
                break;
            default:
                if (reply is not null) Track(AnswerAsync(reply, (DaemonMessage.Note("Nix Manager does not understand this request."), 2)));
                break;
        }
    }

    private string ElsewhereText => $"The running Nix Manager manages {Config.Flake} ({Config.HostKey}).";

    private bool Matches(DaemonIdentity? identity)
    {
        if (identity is null) return true;
        static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return Normal(identity.Flake) == Normal(Config.Flake) && identity.HostKey == Config.HostKey && Normal(identity.StateDir) == Normal(Config.StateDir);
    }

    private void Track(Task task)
    {
        _serving.TryAdd(task, 0);
        _ = task.ContinueWith(done =>
        {
            _serving.TryRemove(done, out _);
            if (done.Exception is { } error) _log.WriteLine($"Answering a launch failed: {error.GetBaseException().Message}");
            _inbox.Writer.TryWrite(new ServeEnded());
        }, TaskScheduler.Default);
    }

    private static async Task AnswerAsync(IApplicationLaunchReply reply, (DaemonMessage Message, int Code) answer)
    {
        await reply.WriteAsync(DaemonProtocol.Serialize(answer.Message)).ConfigureAwait(false);
        await reply.CompleteAsync(answer.Code).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a command line's operation and streams its events and final record back (D025.4). A cancel request, or a
    /// command line that went away, cancels the operation, as Ctrl+C or ending the process does in a run of its own.
    /// </summary>
    private async Task ServeOperationAsync(DaemonRequest request, IApplicationLaunchReply reply)
    {
        await Task.Yield();
        if (_ending)
        {
            await AnswerAsync(reply, (DaemonMessage.Elsewhere("Nix Manager is ending."), DaemonProtocol.RunHere)).ConfigureAwait(false);
            return;
        }
        if (!Matches(request.Identity))
        {
            await AnswerAsync(reply, (DaemonMessage.Elsewhere(ElsewhereText), DaemonProtocol.RunHere)).ConfigureAwait(false);
            return;
        }
        if (request.Request is not { } operationRequest)
        {
            await AnswerAsync(reply, (DaemonMessage.Note("The request names no operation."), 2)).ConfigureAwait(false);
            return;
        }
        var client = new LocalOperationClient(_service);
        Task<Operation> run;
        lock (_startGate)
        {
            // A second operation is refused as a second process would be: with the operation lock's message. The
            // client starts the operation before its first await, so the check and the start are one step here.
            run = _service.Busy
                ? Task.FromException<Operation>(OperationStore.Busy())
                : client.RunAsync(operationRequest, (e, _) => SendAsync(reply, e), reply.Cancelled);
        }
        Operation result;
        try
        {
            result = await run.ConfigureAwait(false);
        }
        catch (ManagerException error)
        {
            await AnswerAsync(reply, (DaemonMessage.Error(error), 1)).ConfigureAwait(false);
            return;
        }
        await reply.WriteAsync(DaemonProtocol.Serialize(DaemonMessage.Result(result))).ConfigureAwait(false);
        await reply.CompleteAsync(result.Status switch
        {
            OperationStatuses.Succeeded => 0,
            OperationStatuses.Cancelled => 130,
            _ => 1,
        }).ConfigureAwait(false);
    }

    // A message is at most 1 MiB; output this large is split into consecutive chunks.
    private const int Chunk = 128 * 1024;

    private async ValueTask SendAsync(IApplicationLaunchReply reply, ManagerEvent e)
    {
        if (e is OperationChanged { Operation.Id: var id }) _forwarded.TryAdd(id, 0);
        if (e is OperationOutput output && output.Text.Length > Chunk)
        {
            var offset = output.Offset;
            for (var start = 0; start < output.Text.Length; start += Chunk)
            {
                var text = output.Text.Substring(start, Math.Min(Chunk, output.Text.Length - start));
                await reply.WriteAsync(DaemonProtocol.Serialize(DaemonMessage.Of(output with { Text = text, Offset = offset }))).ConfigureAwait(false);
                offset += Encoding.UTF8.GetByteCount(text);
            }
            return;
        }
        await reply.WriteAsync(DaemonProtocol.Serialize(DaemonMessage.Of(e))).ConfigureAwait(false);
    }

    /// <summary>
    /// The daemon's loop, on the Desktop host's event loop until Quit, <paramref name="shutdown"/>, or, in
    /// <see cref="DaemonMode.Ui"/>, until the Window is closed and nothing runs. Returns the exit code.
    /// </summary>
    public async Task<int> RunAsync(IDashboardWindows windows, CancellationToken shutdown)
    {
        _windows = windows;
        using var subscription = _service.Subscribe(e =>
        {
            if (e is not OperationChanged { Operation: var operation }) return;
            _inbox.Writer.TryWrite(operation.Status == OperationStatuses.Running ? new Changed() : new Ended(operation));
        });
        using var stop = shutdown.Register(() => _inbox.Writer.TryComplete());
        using var timers = new CancellationTokenSource();
        var tray = _settings.Mode == DaemonMode.Tray;
        // A language chosen in Settings reaches the menu and the tooltip at once.
        using var preferences = tray ? WatchPreferences() : null;
        _notified = _service.History().FirstOrDefault()?.Id ?? "";
        try
        {
            if (tray)
            {
                await RefreshIconAsync();
                _ = ScheduleChecksAsync(timers.Token);
            }
            // Apps of an update applied on next boot follow once the computer runs it.
            try { await _service.ResumeAfterBootAsync(); }
            catch (Exception error) when (error is ManagerException or IOException) { _log.WriteLine($"Resuming after boot failed: {error.Message}"); }
            Refresh();
            if (!tray)
            {
                await ShowAsync(new Show(null));
                if (!windows.IsOpen) return 1;
            }
            await foreach (var item in _inbox.Reader.ReadAllAsync(CancellationToken.None))
            {
                if (item is DesktopStatusIconActivation { MenuItemId: TrayCommands.Quit }) break;
                await HandleAsync(item);
                if (!tray && !windows.IsOpen && !_service.Busy && _serving.IsEmpty) break;
            }
            return 0;
        }
        finally
        {
            _ending = true;
            await timers.CancelAsync();
            await EndAsync();
        }
    }

    /// <summary>Posts <c>Changed</c> when <c>preferences.json</c> is written, which the dashboard does by renaming a new file over it.</summary>
    private FileSystemWatcher? WatchPreferences()
    {
        try
        {
            OperationStore.CreatePrivateDirectory(Config.StateDir);
            var name = Path.GetFileName(DashboardPreferences.PathOf(Config));
            var watcher = new FileSystemWatcher(Config.StateDir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            void Post(string? changed)
            {
                if (changed == name) _inbox.Writer.TryWrite(new Changed());
            }
            watcher.Changed += (_, e) => Post(e.Name);
            watcher.Created += (_, e) => Post(e.Name);
            watcher.Renamed += (_, e) => Post(e.Name);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.WriteLine($"Not following the language setting: {error.Message}");
            return null;
        }
    }

    private async Task HandleAsync(object item)
    {
        switch (item)
        {
            case Show show:
                await ShowAsync(show);
                break;
            case DesktopStatusIconActivation activation:
                await ActivateAsync(activation);
                break;
            case DesktopNotificationActivation activation:
                await NotificationAsync(activation);
                break;
            case WindowClosed:
                _log.WriteLine(_service.Busy ? "The dashboard closed; the operation keeps running." : "The dashboard closed.");
                break;
            case Changed:
                await RefreshIconAsync();
                break;
            case Ended ended:
                await RefreshIconAsync();
                Refresh();
                await NotifyEndedAsync(ended.Operation);
                break;
            case CheckDue:
                _ = CheckAsync();
                break;
        }
    }

    private async Task ShowAsync(Show show)
    {
        try
        {
            var opened = await _windows!.ShowAsync(show.Context, () => _inbox.Writer.TryWrite(new WindowClosed()));
            _log.WriteLine(opened ? "Opened the dashboard." : "Brought the dashboard forward.");
            if (opened) Refresh();
            if (show.Reply is { } reply) Track(reply.CompleteAsync(0).AsTask());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.WriteLine($"The dashboard could not open: {error.Message}");
            if (show.Reply is { } reply) Track(AnswerAsync(reply, (DaemonMessage.Note($"The dashboard could not open: {error.Message}"), 1)));
            // A click on the icon or a notification has no terminal to say why; the Bun tray notified it (invoke in
            // src/tray.ts).
            else await NotifyAsync(NeedsAttention(error.Message));
        }
    }

    private async Task ActivateAsync(DesktopStatusIconActivation activation)
    {
        switch (activation)
        {
            case { Kind: DesktopStatusIconActivationKind.Primary } or { MenuItemId: TrayCommands.Open }:
                await ShowAsync(new Show(activation.PlatformContext));
                break;
            case { Kind: DesktopStatusIconActivationKind.Secondary } or { MenuItemId: TrayCommands.Check }:
                _ = CheckAsync();
                break;
            case { MenuItemId: TrayCommands.Prepare }:
                // Nothing is activated from the tray; the dashboard shows the resulting package changes for confirmation.
                Start(Request.Create() with { Action = Actions.ReviewBuild, Host = Config.HostKey, Update = true });
                await ShowAsync(new Show(activation.PlatformContext));
                break;
        }
    }

    private async Task NotificationAsync(DesktopNotificationActivation activation)
    {
        if (activation.ActionId == NotificationActions.Default)
        {
            await ShowAsync(new Show(activation.PlatformContext));
            return;
        }
        if (activation.ActionId is NotificationActions.ApplyNow or NotificationActions.ApplyOnBoot
            && _noticeReviews.TryGetValue(activation.NotificationId, out var review))
            // The result is notified like any other operation's.
            Start(Request.Create() with
            {
                Action = Actions.ReviewApply,
                Host = review.Host,
                Scope = review.Scope,
                ReviewId = review.Id,
                Activation = activation.ActionId == NotificationActions.ApplyOnBoot ? "boot" : "switch",
            });
    }

    private void Start(Request request)
    {
        try { _service.Start(request); }
        catch (ManagerException error) { _ = NotifyAsync(NeedsAttention(error.Message)); }
    }

    /// <summary>Observes the computer again: at start, when the dashboard opens and after each operation.</summary>
    private void Refresh() =>
        _ = _observer.RefreshAsync().ContinueWith(task => _log.WriteLine($"Observing the system failed: {task.Exception!.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task RefreshIconAsync()
    {
        if (_settings.Mode != DaemonMode.Tray) return;
        var report = _service.Updates();
        var text = Text;
        var state = TrayPresentation.Icon(_service.Active, report, text);
        var key = TrayPresentation.Key(state);
        if (key != _iconKey || !_iconShown)
        {
            _iconKey = key;
            var shown = await _icon.ShowAsync(state);
            if (shown is not PlatformResult<PlatformUnit>.Success && !_iconShown)
                _log.WriteLine("No status area shows the icon yet; it appears once one starts.");
            _iconShown = true;
        }
        if (TrayPresentation.Updates(report, ref _announced, text) is { } updates) await NotifyAsync(updates);
    }

    /// <summary>
    /// Says how an operation ended when no Window shows it and no command line asked for it (D025, W300-031): the
    /// dashboard and the terminal show their own.
    /// </summary>
    private async Task NotifyEndedAsync(Operation operation)
    {
        if (operation.Id == _notified) return;
        _notified = operation.Id;
        if (_forwarded.TryRemove(operation.Id, out _) || _windows?.IsOpen == true) return;
        BuildReview? Review(string id)
        {
            try { return ReviewStore.Get(Config, id); }
            catch (ManagerException) { return null; }
        }
        if (TrayPresentation.Finished(operation, Config, Review, _freeSpace, Text) is not { } notice) return;
        if (notice.Review is { } review) _noticeReviews[notice.Notification.Id] = review;
        else _noticeReviews.TryRemove(notice.Notification.Id, out _);
        await NotifyAsync(notice.Notification);
    }

    private DesktopNotification NeedsAttention(string error)
    {
        var text = Text;
        return new DesktopNotification(TrayPresentation.OperationNotice, text.Messages.tray_needs_attention, text.Stored(error));
    }

    private async Task NotifyAsync(DesktopNotification notification)
    {
        try
        {
            if (await _notifications.ShowAsync(notification) is not PlatformResult<PlatformUnit>.Success and var result)
                _log.WriteLine($"Desktop notification unavailable ({result}): {notification.Title}");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { _log.WriteLine($"Desktop notification failed: {error.Message}"); }
    }

    private async Task ScheduleChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_settings.FirstCheck, _clock, cancellationToken).ConfigureAwait(false);
            _inbox.Writer.TryWrite(new CheckDue());
            using var timer = new PeriodicTimer(_settings.CheckInterval, _clock);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) _inbox.Writer.TryWrite(new CheckDue());
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Checks for updates, then prepares the input updates it found or, when that does not start, cleans up once a
    /// week, as the preferences allow (<c>check</c> in <c>src/tray.ts</c>).
    /// </summary>
    private async Task CheckAsync()
    {
        if (_service.Busy || _ending) return;
        try
        {
            var run = _service.Start(Request.Create() with { Action = Actions.Check, Host = Config.HostKey });
            await run.Completion.ConfigureAwait(false);
            if (_ending) return;
            var prepared = _background.PrepareInBackground();
            if (prepared.Operation is not null) return;
            _log.WriteLine($"Updates are not prepared: {prepared.Reason}");
            var cleaned = _background.CleanUpInBackground();
            if (cleaned.Operation is null) _log.WriteLine($"Not cleaning up: {cleaned.Reason}");
        }
        catch (ManagerException error) when (_service.Busy) { _log.WriteLine($"Not checking now: {error.Message}"); }
        catch (ManagerException error)
        {
            await NotifyAsync(NeedsAttention(error.Message)).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels a running operation, lets command lines receive its end, and closes the Window and the icon.</summary>
    private async Task EndAsync()
    {
        if (_service.Busy)
        {
            _log.WriteLine("Nix Manager is ending; cancelling the running operation.");
            _service.Cancel();
            await _service.WaitAsync();
        }
        var serving = _serving.Keys.ToArray();
        if (serving.Length > 0)
            await Task.WhenAny(Task.WhenAll(serving), Task.Delay(TimeSpan.FromSeconds(10), _clock));
        if (_windows is not null) await _windows.CloseAsync();
        if (_settings.Mode == DaemonMode.Tray)
        {
            try { await _icon.HideAsync(); }
            catch (Exception error) when (error is not OutOfMemoryException) { _log.WriteLine($"Hiding the icon failed: {error.Message}"); }
        }
    }
}
