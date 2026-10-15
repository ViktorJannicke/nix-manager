using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NixManager.Domain;

namespace NixManager.Operations;

/// <summary>
/// JSON as the Bun version writes it with <c>JSON.stringify(value, null, 2)</c>: camelCase, absent members
/// omitted, two-space indentation, and no escaping of text that JSON does not require.
/// </summary>
public static class ManagerJson
{
    private static JsonSerializerOptions Options(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = indented,
        IndentSize = 2,
        NewLine = "\n",
        AllowOutOfOrderMetadataProperties = true,
    };

    /// <summary>Indented, as stored files and <c>--json</c> output are written.</summary>
    public static ManagerJsonContext Default { get; } = new(Options(indented: true));

    /// <summary>One line, for events streamed to a dashboard or a forwarded command line.</summary>
    public static ManagerJsonContext Compact { get; } = new(Options(indented: false));

    public static string Serialize<T>(T value, ManagerJsonContext? context = null) =>
        JsonSerializer.Serialize(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(context ?? Default).GetTypeInfo(typeof(T))!);

    public static T? Deserialize<T>(string json, ManagerJsonContext? context = null) =>
        JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(context ?? Default).GetTypeInfo(typeof(T))!);
}

/// <summary>The <c>status --json</c> document.</summary>
public sealed record StatusDocument(string Host, string Flake, Host Config,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] UpdateReport? Updates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Operation? Latest);

/// <summary>The <c>check --json</c> document.</summary>
public sealed record CheckDocument(Operation? Operation, UpdateReport? Updates);

[JsonSerializable(typeof(Operation))]
[JsonSerializable(typeof(List<Operation>))]
[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Dictionary<string, Host>))]
[JsonSerializable(typeof(UpdateReport))]
[JsonSerializable(typeof(SavedUpdates))]
[JsonSerializable(typeof(ManagerEvent))]
[JsonSerializable(typeof(LogResult))]
[JsonSerializable(typeof(StatusDocument))]
[JsonSerializable(typeof(CheckDocument))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class ManagerJsonContext : JsonSerializerContext;
