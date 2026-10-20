using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NixManager.Domain;

namespace NixManager.Operations;

// The stored and streamed shapes of operations and their events. Records written by earlier versions still read
// here: property names are camelCase, optional members are omitted when absent, and members this version
// does not know are kept (JsonExtensionData) so a record keeps them when it is written again.

public static class OperationStatuses
{
    public const string Running = "running", Succeeded = "succeeded", Failed = "failed", Cancelled = "cancelled", Interrupted = "interrupted";
}

public static class StepStatuses
{
    public const string Pending = "pending", Running = "running", Succeeded = "succeeded", Failed = "failed", Cancelled = "cancelled", Skipped = "skipped";
}

/// <summary>Aggregated progress of a Nix build, derived from <c>--log-format internal-json</c>.</summary>
public sealed record BuildProgress
{
    /// <summary>No build or download has been scheduled yet; Nix is still evaluating.</summary>
    public bool Evaluating { get; init; }
    public BuildCounts Builds { get; init; } = new();
    /// <summary>Store paths substituted from binary caches.</summary>
    public DownloadCounts Downloads { get; init; } = new();
    public ByteCounts Bytes { get; init; } = new();
    /// <summary>Names of derivations currently building or downloading.</summary>
    public IReadOnlyList<string> Current { get; init; } = [];
}

public sealed record BuildCounts
{
    public long Done { get; init; }
    public long Expected { get; init; }
    public long Running { get; init; }
    public long Failed { get; init; }
}

public sealed record DownloadCounts
{
    public long Done { get; init; }
    public long Expected { get; init; }
    public long Running { get; init; }
}

public sealed record ByteCounts
{
    public long Done { get; init; }
    public long Expected { get; init; }
}

public sealed class StepOutcome
{
    public required string Id { get; set; }
    public required string Label { get; set; }
    public required string Status { get; set; }
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public BuildProgress? Progress { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>A failure in plain words.</summary>
public sealed record Explanation
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public Fix? Fix { get; init; }
}

public sealed record Fix
{
    public required string Kind { get; init; }
    public required IReadOnlyList<string> Packages { get; init; }
    public string? Scope { get; init; }
}

/// <summary>The state an applied change replaced.</summary>
public sealed record UndoPoint
{
    public IReadOnlyList<string> Changes { get; init; } = [];
    public UndoGenerations Generations { get; init; } = new();
    public IReadOnlyList<UndoFile> Files { get; init; } = [];
}

public sealed record UndoGenerations
{
    public int? Home { get; init; }
    public int? System { get; init; }
}

public sealed record UndoFile
{
    public required string Path { get; init; }
    public bool Created { get; init; }
    /// <summary>A hash of what the change wrote, or null when it deleted the file; null is written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Written { get; init; }
}

public sealed record RollbackOutcome
{
    public bool Done { get; init; }
    public string? Error { get; init; }
}

/// <summary>One operation as stored in <c>operations/ID.json</c> and sent to the dashboard.</summary>
public sealed class Operation
{
    public required string Id { get; set; }
    public int Pid { get; set; }
    public required string Flake { get; set; }
    public required Request Request { get; set; }
    public required string Status { get; set; }
    public required string StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public string Step { get; set; } = "";
    public List<StepOutcome>? Steps { get; set; }
    public string? Error { get; set; }
    public Explanation? Explanation { get; set; }
    public bool? FinishingPrivilegedStep { get; set; }
    public UndoPoint? Undo { get; set; }
    public string? Held { get; set; }
    public RollbackOutcome? Rollback { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>A deep copy, for events and readers outside the operation's own task.</summary>
    public Operation Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(this, ManagerJson.Default.Operation), ManagerJson.Default.Operation)!;
}

public sealed record LogResult(string Text, long End, bool Truncated);

public sealed record UpdateItem
{
    public required string Name { get; init; }
    public required string Current { get; init; }
    public required string Latest { get; init; }
    /// <summary><c>input</c> or <c>package</c>.</summary>
    public required string Kind { get; init; }
    public string? CurrentDate { get; init; }
    public string? LatestDate { get; init; }
    public string? Source { get; init; }
    public string? Url { get; init; }
}

public sealed record UpdateReport
{
    public required string CheckedAt { get; init; }
    public List<UpdateItem> Items { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

/// <summary><c>updates.json</c>: the last report, for the checkout it was made for.</summary>
public sealed record SavedUpdates(string Flake, UpdateReport Report);

/// <summary>
/// An ordered event of the operation service: a changed operation, output of its running step, or build
/// progress. The shape is the one the dashboard and a forwarded command
/// line both consume.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OperationChanged), "operation")]
[JsonDerivedType(typeof(OperationOutput), "output")]
[JsonDerivedType(typeof(OperationProgress), "progress")]
public abstract record ManagerEvent
{
    public long Sequence { get; init; }
    public string Timestamp { get; init; } = "";
}

public sealed record OperationChanged(Operation Operation) : ManagerEvent;

public sealed record OperationOutput(string OperationId, string Stream, string Text, long Offset) : ManagerEvent;

public sealed record OperationProgress(string OperationId, string StepId, BuildProgress Progress) : ManagerEvent;

/// <summary>Timestamps as JavaScript's <c>toISOString</c> writes them, which every stored record uses.</summary>
public static class Timestamps
{
    public static string Format(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Now(TimeProvider? clock = null) => Format((clock ?? TimeProvider.System).GetUtcNow());

    public static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
