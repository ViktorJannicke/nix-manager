using System.Collections.Immutable;
using System.Threading.Channels;
using NixManager.Daemon;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using Runic.Platform;

namespace NixManager.Tests;

/// <summary>A launch's reply as the running instance writes it.</summary>
internal sealed class RecordingReply : IApplicationLaunchReply
{
    private readonly CancellationTokenSource _cancelRequested = new();
    private readonly CancellationTokenSource _disconnected = new();
    private readonly CancellationTokenSource _cancelled = new();
    public List<DaemonMessage> Messages { get; } = [];
    public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Cancelled => _cancelled.Token;
    public CancellationToken CancelRequested => _cancelRequested.Token;
    public CancellationToken Disconnected => _disconnected.Token;

    /// <summary>The command line asks to cancel, as on Ctrl+C.</summary>
    public void Cancel()
    {
        _cancelRequested.Cancel();
        _cancelled.Cancel();
    }

    /// <summary>The command line went away, such as with a dropped SSH session.</summary>
    public void Disconnect()
    {
        _disconnected.Cancel();
        _cancelled.Cancel();
    }

    public ValueTask<bool> WriteAsync(string message, CancellationToken cancellationToken = default)
    {
        lock (Messages) Messages.Add(DaemonProtocol.ParseMessage(message)!);
        return ValueTask.FromResult(true);
    }

    public ValueTask CompleteAsync(int exitCode, CancellationToken cancellationToken = default)
    {
        Exit.TrySetResult(exitCode);
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingNotifications : IDesktopNotifications
{
    public Channel<DesktopNotification> Shown { get; } = Channel.CreateUnbounded<DesktopNotification>();
    public event EventHandler<DesktopNotificationActivation>? Activated;
    public void Activate(DesktopNotificationActivation activation) => Activated?.Invoke(this, activation);
    public ValueTask<PlatformResult<PlatformUnit>> RequestPermissionAsync(CancellationToken cancellationToken = default) => Ok();
    public ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default)
    {
        Shown.Writer.TryWrite(notification);
        return Ok();
    }
    public ValueTask<PlatformResult<PlatformUnit>> RemoveAsync(string id, CancellationToken cancellationToken = default) => Ok();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static ValueTask<PlatformResult<PlatformUnit>> Ok() => ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(default(PlatformUnit)));
}

internal sealed class RecordingIcon : IDesktopStatusIcon
{
    public List<DesktopStatusIconState> Shown { get; } = [];
    public event EventHandler<DesktopStatusIconActivation>? Activated;
    public void Activate(DesktopStatusIconActivation activation) => Activated?.Invoke(this, activation);
    public ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopStatusIconState state, CancellationToken cancellationToken = default)
    {
        lock (Shown) Shown.Add(state);
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(default(PlatformUnit)));
    }
    public ValueTask<PlatformResult<PlatformUnit>> HideAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(default(PlatformUnit)));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A dashboard that opens and closes when the test says so.</summary>
internal sealed class FakeWindows : IDashboardWindows
{
    private Action? _closed;
    public int Opened { get; private set; }
    public int Raised { get; private set; }
    public bool IsOpen { get; private set; }

    /// <summary>Why the dashboard cannot open, or null when it can.</summary>
    public string? Fails { get; set; }

    public Task<bool> ShowAsync(DesktopActivationContext? context, Action closed)
    {
        if (Fails is { } why) throw new InvalidOperationException(why);
        if (IsOpen)
        {
            Raised++;
            return Task.FromResult(false);
        }
        (IsOpen, _closed) = (true, closed);
        Opened++;
        return Task.FromResult(true);
    }

    public void Close()
    {
        IsOpen = false;
        _closed?.Invoke();
    }

