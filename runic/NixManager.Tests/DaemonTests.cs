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
            if (command.Label.StartsWith("Build", StringComparison.Ordinal)) await _gate.Task.WaitAsync(token);
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

    private DaemonCore Core(DaemonMode mode = DaemonMode.Tray) =>
        new(_service, new BackgroundTasks(_service), new Observer(Config, _ => Task.FromResult<SystemState>(null!)), _icon, _notifications,
            new DaemonSettings { Mode = mode, FirstCheck = TimeSpan.FromHours(1) }, log: _log, freeSpace: () => null);

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

        // The icon shows the menu of the Bun tray, with commands that start work disabled while one runs.
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

        var busy = TrayPresentation.Icon(new Operation { Id = "1", Flake = "", Request = Build, Status = "running", StartedAt = "", Step = "Build home configuration" }, null);
        await Assert.That(busy.Tooltip).IsEqualTo("Build home configuration");
        await Assert.That(busy.Menu.OfType<DesktopMenuCommand>().Where(item => !item.Enabled).Select(item => item.Id)).IsEquivalentTo([TrayCommands.Check, TrayCommands.Prepare]);
        await Assert.That(TrayPresentation.Icon(null, new UpdateReport { CheckedAt = "", Errors = ["x"] }).Tooltip).IsEqualTo("Some update checks failed");

        var announced = "";
        await Assert.That(TrayPresentation.Updates(report, ref announced)?.Body).IsEqualTo("nixpkgs and home-manager");
        await Assert.That(TrayPresentation.Updates(report, ref announced)).IsNull();
        await Assert.That(TrayPresentation.Updates(new UpdateReport { CheckedAt = "" }, ref announced)).IsNull();
        await Assert.That(announced).IsEqualTo("");
    }

    [Test]
    public async Task Notices_follow_the_Bun_tray()
    {
        Operation Ended(string action, string status, string? error = null, bool dryRun = false) => new()
        {
            Id = "1", Flake = _flake, Status = status, StartedAt = "", Error = error,
            Request = Request.Create() with { Action = action, Host = "test", DryRun = dryRun, Scope = Scopes.Home },
        };
        TrayNotice? Notice(Operation operation) => TrayPresentation.Finished(operation, Config, _ => null, () => null);

        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Failed, "Build failed"))!.Notification.Body).IsEqualTo("Build failed");
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
        // Step labels and errors are stored in English and translated where they are shown, values included.
        var busy = TrayPresentation.Icon(new Operation { Id = "1", Flake = "", Request = Build, Status = "running", StartedAt = "", Step = "Build home configuration" }, null, german);
        await Assert.That(busy.Tooltip).IsEqualTo("Benutzerkonfiguration bauen");
        await Assert.That(german.Stored("Building firefox failed")).IsEqualTo("firefox konnte nicht gebaut werden");
        await Assert.That(german.Stored("Build home configuration failed")).IsEqualTo("Fehlgeschlagen: Benutzerkonfiguration bauen");
        await Assert.That(german.Stored("Text nobody translated")).IsEqualTo("Text nobody translated");
        await Assert.That(TrayText.English.Stored("Building firefox failed")).IsEqualTo("Building firefox failed");
        var announced = "";
        await Assert.That(TrayPresentation.Updates(report, ref announced, german)?.Body).IsEqualTo("nixpkgs und home-manager");

        Operation Ended(string action, string status, string? error = null) => new()
        {
            Id = "1", Flake = _flake, Status = status, StartedAt = "", Error = error,
            Request = Request.Create() with { Action = action, Host = "test", Scope = Scopes.Home },
        };
        TrayNotice? Notice(Operation operation) => TrayPresentation.Finished(operation, Config, _ => null, () => null, german);
        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Succeeded))!.Notification.Body).IsEqualTo("Bauen und anwenden für test abgeschlossen.");
        await Assert.That(Notice(Ended(Actions.Switch, OperationStatuses.Failed, "Building firefox failed"))!.Notification)
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
    [Arguments("en")]
    [Arguments("de")]
    public async Task The_daemon_finds_every_stored_text_in_the_generated_catalogue(string locale)
    {
        // The importer's base-text index names a message of the catalogue for each English text it stored.
        var index = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(TranslationCoverageTests.Root, "Frontend", "translations", "stored", "base-text.json")))!;
        var snapshot = NixManager.Dashboard.DashboardTextCatalog.CreateManager(locale).Current;
        await Assert.That(index).IsNotEmpty();
        foreach (var (text, key) in index) await Assert.That(snapshot.TryGetKey(key, out _)).IsTrue().Because($"{key} ({text})");
        await Assert.That(TrayText.English.Stored("Remove user {name}".Replace("{name}", "ada"))).IsEqualTo("Remove user ada");
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
