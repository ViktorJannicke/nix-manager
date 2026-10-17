using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NixManager.Nix;

/// <summary>
/// JSON exactly as JavaScript's <c>JSON.stringify</c> writes it, for the values the Bun version hashes or compares
/// as text: draft hashes (<c>changeHash</c>), lock nodes and change lists. Members keep their order, strings escape
/// only what JSON requires, and numbers are written as JavaScript prints them.
/// </summary>
public static class JsJson
{
    /// <summary>A string literal, as <c>JSON.stringify(text)</c> writes it.</summary>
    public static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        AppendString(builder, text);
        return builder.ToString();
    }

    /// <summary><c>JSON.stringify(value)</c>, compact; null is written as <c>null</c>.</summary>
    public static string Stringify(JsonNode? value)
    {
        var builder = new StringBuilder();
        Append(builder, value, null, "");
        return builder.ToString();
    }

    /// <summary><c>JSON.stringify(value, null, 2)</c>.</summary>
    public static string Indented(JsonNode? value)
    {
        var builder = new StringBuilder();
        Append(builder, value, "  ", "");
        return builder.ToString();
    }

    public static string Stringify(JsonElement value) => Stringify(JsonSerializer.SerializeToNode(value, Operations.ManagerJson.Default.JsonElement));

    /// <summary>Whether two values are the same JSON, member order included (<c>JSON.stringify(a) === JSON.stringify(b)</c>).</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => Stringify(a) == Stringify(b);

    /// <summary>A number as JavaScript prints it: integers without a fraction, the shortest round trip otherwise.</summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "null";
        if (value == 0) return "0";
        var magnitude = Math.Abs(value);
        if (magnitude >= 1e21 || magnitude < 1e-6)
        {
            // JavaScript: 1e+21, 1.5e-7.
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            var e = text.IndexOf('E');
            if (e < 0) return text;
            var mantissa = text[..e];
            var exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            return $"{mantissa}e{(exponent >= 0 ? "+" : "-")}{Math.Abs(exponent)}";
        }
        // Between 1e-6 and 1e21 JavaScript writes the shortest round-trip digits without an exponent: 1.2345678901234568E+20
        // is 123456789012345680000 (a conversion to decimal would round to 15 digits).
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var at = shortest.IndexOf('E');
        if (at < 0) return shortest;
        var negative = shortest.StartsWith('-');
        var digitsText = shortest[(negative ? 1 : 0)..at];
        var point = digitsText.IndexOf('.');
        var digits = digitsText.Replace(".", "", StringComparison.Ordinal);
        var whole = (point < 0 ? digitsText.Length : point) + int.Parse(shortest[(at + 1)..], CultureInfo.InvariantCulture);
        var plain = whole <= 0 ? $"0.{new string('0', -whole)}{digits}"
            : whole >= digits.Length ? digits + new string('0', whole - digits.Length)
            : $"{digits[..whole]}.{digits[whole..]}";
        return negative ? $"-{plain}" : plain;
    }

    private static void Append(StringBuilder builder, JsonNode? value, string? indent, string current)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject members:
            {
                if (members.Count == 0)
                {
                    builder.Append("{}");
                    return;
                }
                builder.Append('{');
                var inner = indent is null ? current : current + indent;
                var first = true;
                foreach (var (name, member) in members)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    if (indent is not null) builder.Append('\n').Append(inner);
                    AppendString(builder, name);
                    builder.Append(indent is null ? ":" : ": ");
                    Append(builder, member, indent, inner);
                }
                if (indent is not null) builder.Append('\n').Append(current);
                builder.Append('}');
                return;
            }
            case JsonArray items:
            {
                if (items.Count == 0)
                {
                    builder.Append("[]");
                    return;
                }
                builder.Append('[');
                var inner = indent is null ? current : current + indent;
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    if (indent is not null) builder.Append('\n').Append(inner);
                    Append(builder, items[i], indent, inner);
                }
                if (indent is not null) builder.Append('\n').Append(current);
                builder.Append(']');
                return;
            }
            case JsonValue scalar:
                switch (scalar.GetValueKind())
                {
                    case JsonValueKind.String:
                        AppendString(builder, scalar.GetValue<string>());
                        return;
                    case JsonValueKind.Number:
                        // A value created from an int or long has no double inside; its JSON text parses the same.
                        builder.Append(Number(double.Parse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)));
                        return;
                    case JsonValueKind.True:
                        builder.Append("true");
                        return;
                    case JsonValueKind.False:
                        builder.Append("false");
                        return;
                    default:
                        builder.Append("null");
                        return;
                }
        }
    }

    private static void AppendString(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    // Lone surrogates are escaped (well-formed JSON.stringify).
                    else if (char.IsHighSurrogate(c) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])))
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1])))
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
