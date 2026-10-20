using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NixManager.Dashboard;
using Runic.Translations;

namespace NixManager.Domain;

/// <summary>
/// Text for a person: a message of the dashboard's catalogue with its inputs, shown in the reader's language, and its
/// English, which the command line prints and which stands in where the message cannot be translated.
/// </summary>
/// <remarks>
/// <para>
/// The message travels as a Runic Translations text reference (<see cref="TranslationReference"/>, transport version 2):
/// the page resolves it with the generated transport module, the daemon with <see cref="TranslationReferenceResolver"/>,
/// and a reference stored months ago is still shown in the language of the day it is read.
/// </para>
/// <para>
/// Stored, it is the reference's JSON, whose fallback text is the English. Records written before references existed
/// hold English text alone, which reads as <see cref="Plain"/> text and is shown as it is.
/// </para>
/// </remarks>
/// <param name="English">The text in English.</param>
/// <param name="Reference">The encoded text reference, or null for text that is not a message, such as a tool's output.</param>
[JsonConverter(typeof(DisplayTextJsonConverter))]
public sealed record DisplayText(string English, string? Reference)
{
    private static readonly Lazy<ITranslationSnapshot> EnglishSnapshot = new(() => DashboardTextCatalog.CreateManager("en").Current);

    /// <summary>Text that is not a message, such as a tool's output or a name; it is shown in every language as it is.</summary>
    public static DisplayText Plain(string text) => new(text, null);

    /// <summary>
    /// A failure as a person reads it: a <see cref="ManagerException"/>'s text, or any other exception's message, which
    /// .NET writes in English.
    /// </summary>
    public static DisplayText From(Exception error) => error is ManagerException manager ? manager.Text : Plain(error.Message);

    /// <summary>
    /// The catalogue's message <paramref name="key"/> with its inputs. The English comes from the catalogue, so the
    /// message is written once.
    /// </summary>
    /// <param name="key">The message's generated key, such as <c>stored_retain_home_generation</c>.</param>
    /// <param name="inputs">
    /// The inputs by name: text, a number, a list of text (<c>:runic:list</c>), or another <see cref="DisplayText"/>,
    /// whose message is translated with it.
    /// </param>
    /// <exception cref="ArgumentException">The catalogue has no such message, or the inputs do not fit it.</exception>
    public static DisplayText Of(string key, params ReadOnlySpan<(string Name, object Value)> inputs)
    {
        var arguments = new Dictionary<string, TranslationReferenceArgument>(inputs.Length, StringComparer.Ordinal);
        foreach (var (name, value) in inputs) arguments[name] = Argument(value);
        var unresolved = new TranslationReference(DashboardTextCatalog.CatalogId, DashboardTextCatalog.ContractFingerprint, key, arguments, null);
        var english = TranslationReferenceResolver.Resolve(EnglishSnapshot.Value, DashboardTextCatalog.ContractFingerprint, unresolved);
        if (english.Outcome != TranslationReferenceOutcome.Translated || english.Text is not { } text)
            throw new ArgumentException($"{key} is not a message of the catalogue with the inputs {string.Join(", ", arguments.Keys)}: {english.Outcome}.", nameof(key));
        var reference = new TranslationReference(DashboardTextCatalog.CatalogId, DashboardTextCatalog.ContractFingerprint, key, arguments, text);
        return new DisplayText(text, JsonSerializer.Serialize(reference, TranslationReferenceJsonContext.Default.TranslationReference));
    }

    private static TranslationReferenceArgument Argument(object value) => value switch
    {
        DisplayText { Reference: { } reference } => new(TranslationReference.Parse(reference)),
        DisplayText plain => new(TextArgumentType.String, plain.English),
        string text => new(TextArgumentType.String, text),
        IReadOnlyList<string> items => new(items),
        // The stored messages take numbers as text, as they were filled into English.
        IFormattable number => new(TextArgumentType.String, number.ToString(null, CultureInfo.InvariantCulture)),
        _ => throw new ArgumentException($"{value.GetType()} is not a message input."),
    };

    /// <summary>The text in <paramref name="snapshot"/>'s language; the English when the message cannot be translated.</summary>
    public string In(ITranslationSnapshot snapshot)
    {
        if (Reference is null) return English;
        var resolved = TranslationReferenceResolver.Resolve(snapshot, DashboardTextCatalog.ContractFingerprint, TranslationReference.Parse(Reference));
        return resolved.Text ?? English;
    }

    /// <summary>The English.</summary>
    public override string ToString() => English;
}

/// <summary>
/// Stores <see cref="DisplayText"/> as its text reference's JSON, and reads English text without a reference, as
/// records written before references existed hold it.
/// </summary>
public sealed class DisplayTextJsonConverter : JsonConverter<DisplayText>
{
    public override DisplayText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return DisplayText.Plain(reader.GetString()!);
            case JsonTokenType.StartObject:
            {
                using var document = JsonDocument.ParseValue(ref reader);
                var json = document.RootElement.GetRawText();
                var reference = TranslationReference.Parse(json);
                return new DisplayText(reference.FallbackText ?? "", json);
            }
            default:
                throw new JsonException($"Expected text or a text reference, not {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, DisplayText value, JsonSerializerOptions options)
    {
        if (value.Reference is { } reference) writer.WriteRawValue(reference, skipInputValidation: true);
        else writer.WriteStringValue(value.English);
    }
}
