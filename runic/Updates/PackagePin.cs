using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Nix;

namespace NixManager.Updates;

/// <summary>A package's <c>pkgs/NAME/source.json</c>: the pinned release and the channel it follows.</summary>
/// <param name="Channel">Null, <c>stable</c> or <c>nightly</c>; nightly follows the newest nightly or stable release.</param>
public sealed record PackagePin(string Version, string? Channel, string? NixpkgsRev);

/// <summary>Reads and validates pins.</summary>
public static partial class PackagePins
{
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)$")]
    internal static partial Regex StableVersion();

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)-nightly\.(\d{8})\.(\d+)$")]
    internal static partial Regex NightlyVersion();

    public static bool ValidVersion(string version) => StableVersion().IsMatch(version) || NightlyVersion().IsMatch(version);

    /// <summary>
    /// The pin in <paramref name="text"/>, or a <see cref="ManagerException"/> naming the first problem: the text is
    /// not JSON (with the .NET parser's reason), not an object, or a field is missing or not valid.
    /// </summary>
    /// <param name="name">The package, named in the failure.</param>
    public static PackagePin Parse(string text, string name)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new ManagerException(string.IsNullOrWhiteSpace(text)
                ? DisplayText.Of("stored_pin_empty", ("name", name))
                : DisplayText.Of("stored_pin_not_valid_json", ("name", name), ("reason", e.Message)));
        }
        if (root is not JsonObject pin)
            throw new ManagerException(DisplayText.Of("stored_pin_not_an_object", ("name", name), ("value", Format(root))));
        ManagerException NotText(string field, JsonNode? value) =>
            new(DisplayText.Of("stored_pin_field_not_text", ("name", name), ("field", field), ("value", Format(value))));

        if (!pin.TryGetPropertyValue("version", out var versionNode))
            throw new ManagerException(DisplayText.Of("stored_pin_field_missing", ("name", name), ("field", "version")));
        if (versionNode is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var version)) throw NotText("version", versionNode);
        if (!ValidVersion(version))
            throw new ManagerException(DisplayText.Of("stored_pin_version_format", ("name", name), ("value", version)));

        string? channel = null;
        if (pin.TryGetPropertyValue("channel", out var channelNode))
        {
            if (channelNode is JsonValue c && c.TryGetValue<string>(out var value) && value is "stable" or "nightly") channel = value;
            else throw new ManagerException(DisplayText.Of("stored_pin_channel_invalid", ("name", name), ("value", Format(channelNode))));
        }

        string? rev = null;
        if (pin.TryGetPropertyValue("nixpkgsRev", out var revNode))
        {
            if (revNode is JsonValue r && r.TryGetValue<string>(out var value)) rev = value;
            else throw NotText("nixpkgsRev", revNode);
        }
        return new PackagePin(version, channel, rev);
    }

    /// <summary>A value as JSON, as the failure names it.</summary>
    private static string Format(JsonNode? node) => JsJson.Stringify(node);
}
