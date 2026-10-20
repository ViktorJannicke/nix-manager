using System.Text.Json;
using NixManager.Operations;

namespace NixManager.Nix;

/// <summary>
/// Converts Nix's structured log (<c>--log-format internal-json</c>) into readable log text and aggregated
/// progress, with the same accounting as Nix's own progress bar.
/// </summary>
public sealed class NixProgress
{
    // Activity and result types from Nix's internal-json protocol.
    private const int CopyPath = 100, FileTransfer = 101, CopyPaths = 103, Builds = 104, BuildActivity = 105, Substitute = 108;
    private const int BuildLogLine = 101, SetPhase = 104, ProgressResult = 105, SetExpected = 106, PostBuildLogLine = 107;

    private sealed class Activity
    {
        public int Type;
        public string Name = "";
        public long Done, Expected, Running, Failed;
        public Dictionary<int, long> ExpectedByType = [];
        public string? Phase;
    }

    private sealed class ByType
    {
        public Dictionary<long, Activity> Live = [];
        public long Done, Expected, Failed;
    }

    // Insertion-ordered, as JavaScript's Map: the current builds are listed in start order.
    private readonly List<KeyValuePair<long, Activity>> _activities = [];
    private readonly Dictionary<int, ByType> _byType = [];
    private bool _started;

    private ByType Type(int type)
    {
        if (!_byType.TryGetValue(type, out var entry)) _byType[type] = entry = new ByType();
        return entry;
    }

    private (long Done, long Expected, long Running, long Failed) Stats(int type)
    {
        var entry = Type(type);
        long done = entry.Done, expected = entry.Done, running = 0, failed = entry.Failed;
        foreach (var activity in entry.Live.Values)
        {
            done += activity.Done;
            expected += activity.Expected;
            running += activity.Running;
            failed += activity.Failed;
        }
        return (done, Math.Max(expected, entry.Expected), running, failed);
    }

    private Activity? Find(long id)
    {
        foreach (var pair in _activities) if (pair.Key == id) return pair.Value;
        return null;
    }

    /// <summary>Log text for one line of output, or null for pure progress lines.</summary>
    public string? Line(string raw)
    {
        if (!raw.StartsWith("@nix ", StringComparison.Ordinal)) return raw;
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(raw.AsMemory(5));
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return raw;
        }
        if (message.ValueKind != JsonValueKind.Object) return raw;
        var id = (long)Number(message, "id");
        var fields = message.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array ? f : default;
        string FieldText(int index) => fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() > index ? Text(fields[index]) : "";
        double FieldNumber(int index) => fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() > index ? ToNumber(fields[index]) : 0;
        var action = message.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        switch (action)
        {
            case "msg":
            {
                var level = Number(message, "level");
                return level <= 3 ? NixText.StripAnsi(Property(message, "msg")) : null;
            }
            case "start":
            {
                _started = true;
                var type = (int)Number(message, "type");
                var text = Property(message, "text");
                var activity = new Activity
                {
                    Type = type,
                    Name = type is BuildActivity or Substitute
                        ? NixText.StoreName(fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() > 0 ? FieldText(0) : text)
                        : text,
                };
                _activities.RemoveAll(pair => pair.Key == id);
                _activities.Add(new(id, activity));
                Type(type).Live[id] = activity;
                return Number(message, "level") <= 3 && text.Length > 0 ? NixText.StripAnsi(text) : null;
            }
            case "stop":
            {
                var activity = Find(id);
                if (activity is null) return null;
                var entry = Type(activity.Type);
                entry.Done += activity.Done;
                entry.Failed += activity.Failed;
                foreach (var (type, expected) in activity.ExpectedByType) Type(type).Expected -= expected;
                entry.Live.Remove(id);
                _activities.RemoveAll(pair => pair.Key == id);
                return null;
            }
            case "result":
            {
                var activity = Find(id);
                var type = (int)Number(message, "type");
                if (type is BuildLogLine or PostBuildLogLine)
                    return $"{activity?.Name ?? "build"}> {NixText.StripAnsi(FieldText(0))}";
                if (activity is null) return null;
                if (type == ProgressResult)
                {
                    activity.Done = (long)FieldNumber(0);
                    activity.Expected = (long)FieldNumber(1);
                    activity.Running = (long)FieldNumber(2);
                    activity.Failed = (long)FieldNumber(3);
                }
                else if (type == SetExpected)
                {
                    var target = (int)FieldNumber(0);
                    var previous = activity.ExpectedByType.GetValueOrDefault(target);
                    var next = (long)FieldNumber(1);
                    activity.ExpectedByType[target] = next;
                    Type(target).Expected += next - previous;
                }
                else if (type == SetPhase) activity.Phase = FieldText(0);
                return null;
            }
            default:
                return null;
        }
    }

    public BuildProgress Snapshot()
    {
        var running = _activities.Select(pair => pair.Value).ToList();
        var builds = running.Where(a => a.Type == BuildActivity).Select(a => a.Phase is { Length: > 0 } ? $"{a.Name} ({a.Phase})" : a.Name).ToList();
        var fetching = running.Where(a => a.Type == Substitute).Select(a => a.Name).ToList();
        var build = Stats(Builds);
        var paths = Stats(CopyPaths);
        var download = Stats(FileTransfer);
        var copied = Stats(CopyPath);
        return new BuildProgress
        {
            Evaluating = !_started || (build.Expected == 0 && paths.Expected == 0 && builds.Count == 0 && fetching.Count == 0),
            Builds = new BuildCounts { Done = build.Done, Expected = build.Expected, Running = build.Running, Failed = build.Failed },
            Downloads = new DownloadCounts { Done = paths.Done, Expected = paths.Expected, Running = paths.Running },
            Bytes = new ByteCounts { Done = Math.Max(download.Done, copied.Done), Expected = Math.Max(download.Expected, copied.Expected) },
            Current = [.. builds.Concat(fetching).Take(4)],
        };
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ToNumber(value) : 0;

    // Number(x) in JavaScript: numbers as they are, numeric strings parsed, everything else 0 (NaN compares false).
    private static double ToNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        JsonValueKind.True => 1,
        _ => 0,
    };

    private static string Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? Text(value) : "";

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };
}
