using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Nix;
using NixManager.Observation;

namespace NixManager.Operations;

/// <summary>Whether the computer may work unasked: plugged in and not busy.</summary>
public sealed record BackgroundConditions(Func<bool> Mains, Func<bool> Idle)
{
    /// <summary>This computer: <c>NIX_MANAGER_SYSFS</c> or /sys for power, the load average for work.</summary>
    public static BackgroundConditions Computer { get; } = new(() => Hardware.OnMains(Hardware.SysfsRoot), Hardware.NotBusy);
}

/// <summary>The input updates last prepared in the background, so the same ones are not prepared twice.</summary>
public sealed record PreparedUpdates(string Signature, string ReviewId);

/// <summary>The last automatic cleanup, with the free space before it to say what it freed.</summary>
public sealed record AutomaticCleanup(string At, string OperationId, long? Free);

/// <summary>
/// Work the daemon (W300-031) starts unasked: preparing the input updates the last check
/// found, and a weekly cleanup of the apps' restore points, each only when the preferences allow it and the computer
/// is plugged in and not busy. Each call returns the started operation or why nothing started.
/// </summary>
public sealed class BackgroundTasks(IOperationService manager, BackgroundConditions? conditions = null, TimeProvider? clock = null)
{
    /// <summary>Automatic cleanup keeps this many restore points of the apps (<c>keptAutomatically</c>).</summary>
    public const int KeptAutomatically = 5;

    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private readonly BackgroundConditions _conditions = conditions ?? BackgroundConditions.Computer;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static string PreparedPath(Config config) => Path.Combine(config.StateDir, "background.json");
    private static string CleanedPath(Config config) => Path.Combine(config.StateDir, "cleanup.json");

    private static JsonObject? ReadObject(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string? Text(JsonObject value, string name) =>
        value[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The updates last prepared in the background (<c>lastPrepared</c>).</summary>
    public static PreparedUpdates? LastPrepared(Config config) =>
        ReadObject(PreparedPath(config)) is { } value ? new PreparedUpdates(Text(value, "signature") ?? "", Text(value, "reviewId") ?? "") : null;

    /// <summary>
    /// Keeps the record of the last prepared updates pointed at a review that exists once <paramref name="reviewId"/>
    /// was discarded as obsolete: at the newer review that <paramref name="replacement"/> names, or at none, so the
    /// updates are prepared again for the checkout as it is now.
    /// </summary>
    public static void Discarded(Config config, string reviewId, string? replacement)
    {
        if (LastPrepared(config) is not { } prepared || prepared.ReviewId != reviewId) return;
        if (replacement is null) File.Delete(PreparedPath(config));
        else OperationStore.AtomicJson(PreparedPath(config), JsJson.Indented(new JsonObject { ["signature"] = prepared.Signature, ["reviewId"] = replacement }));
    }

    /// <summary>The last automatic cleanup (<c>lastCleanup</c>).</summary>
    public static AutomaticCleanup? LastCleanup(Config config) =>
        ReadObject(CleanedPath(config)) is { } value
            ? new AutomaticCleanup(Text(value, "at") ?? "", Text(value, "operationId") ?? "",
                value["free"] is JsonValue free && free.TryGetValue<double>(out var bytes) ? (long)bytes : null)
            : null;

    /// <summary>Free bytes in /nix/store, or null (<c>freeSpace</c>).</summary>
    public static long? FreeSpace(string store = "/nix/store") => SystemObserver.Disk(store)?.Free;

    private static bool Allowed(Config config, string preference) =>
        Preferences.Read(config)[preference] is JsonValue value && value.TryGetValue<bool>(out var allowed) && allowed;

    /// <summary>
    /// Starts preparing the input updates the last check found, when the preferences allow it, these updates were not
    /// prepared before and the computer is plugged in and not busy (<c>prepareInBackground</c>). Package pins change
    /// files in the checkout, so only source updates are prepared unasked.
    /// </summary>
    /// <returns>The started operation, or why none started.</returns>
    public (Operation? Operation, string? Reason) PrepareInBackground()
    {
        var config = manager.Config;
        if (!Allowed(config, "prepareUpdates")) return (null, "Preparing updates in the background is off.");
        var inputs = manager.Updates()?.Items.Where(item => item.Kind == "input").ToList() ?? [];
        if (inputs.Count == 0) return (null, "There are no updates to prepare.");
        var signature = JsJson.Stringify(new JsonArray([.. inputs.Select(item => (JsonNode)new JsonArray(item.Name, item.Latest))]));
        if (LastPrepared(config)?.Signature == signature) return (null, "These updates were prepared already.");
        if (manager.Busy) return (null, "Something else is running.");
        if (!_conditions.Mains()) return (null, "The computer runs on battery.");
        if (!_conditions.Idle()) return (null, "The computer is busy.");
        var run = manager.Start(Request.Create() with
        {
            Action = Actions.ReviewBuild,
            Host = config.HostKey,
            Update = true,
            ReviewId = Guid.NewGuid().ToString(),
        });
        var reviewId = run.Operation.Request.ReviewId!;
        OperationStore.AtomicJson(PreparedPath(config), JsJson.Indented(new JsonObject { ["signature"] = signature, ["reviewId"] = reviewId }));
        return (run.Operation, null);
    }

    /// <summary>
    /// Removes older restore points of the apps and unused store paths, at most once a week, when the preferences
    /// allow it and the computer is plugged in and not busy. System restore points need a password, so they are never
    /// removed unasked (<c>cleanUpInBackground</c>).
    /// </summary>
    /// <returns>The started operation, or why none started.</returns>
    public (Operation? Operation, string? Reason) CleanUpInBackground(DateTimeOffset? now = null, string store = "/nix/store")
    {
        var config = manager.Config;
        var at = now ?? _clock.GetUtcNow();
        if (!Allowed(config, "cleanUp")) return (null, "Cleaning up automatically is off.");
        if (LastCleanup(config) is { } last && Timestamps.Parse(last.At) is { } previous && at - previous < Week)
            return (null, "The last cleanup was less than a week ago.");
        if (manager.Busy) return (null, "Something else is running.");
        if (!_conditions.Mains()) return (null, "The computer runs on battery.");
        if (!_conditions.Idle()) return (null, "The computer is busy.");
        var free = FreeSpace(store);
        var run = manager.Start(Request.Create() with { Action = Actions.Gc, Scope = Scopes.Home, Host = config.HostKey, Retention = KeptAutomatically });
        var record = new JsonObject { ["at"] = Timestamps.Format(at), ["operationId"] = run.Operation.Id };
        // JsJson writes numbers as JavaScript does, from doubles.
        if (free is { } bytes) record["free"] = (double)bytes;
        OperationStore.AtomicJson(CleanedPath(config), JsJson.Indented(record));
        return (run.Operation, null);
    }
}
