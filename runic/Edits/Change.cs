using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NixManager.Domain;
using NixManager.Nix;
using NixManager.Observation;

namespace NixManager.Edits;

/// <summary>The kinds of pending edit (<c>Change</c> in <c>src/draft.ts</c>).</summary>
public static class ChangeKinds
{
    public const string AddPackage = "add-package", RemovePackage = "remove-package", SetOption = "set-option",
        UnsetOption = "unset-option", CreateModule = "create-module";
}

/// <summary>The configurations an edit can target (<c>Target</c>).</summary>
public static class Targets
{
    public const string Home = "home", System = "system";
}

/// <summary>
/// One pending edit of the draft. The Bun version hashes and compares edits as <c>JSON.stringify</c> text, so an
/// edit keeps its members in the order it got them: the schema's order when read from <c>draft.json</c>
/// (<see cref="Decode"/>), the order the Bun command line creates them in otherwise (<see cref="AddPackage"/> and
/// the other factories), and the stored order when read from a review.
/// </summary>
[JsonConverter(typeof(ChangeConverter))]
[Runic.Application.Views.RunicBridgeCodec(typeof(ViewModels.ChangeBridgeCodec))]
public sealed class Change
{
    private readonly JsonObject _json;

    private Change(JsonObject json)
    {
        _json = json;
        Kind = Text("kind") ?? "";
        Scope = Text("scope") ?? "";
        File = Text("file") ?? "";
        Label = Text("label");
        Attr = Text("attr");
        Option = Text("option");
        Importer = Text("importer");
    }

    private string? Text(string name) => _json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public string Kind { get; }
    /// <summary><c>home</c> or <c>system</c>.</summary>
    public string Scope { get; }
    /// <summary>Path of the edited file inside the flake.</summary>
    public string File { get; }
    /// <summary>What the change belongs to, such as an experience title.</summary>
    public string? Label { get; }
    /// <summary>Package attribute of <c>add-package</c> and <c>remove-package</c>, such as <c>pkgs.fzf</c>.</summary>
    public string? Attr { get; }
    /// <summary>Option path of <c>set-option</c> and <c>unset-option</c>.</summary>
    public string? Option { get; }
    /// <summary>The module that imports a created file (<c>create-module</c>).</summary>
    public string? Importer { get; }
    /// <summary>The value of <c>set-option</c>, as JSON; null for JSON null or no value.</summary>
    public JsonNode? Value => _json["value"]?.DeepClone();
    public bool HasValue => _json.ContainsKey("value");

    public bool IsPackage => Kind is ChangeKinds.AddPackage or ChangeKinds.RemovePackage;
    public bool IsOption => Kind is ChangeKinds.SetOption or ChangeKinds.UnsetOption;

    /// <summary>The edit as JSON, in its member order.</summary>
    public JsonObject ToJson() => (JsonObject)_json.DeepClone();

    /// <summary><c>JSON.stringify(change)</c>.</summary>
    public override string ToString() => JsJson.Stringify(_json);

    /// <summary>Whether two edits are the same text (<c>same</c> in <c>src/changes.ts</c>), member order included.</summary>
    public bool Same(Change other) => ToString() == other.ToString();

    public static Change AddPackage(string scope, string file, string attr, string? label = null) =>
        Create(ChangeKinds.AddPackage, scope, file, label, ("attr", attr));

    public static Change RemovePackage(string scope, string file, string attr, string? label = null) =>
        Create(ChangeKinds.RemovePackage, scope, file, label, ("attr", attr));

    public static Change SetOption(string scope, string file, string option, JsonNode? value, string? label = null) =>
        Create(ChangeKinds.SetOption, scope, file, label, ("option", option), ("value", value));

    public static Change UnsetOption(string scope, string file, string option, string? label = null) =>
        Create(ChangeKinds.UnsetOption, scope, file, label, ("option", option));

    public static Change CreateModule(string scope, string file, string importer, string? label = null) =>
        Create(ChangeKinds.CreateModule, scope, file, label, ("importer", importer));

