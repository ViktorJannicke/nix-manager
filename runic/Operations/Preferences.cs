using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;

namespace NixManager.Operations;

/// <summary>
/// <c>preferences.json</c> in the state directory, shared with the Bun version. The backend only needs the first
/// choice: someone who has run operations before keeps the detailed view, everyone else starts simple, with a
/// kept history. It is written before the first operation is recorded, as <c>readPreferences</c> does.
/// </summary>
public static class Preferences
{
    private static string PathOf(Config config) => Path.Combine(config.StateDir, "preferences.json");

    public static JsonObject Read(Config config)
    {
        var current = Stored(config);
        if (current["detail"] is JsonValue detail && detail.TryGetValue<string>(out var value) && value is "simple" or "detailed")
            return current;
        var operations = Paths.Operations(config);
        var usedBefore = Directory.Exists(operations) && Directory.EnumerateFiles(operations, "*.json").Any();
        current["detail"] = usedBefore ? "detailed" : "simple";
        if (!usedBefore) current["history"] = true;
        OperationStore.CreatePrivateDirectory(config.StateDir);
        OperationStore.AtomicJson(PathOf(config), current.ToJsonString(new JsonSerializerOptions(ManagerJson.Default.Options)));
        return current;
    }

    private static JsonObject Stored(Config config)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(PathOf(config))) as JsonObject ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }
}
