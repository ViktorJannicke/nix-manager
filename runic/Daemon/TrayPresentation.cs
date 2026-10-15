using System.Collections.Immutable;
using NixManager.Domain;
using NixManager.Operations;
using NixManager.Reviews;
using Runic.Platform;

namespace NixManager.Daemon;

/// <summary>The menu commands of the status icon, as <c>src/tray.ts</c> lists them.</summary>
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
/// What the status icon and the notifications say (<c>refresh</c> and <c>notify</c> in <c>src/tray.ts</c>), in
/// English; the dashboard has the translations.
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

    private static string Plural(int count, string one, string many) =>
        (count == 1 ? one : many).Replace("{count}", count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The icon, its tooltip and menu for the running operation and the last update report.</summary>
    public static DesktopStatusIconState Icon(Operation? active, UpdateReport? report)
    {
        var count = report?.Items.Count ?? 0;
        var busy = active is not null;
        var tooltip = active is not null ? active.Step
            : report?.Errors.Count > 0 ? "Some update checks failed"
            : count > 0 ? Plural(count, "{count} update available", "{count} updates available")
            : "No updates reported";
        return new DesktopStatusIconState(new DesktopIcon { ThemeName = IconName }, Title)
        {
            Status = count > 0 ? DesktopStatusIconStatus.NeedsAttention : DesktopStatusIconStatus.Active,
            AttentionIcon = new DesktopIcon { ThemeName = AttentionIconName },
            Tooltip = tooltip,
            Menu =
            [
                new DesktopMenuCommand(TrayCommands.Open, "Open Nix Manager"),
                // Nothing is activated from the tray: preparing builds a review that the dashboard shows for confirmation.
                new DesktopMenuCommand(TrayCommands.Check, "Check for updates") { Enabled = !busy },
                new DesktopMenuCommand(TrayCommands.Prepare, "Prepare all updates for review…") { Enabled = !busy },
                new DesktopMenuSeparator(),
                new DesktopMenuCommand(TrayCommands.Quit, "Quit Nix Manager"),
            ],
        };
    }

    /// <summary>A key that changes whenever <see cref="Icon"/> would show something else.</summary>
    public static string Key(DesktopStatusIconState state) =>
        $"{state.Status}|{state.Tooltip}|{string.Join(",", state.Menu.OfType<DesktopMenuCommand>().Select(item => $"{item.Id}:{item.Enabled}"))}";

    /// <summary>The update report's notification, when it lists updates not announced before (<paramref name="announced"/>).</summary>
    public static DesktopNotification? Updates(UpdateReport? report, ref string announced)
    {
        var count = report?.Items.Count ?? 0;
        if (count > 0 && report is not null)
        {
            var signature = string.Join("\n", report.Items.Select(item => $"{item.Kind}:{item.Name}:{item.Current}:{item.Latest}"));
            if (signature == announced) return null;
            announced = signature;
            return new DesktopNotification(UpdatesNotice, Plural(count, "{count} update available", "{count} updates available"),
                ReviewSummary.List([.. report.Items.Select(item => item.Name)]));
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
    public static TrayNotice? Finished(Operation operation, Config config, Func<string, BuildReview?> review, Func<long?> freeNow)
    {
        if (operation.Request.DryRun || operation.Status == OperationStatuses.Running) return null;
        DesktopNotification Notice(string title, string body) => new(OperationNotice, title, body.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal));
        var action = operation.Request.Action;
        if (operation.Status == OperationStatuses.Failed)
        {
            var error = operation.Error is { Length: > 0 } text ? text[..Math.Min(text.Length, 300)] : "An operation failed. Open the dashboard for its log.";
            return new TrayNotice(Notice("Nix Manager needs attention", error));
        }
        if (operation.Status != OperationStatuses.Succeeded) return null;
        if (action == Actions.ReviewBuild && operation.Request.ReviewId is { } reviewId && reviewId == BackgroundTasks.LastPrepared(config)?.ReviewId)
        {
            if (review(reviewId) is not { } prepared) return null;
            ImmutableArray<DesktopNotificationAction> actions = prepared.System is not null
                ? [new(NotificationActions.ApplyNow, "Apply now"), new(NotificationActions.ApplyOnBoot, "Apply on next boot")]
                : [new(NotificationActions.ApplyNow, "Apply now")];
            return new TrayNotice(Notice("Updates are ready", $"{ReviewSummary.Of(prepared)} Nothing changes until you apply them.") with { Actions = actions }, prepared);
        }
        if (action == Actions.Gc && BackgroundTasks.LastCleanup(config) is { } cleanup && cleanup.OperationId == operation.Id)
        {
            var after = freeNow();
            var freed = cleanup.Free is { } before && after is { } now ? now - before : 0;
            var first = freed > 0 ? $"{ReviewSummary.Bytes(freed)} were freed." : "Older restore points were removed.";
            return new TrayNotice(Notice("Space freed", $"{first} The newest {BackgroundTasks.KeptAutomatically} restore points of your apps are kept."));
        }
        if (action == Actions.ReviewBuild)
            return new TrayNotice(Notice("Changes are ready to review", "The build finished. Open Nix Manager to see what changes and apply it."));
        if (action == Actions.Check) return null;
        var host = operation.Request.Host is { Length: > 0 } named ? named : config.HostKey;
        return new TrayNotice(Notice("Nix Manager finished", $"{Actions.Label(action)} completed for {host}."));
    }
}
