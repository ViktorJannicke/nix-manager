using System.Collections.Immutable;
using NixManager.Domain;
using NixManager.Operations;
using NixManager.Reviews;
using Runic.Platform;

namespace NixManager.Daemon;

/// <summary>The menu commands of the status icon.</summary>
public static class TrayCommands
{
    public const string Open = "open", Check = "check", Prepare = "prepare", Quit = "quit";
}

/// <summary>The buttons of a notification.</summary>
public static class NotificationActions
{
    /// <summary>A click on the notification itself.</summary>
    public const string Default = "default";
    public const string ApplyNow = "apply-now", ApplyOnBoot = "apply-boot";
}

/// <summary>A notification and, for its buttons, the review they apply.</summary>
public sealed record TrayNotice(DesktopNotification Notification, BuildReview? Review = null);

/// <summary>
/// What the status icon and the notifications say, in the language of <see cref="TrayText"/>, English unless the
/// daemon passes the dashboard's.
/// </summary>
public static class TrayPresentation
{
    public const string Title = "Nix Manager";
    public const string IconName = "system-software-update";
    public const string AttentionIconName = "software-update-available";
    /// <summary>The notification that operations replace.</summary>
    public const string OperationNotice = "operation";
    /// <summary>The notification that update reports replace.</summary>
    public const string UpdatesNotice = "updates";

    /// <summary>The icon, its tooltip and menu for the running operation and the last update report.</summary>
    public static DesktopStatusIconState Icon(Operation? active, UpdateReport? report, TrayText? text = null)
    {
        text ??= TrayText.English;
        var m = text.Messages;
        var count = report?.Items.Count ?? 0;
        var busy = active is not null;
        var tooltip = active is not null ? text.Show(active.Step)
            : report?.Errors.Count > 0 ? m.tray_checks_failed
            : count > 0 ? m.tray_updates_available(count)
            : m.tray_no_updates;
        return new DesktopStatusIconState(new DesktopIcon { ThemeName = IconName }, Title)
        {
            Status = count > 0 ? DesktopStatusIconStatus.NeedsAttention : DesktopStatusIconStatus.Active,
            AttentionIcon = new DesktopIcon { ThemeName = AttentionIconName },
            Tooltip = tooltip,
            Menu =
            [
                new DesktopMenuCommand(TrayCommands.Open, m.tray_open),
                // Nothing is activated from the tray: preparing builds a review that the dashboard shows for confirmation.
                new DesktopMenuCommand(TrayCommands.Check, m.tray_check) { Enabled = !busy },
                new DesktopMenuCommand(TrayCommands.Prepare, m.tray_prepare) { Enabled = !busy },
                new DesktopMenuSeparator(),
                new DesktopMenuCommand(TrayCommands.Quit, m.tray_quit),
            ],
        };
    }

    /// <summary>A key that changes whenever <see cref="Icon"/> would show something else, also in another language.</summary>
    public static string Key(DesktopStatusIconState state) =>
        $"{state.Status}|{state.Tooltip}|{string.Join(",", state.Menu.OfType<DesktopMenuCommand>().Select(item => $"{item.Id}:{item.Enabled}:{item.Label}"))}";

    /// <summary>The update report's notification, when it lists updates not announced before (<paramref name="announced"/>).</summary>
    public static DesktopNotification? Updates(UpdateReport? report, ref string announced, TrayText? text = null)
    {
        text ??= TrayText.English;
        var count = report?.Items.Count ?? 0;
        if (count > 0 && report is not null)
        {
            var signature = string.Join("\n", report.Items.Select(item => $"{item.Kind}:{item.Name}:{item.Current}:{item.Latest}"));
            if (signature == announced) return null;
            announced = signature;
            return new DesktopNotification(UpdatesNotice, text.Messages.tray_updates_available(count),
                text.List([.. report.Items.Select(item => item.Name)]));
        }
        if (report is not null && report.Errors.Count == 0) announced = "";
        return null;
    }

    /// <summary>
    /// What to say about an operation that ended (<c>refresh</c>): a failure, prepared updates with buttons to apply them,
    /// the space an automatic cleanup freed, a build ready for review, or a finished action. Nothing for checks, dry
    /// runs and cancelled operations.
    /// </summary>
    /// <param name="operation">The final record.</param>
    /// <param name="config">The configuration the daemon runs.</param>
    /// <param name="review">Reads a stored review; null when it is gone.</param>
    /// <param name="freeNow">Free bytes in the store now, for an automatic cleanup.</param>
    /// <param name="text">The language to say it in; English when null.</param>
    public static TrayNotice? Finished(Operation operation, Config config, Func<string, BuildReview?> review, Func<long?> freeNow, TrayText? text = null)
    {
        text ??= TrayText.English;
        var m = text.Messages;
        if (operation.Request.DryRun || operation.Status == OperationStatuses.Running) return null;
        DesktopNotification Notice(string title, string body) => new(OperationNotice, title, body.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal));
        var action = operation.Request.Action;
        if (operation.Status == OperationStatuses.Failed)
        {
            var error = operation.Error is { English.Length: > 0 } failure ? text.Show(failure) : m.tray_operation_failed;
            error = error[..Math.Min(error.Length, 300)];
            return new TrayNotice(Notice(m.tray_needs_attention, error));
        }
        if (operation.Status != OperationStatuses.Succeeded) return null;
        if (action == Actions.ReviewBuild && operation.Request.ReviewId is { } reviewId && reviewId == BackgroundTasks.LastPrepared(config)?.ReviewId)
        {
            if (review(reviewId) is not { } prepared) return null;
            ImmutableArray<DesktopNotificationAction> actions = prepared.System is not null
                ? [new(NotificationActions.ApplyNow, m.tray_apply_now), new(NotificationActions.ApplyOnBoot, m.tray_apply_on_boot)]
                : [new(NotificationActions.ApplyNow, m.tray_apply_now)];
            return new TrayNotice(Notice(m.tray_updates_ready, m.tray_nothing_changes(ReviewSummary.Of(prepared, text))) with { Actions = actions }, prepared);
        }
        if (action == Actions.Gc && BackgroundTasks.LastCleanup(config) is { } cleanup && cleanup.OperationId == operation.Id)
        {
            var after = freeNow();
            var freed = cleanup.Free is { } before && after is { } now ? now - before : 0;
            var first = freed > 0 ? m.tray_freed(text.Bytes(freed)) : m.tray_older_removed;
            return new TrayNotice(Notice(m.tray_space_freed, $"{first} {m.tray_newest_kept(BackgroundTasks.KeptAutomatically)}"));
        }
        if (action == Actions.ReviewBuild)
            return new TrayNotice(Notice(m.tray_ready_to_review, m.tray_build_finished));
        if (action == Actions.Check) return null;
        var host = operation.Request.Host is { Length: > 0 } named ? named : config.HostKey;
        return new TrayNotice(Notice(m.tray_finished, m.tray_completed_for(text.Show(Actions.Label(action)), host)));
    }
}
