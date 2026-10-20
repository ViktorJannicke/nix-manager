using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NixManager.Operations;

namespace NixManager.Nix;

/// <summary>Text helpers for Nix's output.</summary>
public static partial class NixText
{
    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]")]
    private static partial Regex Ansi();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_'-]*$")]
    private static partial Regex PlainAttribute();

    [GeneratedRegex("^.*/nix/store/[a-z0-9]{32}-")]
    private static partial Regex StorePrefix();

    [GeneratedRegex(@"\.drv$")]
    private static partial Regex DrvSuffix();

    [GeneratedRegex("['\"].*$")]
    private static partial Regex Quoted();

    public static string StripAnsi(string text) => Ansi().Replace(text, "");

    /// <summary><c>homeConfigurations."user@host"</c> style attribute for a flake fragment.</summary>
    public static string Attr(string name) => PlainAttribute().IsMatch(name) ? name : JavaScriptString(name);

    /// <summary>A string as <c>JSON.stringify</c> writes it.</summary>
    public static string JavaScriptString(string value) =>
        JsonSerializer.Serialize(value, ManagerJson.Compact.GetTypeInfo(typeof(string))!);

    /// <summary>Name of a store path or derivation without its hash and <c>.drv</c> suffix.</summary>
    public static string StoreName(string path) => Quoted().Replace(DrvSuffix().Replace(StorePrefix().Replace(path, ""), ""), "");

    /// <summary>A lock node's reference, as <c>lockRef</c> derives it for update reports.</summary>
    public static LockRef? LockRef(JsonElement? locked)
    {
        if (locked is not { ValueKind: JsonValueKind.Object } node) return null;
        string? Text(string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var rev = Text("rev");
        var narHash = Text("narHash");
        string? lastModified = null;
        if (node.TryGetProperty("lastModified", out var modified) && modified.ValueKind == JsonValueKind.Number)
            lastModified = Timestamps.Format(DateTimeOffset.FromUnixTimeMilliseconds((long)(modified.GetDouble() * 1000)));
        string? url = null, source = null;
        var type = Text("type");
        if (type is "github" or "gitlab" && Text("owner") is { Length: > 0 } owner && Text("repo") is { Length: > 0 } repo)
        {
            source = $"{owner}/{repo}";
            var host = type == "github" ? "github.com" : "gitlab.com";
            url = $"https://{host}/{source}{(rev is null ? "" : $"/commit/{rev}")}";
        }
        else if (Text("url") is { } address) source = UrlDecoration().Replace(address, "");
        else if (Text("path") is { } path) source = path;
        return new LockRef(rev, narHash, lastModified, source, url);
    }

    [GeneratedRegex(@"^git\+|\?.*$")]
    private static partial Regex UrlDecoration();

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed record LockRef(string? Rev, string? NarHash, string? LastModified, string? Source, string? Url);
