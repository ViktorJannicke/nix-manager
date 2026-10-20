using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Reviews;

namespace NixManager.Nix;

/// <summary>Readers for Nix's own outputs.</summary>
public static partial class NixOutputs
{
    private static readonly Dictionary<string, double> Units = new()
    {
        ["B"] = 1, ["KiB"] = 1024, ["MiB"] = 1024d * 1024, ["GiB"] = 1024d * 1024 * 1024, ["TiB"] = 1024d * 1024 * 1024 * 1024,
    };

    [GeneratedRegex(@"(?:^|, )([+-]\d+(?:\.\d+)?) (B|KiB|MiB|GiB|TiB)$")]
    private static partial Regex SizeSuffix();

    [GeneratedRegex(@"^([+-]\d+(?:\.\d+)?) (B|KiB|MiB|GiB|TiB)$")]
    private static partial Regex SizeOnly();

    private static List<string> Versions(string value) =>
        value == "∅" ? [] : value.Split(", ").Select(v => v == "ε" ? "" : v).ToList();

    /// <summary>Parses <c>nix store diff-closures</c> output into package changes (<c>parseClosureDiff</c>).</summary>
    public static List<PackageChange> ParseClosureDiff(string text)
    {
        var changes = new List<PackageChange>();
        foreach (var raw in NixText.StripAnsi(text).Split('\n'))
        {
            var line = raw.Trim();
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (line.Length == 0 || separator <= 0) continue;
            var name = line[..separator];
            var rest = line[(separator + 2)..];
            long sizeDelta = 0;
            var sized = SizeSuffix().Match(rest);
            if (sized.Success)
            {
                // Math.round: halves round up.
                sizeDelta = (long)Math.Floor(double.Parse(sized.Groups[1].Value, CultureInfo.InvariantCulture) * Units[sized.Groups[2].Value] + 0.5);
                rest = rest[..^sized.Length];
            }
            List<string> before = [], after = [];
            if (rest.Contains(" → ", StringComparison.Ordinal))
            {
                var sides = rest.Split(" → ");
                before = Versions(sides[0]);
                after = Versions(sides[1]);
            }
            else if (rest.Length > 0 && !SizeOnly().IsMatch(rest)) continue;
            changes.Add(new PackageChange
            {
                Name = name,
                Before = before,
                After = after,
                SizeDelta = sizeDelta,
                Kind = before.Count == 0 && after.Count > 0 ? "added"
                    : before.Count > 0 && after.Count == 0 ? "removed"
                    : before.Count > 0 || after.Count > 0 ? "changed"
                    : "rebuilt",
            });
        }
        return changes;
    }

    private static JsonObject Nodes(string text)
    {
        try
        {
            return JsonNode.Parse(text)?["nodes"] as JsonObject ?? [];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static LockRef? Reference(JsonNode? node) =>
        node?["locked"] is JsonObject locked ? NixText.LockRef(JsonSerializer.Deserialize(locked.ToJsonString(), Operations.ManagerJson.Default.JsonElement)) : null;

    /// <summary>Every changed node between two lock files, transitive ones included (<c>lockChanges</c>).</summary>
    public static List<LockChange> LockChanges(string before, string after)
    {
        var old = Nodes(before);
        var next = Nodes(after);
        string Key(JsonNode? node) => JsJson.Stringify(node?["locked"]);
        return old.Select(pair => pair.Key).Concat(next.Select(pair => pair.Key)).Distinct()
            .Where(name => name != "root" && Key(old[name]) != Key(next[name]))
            .Order(StringComparer.Ordinal)
            .Select(name => new LockChange { Name = name, Before = Reference(old[name]), After = Reference(next[name]) })
            .ToList();
    }

    [GeneratedRegex("-linux-([^/]+)/")]
    private static partial Regex Kernel();

    /// <summary>The kernel version of a <c>kernel</c> link's target (<c>kernelVersion</c>).</summary>
    public static string? KernelVersion(string? target) =>
        target is not null && Kernel().Match(target) is { Success: true } match ? match.Groups[1].Value : null;
}
