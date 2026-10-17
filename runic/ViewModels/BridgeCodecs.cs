using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Edits;
using Runic.Application.Views;

namespace NixManager.ViewModels;

// Bridge codecs for what the Bridge has no wire type for: a JsonObject, and pending edits, which keep their member
// order. The dashboard receives them as the JSON the Bun version's dashboard received. JsonElement and JsonNode
// values, such as option values, cross as any JSON value without a codec.

/// <summary>A JSON object, as the Bun contracts' <c>unknown</c>; null for JSON null. The Bridge has no wire type for <see cref="JsonObject"/>.</summary>
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