    // The Bun command line writes { kind, scope, file, <own members> }.
    private static Change Create(string kind, string scope, string file, string? label, params (string Name, JsonNode? Value)[] members)
    {
        var json = new JsonObject { ["kind"] = kind, ["scope"] = scope, ["file"] = file };
        foreach (var (name, value) in members) json[name] = value?.DeepClone();
        if (label is not null) json["label"] = label;
        return new Change(json);
    }

    /// <summary>An edit exactly as stored, keeping its member order (reviews, events).</summary>
    public static Change FromJson(JsonObject json) => new((JsonObject)json.DeepClone());

    private static readonly string[] Kinds =
        [ChangeKinds.AddPackage, ChangeKinds.RemovePackage, ChangeKinds.SetOption, ChangeKinds.UnsetOption, ChangeKinds.CreateModule];

    /// <summary>
    /// Validates an edit as the Bun version's schema decodes it (<c>Schema.decodeUnknownSync(Change)</c>): the
    /// members in the schema's order, unknown members dropped.
    /// </summary>
    /// <exception cref="ManagerException">The value is not an edit.</exception>
    public static Change Decode(JsonNode? node)
    {
        static ManagerException Invalid(string why) => new($"Invalid edit: {why}");
        if (node is not JsonObject json) throw Invalid("expected an object.");
        string Required(string name, int limit)
        {
            if (json[name] is not JsonValue value || !value.TryGetValue<string>(out var text)) throw Invalid($"{name} is missing.");
            if (text.Length > limit) throw Invalid($"{name} is longer than {limit} characters.");
            return text;
        }
        var kind = Required("kind", int.MaxValue);
        if (!Kinds.Contains(kind)) throw Invalid($"unknown kind {kind}.");
        var decoded = new JsonObject { ["kind"] = kind };
        switch (kind)
        {
            case ChangeKinds.AddPackage or ChangeKinds.RemovePackage:
                decoded["attr"] = Required("attr", 256);
                break;
            case ChangeKinds.SetOption:
                decoded["option"] = Required("option", 256);
                decoded["value"] = json["value"]?.DeepClone();
                break;
            case ChangeKinds.UnsetOption:
                decoded["option"] = Required("option", 256);
                break;
            case ChangeKinds.CreateModule:
                decoded["importer"] = Required("importer", 256);
                break;
        }
        var scope = Required("scope", int.MaxValue);
        if (scope is not (Targets.Home or Targets.System)) throw Invalid($"unknown scope {scope}.");
        decoded["scope"] = scope;
        decoded["file"] = Required("file", 256);
        if (json.ContainsKey("label")) decoded["label"] = Required("label", 120);
        return new Change(decoded);
    }

    /// <summary>A list of edits as the draft decodes it (<c>Changes</c>: at most 200).</summary>
    public static List<Change> DecodeList(JsonNode? node)
    {
        if (node is not JsonArray items) throw new ManagerException("Invalid edits: expected a list.");
        if (items.Count > 200) throw new ManagerException("Invalid edits: at most 200 are allowed.");
        return items.Select(Decode).ToList();
    }

    /// <summary>Identifies a change list, so a review can tell whether the draft moved on (<c>changeHash</c>).</summary>
    public static string Hash(IEnumerable<Change> changes) =>
        Repository.HashText(JsJson.Stringify(new JsonArray([.. changes.Select(change => (JsonNode)change.ToJson())])));

    /// <summary>A change's subject, for messages: the attribute or option it edits (<c>subject</c>).</summary>
    public string Subject => Attr ?? Option ?? File;
}

/// <summary>Reads an edit as stored and writes it in its own member order.</summary>
public sealed class ChangeConverter : JsonConverter<Change>
{
    public override Change Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonNode.Parse(ref reader) is JsonObject json ? Change.FromJson(json) : throw new JsonException("An edit must be an object.");

    public override void Write(Utf8JsonWriter writer, Change value, JsonSerializerOptions options) =>
        value.ToJson().WriteTo(writer, options);
}
