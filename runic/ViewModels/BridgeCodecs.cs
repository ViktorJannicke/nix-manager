using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Edits;
using Runic.Application.Views;

namespace NixManager.ViewModels;

// Bridge codecs for the values the Bun version's contracts keep as arbitrary JSON (option values, an edit's
// value) and for pending edits, which keep their member order. The dashboard receives them as the JSON the Bun
// version's dashboard received.

/// <summary>Any JSON value, as the Bun contracts' <c>unknown</c>; null for JSON null.</summary>
[RunicCodecShape("unknown", "$value", "$value")]
public sealed class JsonNodeBridgeCodec : IRunicBridgeCodec<JsonNode?>
{
    public static JsonNode? Read(JsonElement value) => JsonNode.Parse(value.GetRawText());

    public static void Write(Utf8JsonWriter writer, JsonNode? value)
    {
        if (value is null) writer.WriteNullValue();
        else value.WriteTo(writer);
    }
}

/// <summary>Any JSON value, as the Bun contracts' <c>unknown</c>; an absent value is written as null.</summary>
[RunicCodecShape("unknown", "$value", "$value")]
public sealed class JsonElementBridgeCodec : IRunicBridgeCodec<JsonElement>
{
    public static JsonElement Read(JsonElement value) => value.Clone();

    public static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
        else value.WriteTo(writer);
    }
}

/// <summary>
/// A pending edit as <c>Change</c> in <c>src/draft.ts</c>: written in its member order, read as the Bun
/// dashboard's store decodes edits it receives (<see cref="Change.Decode"/>).
/// </summary>
[RunicCodecShape(
    "{ readonly kind: \"add-package\" | \"remove-package\"; readonly attr: string; readonly scope: \"home\" | \"system\"; readonly file: string; readonly label?: string }"
    + " | { readonly kind: \"set-option\"; readonly option: string; readonly value: unknown; readonly scope: \"home\" | \"system\"; readonly file: string; readonly label?: string }"
    + " | { readonly kind: \"unset-option\"; readonly option: string; readonly scope: \"home\" | \"system\"; readonly file: string; readonly label?: string }"
    + " | { readonly kind: \"create-module\"; readonly importer: string; readonly scope: \"home\" | \"system\"; readonly file: string; readonly label?: string }",
    "($value as never)", "$value")]
public sealed class ChangeBridgeCodec : IRunicBridgeCodec<Change>
{
    public static Change Read(JsonElement value) => Change.Decode(JsonNode.Parse(value.GetRawText()));

    public static void Write(Utf8JsonWriter writer, Change value) => value.ToJson().WriteTo(writer);
}
