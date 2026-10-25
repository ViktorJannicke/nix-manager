using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Operations;

namespace NixManager.Daemon;

/// <summary>
/// The dashboard's "Restart now" once a newer tray service is installed: it records the page the dashboard shows in
/// <c>reopen.json</c> in the state directory and asks the daemon to end with <see cref="DaemonCore.Replaced"/>. The tray
/// systemd starts next opens the dashboard on that page once (<see cref="Resume"/>) and removes the record.
/// </summary>
/// <param name="outdated">
/// <see cref="DaemonSettings.Outdated"/>; null outside the NixOS module's tray service, where the dashboard offers no restart.
/// </param>
public sealed class TrayRestart(Config config, Func<bool>? outdated = null, TimeProvider? clock = null)
{
    /// <summary>How long a record waits for the next tray; an older one, such as of a restart that failed, is ignored.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string? _page;

    /// <summary>The record of the page to reopen.</summary>
    public string Record => Path.Combine(config.StateDir, "reopen.json");

    /// <summary>A newer tray service is installed than this process runs; reads one small file.</summary>
    public bool Outdated => outdated?.Invoke() == true;

    /// <summary>Raised on the caller's thread once the dashboard asked to restart.</summary>
    public event Action? Requested;

    /// <summary>Records <paramref name="page"/> for the next tray and asks the daemon to end.</summary>
    public void Request(string page)
    {
        OperationStore.CreatePrivateDirectory(config.StateDir);
        OperationStore.AtomicJson(Record, new JsonObject { ["page"] = page, ["at"] = Timestamps.Now(_clock) }.ToJsonString());
        Requested?.Invoke();
    }

    /// <summary>Removes the record, such as of a restart the daemon refused.</summary>
    public void Discard()
    {
        try { File.Delete(Record); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// At the tray's start: the page the previous tray recorded before it restarted, which the next dashboard opens on;
    /// null without a record of the last <see cref="Lifetime"/>. The record is removed either way.
    /// </summary>
    public string? Resume()
    {
        string? page = null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Record)) is JsonObject record
                && record["page"] is JsonValue value && value.TryGetValue<string>(out var recorded)
                && record["at"] is JsonValue at && at.TryGetValue<string>(out var written) && Timestamps.Parse(written) is { } time
                && _clock.GetUtcNow() - time is var age && age >= TimeSpan.Zero && age <= Lifetime)
                page = recorded;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        Discard();
        _page = page;
        return page;
    }

    /// <summary>The page the first dashboard after <see cref="Resume"/> opens on; null for every later one.</summary>
    public string? TakePage() => Interlocked.Exchange(ref _page, null);
}
