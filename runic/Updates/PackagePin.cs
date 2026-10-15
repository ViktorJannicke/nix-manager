using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Nix;

namespace NixManager.Updates;

/// <summary>A package's <c>pkgs/NAME/source.json</c>: the pinned release and the channel it follows (<c>Pin</c> in <c>src/updates.ts</c>).</summary>
/// <param name="Channel">Null, <c>stable</c> or <c>nightly</c>; nightly follows the newest nightly or stable release.</param>
public sealed record PackagePin(string Version, string? Channel, string? NixpkgsRev);

/// <summary>Reads pins with the Bun version's validation and its messages (<c>parsePin</c>).</summary>
public static partial class PackagePins
{
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)$")]
    internal static partial Regex StableVersion();

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)-nightly\.(\d{8})\.(\d+)$")]
    internal static partial Regex NightlyVersion();

    public static bool ValidVersion(string version) => StableVersion().IsMatch(version) || NightlyVersion().IsMatch(version);

    private const string Struct =
        """{ readonly version: { string | filter }; readonly channel?: "stable" | "nightly" | undefined; readonly nixpkgsRev?: string | undefined }""";

    private const string Header = $"Invalid source pin: (parseJson <-> {Struct})";

    /// <summary>
    /// The pin in <paramref name="text"/>, or a <see cref="ManagerException"/> whose message is Effect Schema's
    /// error tree for <c>Schema.parseJson(Pin)</c>, as the Bun version reports it. JSON syntax errors name the
    /// .NET parser's reason where Bun names JavaScriptCore's.
    /// </summary>
    public static PackagePin Parse(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            var reason = string.IsNullOrWhiteSpace(text) ? "Unexpected EOF" : e.Message;
            throw new ManagerException($"{Header}\n└─ Encoded side transformation failure\n   └─ parseJson\n      └─ Transformation process failure\n         └─ JSON Parse error: {reason}");
        }
        if (root is not JsonObject pin)
            throw new ManagerException($"{Header}\n└─ Type side transformation failure\n   └─ Expected {Struct}, actual {Format(root)}");
        string Field(string name, string tree) =>
            $"{Header}\n└─ Type side transformation failure\n   └─ {Struct}\n      └─ [\"{name}\"]\n         └─ {tree}";

        if (!pin.TryGetPropertyValue("version", out var versionNode)) throw new ManagerException(Field("version", "is missing"));
        if (versionNode is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var version))
            throw new ManagerException(Field("version",
                $"{{ string | filter }}\n            └─ From side refinement failure\n               └─ Expected string, actual {Format(versionNode)}"));
        if (!ValidVersion(version)) throw new ManagerException(Field("version", "Expected X.Y.Z or X.Y.Z-nightly.YYYYMMDD.N"));

        string? channel = null;
        if (pin.TryGetPropertyValue("channel", out var channelNode))
        {
            if (channelNode is JsonValue c && c.TryGetValue<string>(out var value) && value is "stable" or "nightly") channel = value;
            else
            {
                var actual = Format(channelNode);
                throw new ManagerException(Field("channel",
                    $"\"stable\" | \"nightly\" | undefined\n" +
                    $"            ├─ \"stable\" | \"nightly\"\n" +
                    $"            │  ├─ Expected \"stable\", actual {actual}\n" +
                    $"            │  └─ Expected \"nightly\", actual {actual}\n" +
                    $"            └─ Expected undefined, actual {actual}"));
            }
        }

        string? rev = null;
        if (pin.TryGetPropertyValue("nixpkgsRev", out var revNode))
        {
            if (revNode is JsonValue r && r.TryGetValue<string>(out var value)) rev = value;
            else
            {
                var actual = Format(revNode);
                throw new ManagerException(Field("nixpkgsRev",
                    $"string | undefined\n            ├─ Expected string, actual {actual}\n            └─ Expected undefined, actual {actual}"));
            }
        }
        return new PackagePin(version, channel, rev);
    }

    /// <summary>A value as Effect's <c>formatUnknown</c> writes it.</summary>
    private static string Format(JsonNode? node) => JsJson.Stringify(node);
}
