using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Configuration;

namespace NixManager.Tests;

/// <summary>
/// A checkout with a copy of <c>Fixtures/recorded-state</c>: real operation records, logs and the update report the
/// Bun version wrote, with the checkout path replaced. <c>record-bun-cli.py</c> prepares the same for the Bun
/// version's recorded outputs.
/// </summary>
internal sealed partial class RecordedFixtures : IDisposable
{
    public const string RecordedFlake = "/home/user/my-flakes";
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public string Root { get; }
    public string StateDir => Path.Combine(Root, "state", "nix-manager");
    public string Operations => Path.Combine(StateDir, "operations");
    public Dictionary<string, string?> Variables { get; }
    public ManagerEnvironment Environment { get; }

    /// <param name="files">Files to add, by path relative to the checkout; <c>&lt;flake&gt;</c> in their text is the checkout.</param>
    public RecordedFixtures(IReadOnlyDictionary<string, string>? files = null)
    {
        var created = System.IO.Directory.CreateTempSubdirectory("nix-manager-fixtures-").FullName;
        Root = NixManager.Operations.Native.RealPath(created) ?? created;
        File.WriteAllText(Path.Combine(Root, "flake.nix"), "{}");
        File.WriteAllText(Path.Combine(Root, "hosts.json"),
            """{"test":{"homeConfiguration":"user-test","nixosConfiguration":"test-machine","nixosHostName":"test-machine","system":"x86_64-linux"}}""");
        Copy(Path.Combine(Directory, "recorded-state"), StateDir);
        foreach (var file in System.IO.Directory.EnumerateFiles(StateDir, "*.json", SearchOption.AllDirectories))
            File.WriteAllText(file, File.ReadAllText(file).Replace(RecordedFlake, Root, StringComparison.Ordinal));
        var profile = Path.Combine(Root, "state", "nix", "profiles", "home-manager");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, "");
        foreach (var (name, text) in files ?? new Dictionary<string, string>())
        {
            var path = Path.Combine(Root, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Replace("<flake>", Root, StringComparison.Ordinal));
        }
        Variables = new()
        {
            ["NIX_MANAGER_HOSTS"] = Path.Combine(Root, "hosts.json"),
            ["NIX_MANAGER_FLAKE"] = Root,
            ["NIX_MANAGER_HOST"] = "test",
            ["XDG_STATE_HOME"] = Path.Combine(Root, "state"),
            ["NO_COLOR"] = "1",
        };
        Environment = new ManagerEnvironment(name => Variables.GetValueOrDefault(name), "test-machine", "test", Root);
    }

    private static void Copy(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in System.IO.Directory.EnumerateDirectories(source))
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);

    [GeneratedRegex(@"/run/wrappers/bin/pkexec \S+(?: \S+)? --privileged")]
    private static partial Regex PrivilegedText();

    /// <summary>
    /// Text with the checkout as <c>&lt;flake&gt;</c> and the privileged entry point, which names the Bun or the
    /// .NET program, as <c>&lt;privileged&gt;</c>.
    /// </summary>
    public string NormalizeText(string text) => PrivilegedText().Replace(text.Replace(Root, "<flake>", StringComparison.Ordinal), "<privileged>");

    public static string NormalizeRecordedText(string text) => PrivilegedText().Replace(text, "<privileged>");

    /// <summary>A JSON document with argument vectors' privileged prefix collapsed to <c>&lt;privileged&gt;</c>.</summary>
    public static JsonNode? NormalizeJson(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var (name, child) in value.ToList()) value[name] = NormalizeJson(child?.DeepClone());
                return value;
            case JsonArray array:
            {
                var items = array.Select(item => NormalizeJson(item?.DeepClone())).ToList();
                var marker = items.FindIndex(item => item is JsonValue v && v.TryGetValue<string>(out var s) && s == "--privileged");
                if (marker > 0 && items[0] is JsonValue first && first.TryGetValue<string>(out var program) && program.EndsWith("pkexec", StringComparison.Ordinal))
                    items = [JsonValue.Create("<privileged>"), .. items.Skip(marker + 1)];
                return new JsonArray([.. items]);
            }
            default:
                return node;
        }
    }

    /// <summary>Structural equality: objects by member, ignoring order; arrays and values exactly.</summary>
    public static string? Difference(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        switch (expected, actual)
        {
            case (null, null):
                return null;
            case (JsonObject e, JsonObject a):
                foreach (var name in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    if (!e.ContainsKey(name)) return $"{path}.{name}: unexpected {a[name]?.ToJsonString()}";
                    if (!a.ContainsKey(name)) return $"{path}.{name}: missing {e[name]?.ToJsonString()}";
                    if (Difference(e[name], a[name], $"{path}.{name}") is { } difference) return difference;
                }
                return null;
            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count) return $"{path}: {e.Count} items expected, {a.Count} found";
                for (var i = 0; i < e.Count; i++)
                    if (Difference(e[i], a[i], $"{path}[{i}]") is { } difference) return difference;
                return null;
            case (JsonValue e, JsonValue a):
                return JsonNode.DeepEquals(e, a) || (e.TryGetValue<double>(out var x) && a.TryGetValue<double>(out var y) && x == y)
                    ? null
                    : $"{path}: expected {e.ToJsonString()}, found {a.ToJsonString()}";
            default:
                return $"{path}: expected {expected?.ToJsonString() ?? "null"}, found {actual?.ToJsonString() ?? "null"}";
        }
    }
}