    public ValueTask CloseAsync()
    {
        if (IsOpen) Close();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The daemon's core without a desktop: forwarded operations and their replies, the dashboard's launches, the status
/// icon and the notifications; and the command line's routing decisions. The processes themselves are tested in
/// test/test-daemon.py.
/// </summary>
public sealed class DaemonTests : IDisposable
{
    private readonly string _flake = TestConfig.TemporaryFlake();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly OperationService _service;
    private readonly RecordingNotifications _notifications = new();
    private readonly RecordingIcon _icon = new();
    private readonly FakeWindows _windows = new();
    private readonly StringWriter _log = new();

    public DaemonTests()
    {
        var runner = new FakeRunner(async (command, output, token) =>
        {
            output(new TextOutput("stdout", $"{command.Label}\n"));
            if (command.Label.English.StartsWith("Build", StringComparison.Ordinal)) await _gate.Task.WaitAsync(token);
            return new CommandResult(0, "", "");
        });
        _service = new OperationService(Config, runner, new FakeUpdates(new UpdateReport { CheckedAt = "2026-10-15T00:00:00.000Z" }));
    }

    private Config Config => TestConfig.Create(_flake);

    public void Dispose()
    {
        _gate.TrySetResult();
        _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Directory.Delete(_flake, recursive: true);
    }

    private DaemonCore Core(DaemonMode mode = DaemonMode.Tray, Func<bool>? outdated = null, TrayRestart? restart = null) =>
        new(_service, new BackgroundTasks(_service), new Observer(Config, _ => Task.FromResult<SystemState>(null!)), _icon, _notifications,
            new DaemonSettings { Mode = mode, FirstCheck = TimeSpan.FromHours(1), Outdated = outdated }, log: _log, freeSpace: () => null,
            restart: restart);

    private static ApplicationLaunch Launch(DaemonRequest request, IApplicationLaunchReply? reply) =>
        DaemonProtocol.Launch(request) with { Reply = reply };

    private static Request Build => Request.Create() with { Action = Actions.Build, Scope = Scopes.Home, Host = "test" };

    [Test]
    public async Task A_forwarded_operation_streams_its_events_and_final_record_and_exits_with_its_status()
    {
        var core = Core();
        var reply = new RecordingReply();
        _gate.SetResult();

        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config), Build), reply));

        await Assert.That(await reply.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
        var kinds = reply.Messages.Select(message => message.Kind).ToList();
        await Assert.That(kinds[^1]).IsEqualTo("result");
        await Assert.That(kinds.Take(kinds.Count - 1).All(kind => kind == "event")).IsTrue();
        await Assert.That(reply.Messages.Any(message => message.Event is OperationOutput { Text: "Build home configuration\n" })).IsTrue();
        await Assert.That(reply.Messages[^1].Operation!.Status).IsEqualTo(OperationStatuses.Succeeded);
    }

    [Test]
    public async Task A_cancel_request_cancels_the_operation_and_still_sends_its_end()
    {
        var core = Core();
        var reply = new RecordingReply();
        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config), Build), reply));
        while (!_service.Busy) await Task.Delay(10);

        reply.Cancel();

        await Assert.That(await reply.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(130);
        await Assert.That(reply.Messages[^1].Operation!.Status).IsEqualTo(OperationStatuses.Cancelled);
    }

    [Test]
    public async Task An_operation_outlives_a_command_line_that_went_away()
    {
        var core = Core();
        var reply = new RecordingReply();
        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config), Build), reply));
        while (!_service.Busy) await Task.Delay(10);

        reply.Disconnect();
        await Task.Delay(100);
        await Assert.That(_service.Busy).IsTrue();
        _gate.SetResult();

        await Assert.That(await reply.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
        await Assert.That(reply.Messages[^1].Operation!.Status).IsEqualTo(OperationStatuses.Succeeded);
    }

    [Test]
    public async Task A_second_operation_is_refused_with_the_lock_message_and_another_checkout_runs_elsewhere()
    {
        var core = Core();
        var first = new RecordingReply();
        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config), Build), first));
        while (!_service.Busy) await Task.Delay(10);

        var second = new RecordingReply();
        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config), Build), second));
        await Assert.That(await second.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(1);
        await Assert.That(second.Messages.Single().Message).IsEqualTo("Another nix-manager operation is running. Wait for it to finish.");

        var other = new RecordingReply();
        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config) with { HostKey = "laptop" }, Build), other));
        await Assert.That(await other.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(DaemonProtocol.RunHere);
        await Assert.That(other.Messages.Single().Kind).IsEqualTo("elsewhere");

        _gate.SetResult();
        await Assert.That(await first.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
    }

    [Test]
    public async Task The_dashboard_opens_once_comes_forward_on_later_launches_and_ends_the_ui_process_when_closed()
    {
        var core = Core(DaemonMode.Ui);
        using var shutdown = new CancellationTokenSource();
        var running = core.RunAsync(_windows, shutdown.Token);
        while (_windows.Opened == 0) await Task.Delay(10);

        var reply = new RecordingReply();
        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Ui), reply));
        await Assert.That(await reply.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
        await Assert.That((_windows.Opened, _windows.Raised)).IsEqualTo((1, 1));

        // A tray started meanwhile waits for this process to end.
        var tray = new RecordingReply();
        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Daemon), tray));
        await Assert.That(await tray.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(DaemonProtocol.RunHere);

        _windows.Close();
        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
    }

    [Test]
    public async Task Closing_the_dashboard_of_the_ui_process_waits_for_the_running_operation()
    {
        var core = Core(DaemonMode.Ui);
        using var shutdown = new CancellationTokenSource();
        var running = core.RunAsync(_windows, shutdown.Token);
        while (_windows.Opened == 0) await Task.Delay(10);
        _service.Start(Build);

        _windows.Close();
        await Task.Delay(200);
        await Assert.That(running.IsCompleted).IsFalse();

        _gate.SetResult();
        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
        await Assert.That(_service.History()[0].Status).IsEqualTo(OperationStatuses.Succeeded);
    }

    [Test]
    public async Task An_outdated_tray_keeps_running_the_operation_and_the_dashboard_then_ends_for_the_new_one()
    {
        var outdated = false;
        var core = Core(outdated: () => outdated);
        using var shutdown = new CancellationTokenSource();
        var running = core.RunAsync(_windows, shutdown.Token);
        _icon.Activate(new DesktopStatusIconActivation(DesktopStatusIconActivationKind.MenuItem, TrayCommands.Open));
        while (_windows.Opened == 0) await Task.Delay(10);

        // The operation installs a newer tray service, as a system switch does; its end leaves the dashboard open.
        var operation = _service.Start(Build);
        outdated = true;
        _gate.SetResult();
        await operation.Completion;
        await Task.Delay(200);
        await Assert.That(running.IsCompleted).IsFalse();
        await Assert.That(_windows.IsOpen).IsTrue();

        _windows.Close();
        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(DaemonCore.Replaced);
        await Assert.That(_service.History()[0].Status).IsEqualTo(OperationStatuses.Succeeded);
    }

    [Test]
    public async Task A_current_tray_keeps_running_after_an_operation()
    {
        var core = Core(outdated: () => false);
        using var shutdown = new CancellationTokenSource();
        var running = core.RunAsync(_windows, shutdown.Token);
        _gate.SetResult();
        await _service.Start(Build).Completion;
        await Task.Delay(200);
        await Assert.That(running.IsCompleted).IsFalse();
        await shutdown.CancelAsync();
        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
    }

    [Test]
    public async Task Restart_now_ends_the_tray_with_the_page_recorded_and_the_next_tray_reopens_it_once()
    {
        var restart = new TrayRestart(Config, () => true);
        using var shutdown = new CancellationTokenSource();
        var running = Core(outdated: () => true, restart: restart).RunAsync(_windows, shutdown.Token);
        _icon.Activate(new DesktopStatusIconActivation(DesktopStatusIconActivationKind.MenuItem, TrayCommands.Open));
        while (_windows.Opened == 0) await Task.Delay(10);

        restart.Request("software");
        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(DaemonCore.Replaced);
        await Assert.That(_windows.IsOpen).IsFalse();
        await Assert.That(File.GetUnixFileMode(restart.Record)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // The tray systemd starts next opens the dashboard by itself, and only its first Window gets the page.
        var next = new TrayRestart(Config, () => false);
        var windows = new FakeWindows();
        using var stop = new CancellationTokenSource();
        var after = Core(outdated: () => false, restart: next).RunAsync(windows, stop.Token);
        while (windows.Opened == 0) await Task.Delay(10);
        await Assert.That(File.Exists(next.Record)).IsFalse();
        await Assert.That(next.TakePage()).IsEqualTo("software");
        await Assert.That(next.TakePage()).IsNull();
        await stop.CancelAsync();
        await Assert.That(await after.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
    }

    [Test]
    public async Task Restart_now_waits_for_the_running_operation()
    {
        var restart = new TrayRestart(Config, () => true);
        using var shutdown = new CancellationTokenSource();
        var running = Core(outdated: () => true, restart: restart).RunAsync(_windows, shutdown.Token);
        _icon.Activate(new DesktopStatusIconActivation(DesktopStatusIconActivationKind.MenuItem, TrayCommands.Open));
        while (_windows.Opened == 0) await Task.Delay(10);
        var operation = _service.Start(Build);

        // Refused, and its record removed; the operation's end leaves the open dashboard running.
        restart.Request("activity");
        while (File.Exists(restart.Record)) await Task.Delay(10);
        await Assert.That(running.IsCompleted).IsFalse();
        _gate.SetResult();
        await operation.Completion;
        await Task.Delay(200);
        await Assert.That(running.IsCompleted).IsFalse();

        restart.Request("activity");
        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(DaemonCore.Replaced);
        await Assert.That(_service.History()[0].Status).IsEqualTo(OperationStatuses.Succeeded);
    }

    [Test]
    public async Task A_stale_or_unreadable_page_record_is_discarded_without_opening_the_dashboard()
    {
        var restart = new TrayRestart(Config, () => false);
        Directory.CreateDirectory(Config.StateDir);
        var old = Timestamps.Format(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        await File.WriteAllTextAsync(restart.Record, $$"""{"page":"software","at":"{{old}}"}""");
        using var shutdown = new CancellationTokenSource();
        var running = Core(outdated: () => false, restart: restart).RunAsync(_windows, shutdown.Token);
        while (File.Exists(restart.Record)) await Task.Delay(10);
        await Task.Delay(100);
        await Assert.That(_windows.Opened).IsEqualTo(0);
        await Assert.That(restart.TakePage()).IsNull();
        await shutdown.CancelAsync();
        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);

        await File.WriteAllTextAsync(restart.Record, "not json");
        await Assert.That(restart.Resume()).IsNull();
        await Assert.That(File.Exists(restart.Record)).IsFalse();
    }

    [Test]
    public async Task The_tray_is_outdated_once_the_installed_revision_differs_from_its_own()
    {
        var file = Path.Combine(_flake, "tray-revision");
        await Assert.That(DaemonCommand.Outdated(null, file) is null).IsTrue();
        var outdated = DaemonCommand.Outdated("abc", file)!;
        // Without the file the service is no longer installed; systemd stops it.
        await Assert.That(outdated()).IsFalse();
        await File.WriteAllTextAsync(file, "abc");
        await Assert.That(outdated()).IsFalse();
        await File.WriteAllTextAsync(file, "def");
        await Assert.That(outdated()).IsTrue();
    }

    [Test]
    public async Task A_dashboard_that_cannot_open_from_the_icon_is_notified()
    {
        var core = Core();
        using var shutdown = new CancellationTokenSource();
        var running = core.RunAsync(_windows, shutdown.Token);
        _gate.SetResult();
        _windows.Fails = "No display.";
        _icon.Activate(new DesktopStatusIconActivation(DesktopStatusIconActivationKind.MenuItem, TrayCommands.Open));
        var notice = await _notifications.Shown.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(notice.Title).IsEqualTo("Nix Manager needs attention");
        await Assert.That(notice.Body).IsEqualTo("No display.");
        await shutdown.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task An_operation_that_ends_without_a_window_is_notified_unless_a_command_line_asked_for_it()
    {
        var core = Core();
        using var shutdown = new CancellationTokenSource();
        var running = core.RunAsync(_windows, shutdown.Token);
        _gate.SetResult();

        // Started from the tray, nothing open: notified.
        await _service.Start(Build).Completion;
        var notice = await _notifications.Shown.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(notice.Title).IsEqualTo("Nix Manager finished");
        await Assert.That(notice.Body).IsEqualTo("Build configuration completed for test.");

        // Asked for by a command line, whose terminal shows the end: not notified.
        var reply = new RecordingReply();
        core.OnLaunch(Launch(new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config), Build), reply));
        await reply.Exit.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // With the dashboard open, it shows the end itself: not notified.
        await _windows.ShowAsync(null, () => { });
        await _service.Start(Build).Completion;
        await Task.Delay(300);
        await Assert.That(_notifications.Shown.Reader.TryRead(out _)).IsFalse();

        // The icon shows the tray menu, with commands that start work disabled while one runs.
        var menu = _icon.Shown[^1].Menu.OfType<DesktopMenuCommand>().Select(item => item.Label).ToList();
        await Assert.That(menu).IsEquivalentTo(["Open Nix Manager", "Check for updates", "Prepare all updates for review…", "Quit Nix Manager"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await shutdown.CancelAsync();
        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
    }

    [Test]
    public async Task Quit_cancels_the_running_operation_and_ends()
    {
        var core = Core();
        var running = core.RunAsync(_windows, CancellationToken.None);
        var run = _service.Start(Build);
        while (_icon.Shown.Count == 0) await Task.Delay(10);

        _icon.Activate(new DesktopStatusIconActivation(DesktopStatusIconActivationKind.MenuItem, TrayCommands.Quit));

        await Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
        await Assert.That((await run.Completion).Status).IsEqualTo(OperationStatuses.Cancelled);
    }

    [Test]
    public async Task The_icon_says_what_runs_and_how_many_updates_wait()
    {
        var report = new UpdateReport
        {
            CheckedAt = "",
            Items = [new UpdateItem { Name = "nixpkgs", Current = "a", Latest = "b", Kind = "input" }, new UpdateItem { Name = "home-manager", Current = "a", Latest = "b", Kind = "input" }],
        };
        var idle = TrayPresentation.Icon(null, report);
        await Assert.That(idle.Tooltip).IsEqualTo("2 updates available");
        await Assert.That(idle.Status).IsEqualTo(DesktopStatusIconStatus.NeedsAttention);
        await Assert.That(idle.Menu.OfType<DesktopMenuCommand>().All(item => item.Enabled)).IsTrue();

        var busy = TrayPresentation.Icon(new Operation { Id = "1", Flake = "", Request = Build, Status = "running", StartedAt = "", Step = DisplayText.Of("stored_build_home_configuration") }, null);
        await Assert.That(busy.Tooltip).IsEqualTo("Build home configuration");
        await Assert.That(busy.Menu.OfType<DesktopMenuCommand>().Where(item => !item.Enabled).Select(item => item.Id)).IsEquivalentTo([TrayCommands.Check, TrayCommands.Prepare]);
        await Assert.That(TrayPresentation.Icon(null, new UpdateReport { CheckedAt = "", Errors = [DisplayText.Plain("x")] }).Tooltip).IsEqualTo("Some update checks failed");

        var announced = "";
        await Assert.That(TrayPresentation.Updates(report, ref announced)?.Body).IsEqualTo("nixpkgs and home-manager");
        await Assert.That(TrayPresentation.Updates(report, ref announced)).IsNull();
        await Assert.That(TrayPresentation.Updates(new UpdateReport { CheckedAt = "" }, ref announced)).IsNull();
        await Assert.That(announced).IsEqualTo("");
    }

    [Test]
    public async Task Notices_follow_the_tray()
    {
        Operation Ended(string action, string status, DisplayText? error = null, bool dryRun = false) => new()
        {
            Id = "1", Flake = _flake, Status = status, StartedAt = "", Error = error,
            Request = Request.Create() with { Action = action, Host = "test", DryRun = dryRun, Scope = Scopes.Home },
        };
        TrayNotice? Notice(Operation operation) => TrayPresentation.Finished(operation, Config, _ => null, () => null);

        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Failed, DisplayText.Plain("Build failed")))!.Notification.Body).IsEqualTo("Build failed");
        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Failed))!.Notification.Body).IsEqualTo("An operation failed. Open the dashboard for its log.");
        await Assert.That(Notice(Ended(Actions.ReviewBuild, OperationStatuses.Succeeded))!.Notification.Title).IsEqualTo("Changes are ready to review");
        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Succeeded))!.Notification.Body).IsEqualTo("Build and switch completed for test.");
        await Assert.That(Notice(Ended(Actions.Check, OperationStatuses.Succeeded))).IsNull();
        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Cancelled))).IsNull();
        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Succeeded, dryRun: true))).IsNull();
    }

    [Test]
    public async Task Review_summaries_say_what_applying_does()
    {
        var review = new BuildReview
        {
            Id = "r", Scope = Scopes.All, CreatedAt = "", Source = "update",
            Inputs = new ReviewInputs { BaseLockHash = "", Changes = [new LockChange { Name = "nixpkgs" }, new LockChange { Name = "home-manager" }] },
            Home = new ReviewOutput { Path = "/h", Compared = true, Changes = [new PackageChange { Name = "a", Kind = "changed" }, new PackageChange { Name = "b", Kind = "added" }], ClosureSize = 3L << 30, CurrentClosureSize = 1L << 30 },
            System = new ReviewOutput { Path = "/s", Compared = true, Changes = [new PackageChange { Name = "c", Kind = "removed" }], Kernel = new VersionPair { Before = "6.1", After = "6.2" } },
            Downloaded = 5L << 20,
        };
        await Assert.That(ReviewSummary.Of(review)).IsEqualTo(
            "Updates 2 software sources. Updates 1 package, adds 1, and removes 1. It needs about 2.0 GiB more disk space. 5.0 MiB were downloaded to prepare it. Restart afterwards to use the new kernel.");
        await Assert.That(ReviewSummary.Bytes(1536)).IsEqualTo("1.5 KiB");
        await Assert.That(ReviewSummary.Bytes(-200, signed: true)).IsEqualTo("−200 B");
        await Assert.That(ReviewSummary.TimeLeft(600, 60)).IsEqualTo("About 9 minutes left");
        await Assert.That(ReviewSummary.TimeLeft(60, 200)).IsEqualTo("Taking longer than usual");
    }

    [Test]
    public async Task The_icon_and_the_notifications_speak_the_dashboards_language()
    {
        var german = TrayText.For("de");
        var report = new UpdateReport
        {
            CheckedAt = "",
            Items = [new UpdateItem { Name = "nixpkgs", Current = "a", Latest = "b", Kind = "input" }, new UpdateItem { Name = "home-manager", Current = "a", Latest = "b", Kind = "input" }],
        };
        var idle = TrayPresentation.Icon(null, report, german);
        await Assert.That(idle.Tooltip).IsEqualTo("2 Aktualisierungen verfügbar");
        await Assert.That(idle.Menu.OfType<DesktopMenuCommand>().Select(item => item.Label))
            .IsEquivalentTo(["Nix Manager öffnen", "Nach Aktualisierungen suchen", "Alle Aktualisierungen zur Prüfung vorbereiten…", "Nix Manager beenden"]);
        await Assert.That(TrayPresentation.Key(idle)).IsNotEqualTo(TrayPresentation.Key(TrayPresentation.Icon(null, report)));
        // Step labels and errors are text references, shown in the dashboard's language with their values; a message
        // inside, such as the step that failed, is translated too.
        var building = DisplayText.Of("stored_building_failed", ("name", "firefox"));
        var busy = TrayPresentation.Icon(new Operation { Id = "1", Flake = "", Request = Build, Status = "running", StartedAt = "", Step = DisplayText.Of("stored_build_home_configuration") }, null, german);
        await Assert.That(busy.Tooltip).IsEqualTo("Benutzerkonfiguration bauen");
        await Assert.That(german.Show(building)).IsEqualTo("firefox konnte nicht gebaut werden");
        await Assert.That(german.Show(DisplayText.Of("stored_failed", ("label", DisplayText.Of("stored_build_home_configuration")))))
            .IsEqualTo("Fehlgeschlagen: Benutzerkonfiguration bauen");
        await Assert.That(german.Show(DisplayText.Plain("Text nobody translated"))).IsEqualTo("Text nobody translated");
        await Assert.That(TrayText.English.Show(building)).IsEqualTo("Building firefox failed");
        var announced = "";
        await Assert.That(TrayPresentation.Updates(report, ref announced, german)?.Body).IsEqualTo("nixpkgs und home-manager");

        Operation Ended(string action, string status, DisplayText? error = null) => new()
        {
            Id = "1", Flake = _flake, Status = status, StartedAt = "", Error = error,
            Request = Request.Create() with { Action = action, Host = "test", Scope = Scopes.Home },
        };
        TrayNotice? Notice(Operation operation) => TrayPresentation.Finished(operation, Config, _ => null, () => null, german);
        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Succeeded))!.Notification.Body).IsEqualTo("Bauen und anwenden für test abgeschlossen.");
        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Failed, building))!.Notification)
            .Satisfies(notification => notification.Title == "Nix Manager braucht Ihre Aufmerksamkeit" && notification.Body == "firefox konnte nicht gebaut werden", "German failure");
        await Assert.That(Notice(Ended(Actions.ReviewBuild, OperationStatuses.Succeeded))!.Notification.Title).IsEqualTo("Änderungen sind bereit zur Prüfung");

        var review = new BuildReview
        {
            Id = "r", Scope = Scopes.All, CreatedAt = "", Source = "update",
            Inputs = new ReviewInputs { BaseLockHash = "", Changes = [new LockChange { Name = "nixpkgs" }, new LockChange { Name = "home-manager" }] },
            Home = new ReviewOutput { Path = "/h", Compared = true, Changes = [new PackageChange { Name = "a", Kind = "changed" }, new PackageChange { Name = "b", Kind = "added" }], ClosureSize = 3L << 30, CurrentClosureSize = 1L << 30 },
            Downloaded = 5L << 20,
        };
        await Assert.That(ReviewSummary.Of(review, german)).IsEqualTo(
            "Aktualisiert 2 Softwarequellen. Aktualisiert 1 Paket und fügt 1 hinzu. Es wird etwa 2,0 GiB mehr Speicherplatz benötigt. Zur Vorbereitung wurden 5,0 MiB heruntergeladen.");
    }

    [Test]
    public async Task The_tray_shows_a_record_from_before_text_references_in_its_English()
    {
        // A failed apply recorded with English step labels, error and explanation, as the Bun version wrote them.
        var record = File.ReadAllText(Path.Combine(RecordedFixtures.Directory, "recorded-state", "operations",
            "1791817200000-3a9e5c71-2b84-4d6f-a0c3-9e8d7f6b5a41.json"));
        var operation = ManagerJson.Deserialize<Operation>(record)!;
        await Assert.That(operation.Step).IsEqualTo(DisplayText.Plain("Activate system"));
        await Assert.That(operation.Explanation!.Title.Reference).IsNull();
        var german = TrayText.For("de");
        await Assert.That(german.Show(operation.Step)).IsEqualTo("Activate system");
        var running = TrayPresentation.Icon(new Operation
        {
            Id = operation.Id, Flake = operation.Flake, Request = operation.Request, Status = OperationStatuses.Running,
            StartedAt = operation.StartedAt, Step = operation.Step,
        }, null, german);
        await Assert.That(running.Tooltip).IsEqualTo("Activate system");
        var notice = TrayPresentation.Finished(operation, Config, _ => null, () => null, german);
        await Assert.That(notice!.Notification.Body)
            .IsEqualTo("Activate system failed (exit 4). warning: the following units failed: home-manager-user.service");
        // Written again, the English stays as it was read.
        await Assert.That(ManagerJson.Serialize(operation.Step)).IsEqualTo("\"Activate system\"");
    }

    [Test]
    public async Task The_daemon_formats_its_numbers_in_the_dashboards_language()
    {
        await Assert.That(TrayText.For("de").Messages.tray_updates_available(1234)).IsEqualTo("1.234 Aktualisierungen verfügbar");
        await Assert.That(TrayText.English.Messages.tray_updates_available(1234)).IsEqualTo("1,234 updates available");
        await Assert.That(TrayText.English.Messages.tray_updates_available(1)).IsEqualTo("1 update available");
        await Assert.That(TrayText.For("de").Messages.tray_updates_available(1)).IsEqualTo("1 Aktualisierung verfügbar");
    }

    [Test]
    public async Task The_command_line_hands_operations_to_the_daemon_except_privileged_ones_over_SSH()
    {
        var local = new LocalOperationClient(_service);
        Dictionary<string, string> variables = [];
        var client = new RoutingOperationClient(local, name => variables.GetValueOrDefault(name));
        var system = Request.Create() with { Action = Actions.Switch, Scope = Scopes.System, Host = "test" };

        await Assert.That(client.Routes(system)).IsTrue();
        variables["SSH_CONNECTION"] = "10.0.0.2 50000 10.0.0.1 22";
        await Assert.That(client.Routes(system)).IsFalse();
        await Assert.That(client.Routes(Build)).IsTrue();
        variables["NIX_MANAGER_DAEMON"] = "0";
        await Assert.That(client.Routes(Build)).IsFalse();
    }

    [Test]
    public async Task Requests_round_trip_through_their_launch_arguments()
    {
        var request = new DaemonRequest(DaemonRequestKinds.Operation, DaemonIdentity.Of(Config), Build with { Extra = ["--show-trace"] });
        var launch = DaemonProtocol.Launch(request);
        var parsed = DaemonProtocol.Parse(launch)!;
        await Assert.That(parsed.Kind).IsEqualTo(DaemonRequestKinds.Operation);
        await Assert.That(parsed.Identity).IsEqualTo(request.Identity);
        await Assert.That(parsed.Request!.Extra).IsEquivalentTo(["--show-trace"]);
        await Assert.That(DaemonProtocol.Parse(new ApplicationLaunch(ImmutableArray.Create("ui"), "/"))).IsNull();
    }
}
