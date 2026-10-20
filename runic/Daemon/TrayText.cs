using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NixManager.Dashboard;
using NixManager.Domain;
using Runic.Translations;

namespace NixManager.Daemon;

/// <summary>
/// The text the daemon shows itself, the status icon, its menu and the notifications, in the dashboard's language
/// (<c>t</c>, <c>tn</c> and <c>list</c> in <c>src/i18n.ts</c> as the Bun tray uses them).
/// </summary>
/// <remarks>
/// The messages are the dashboard's own Runic Translations catalogue (<c>Frontend/translations</c>), which the Vite
/// plugin compiles for the page and Runic.Translations.Build for this assembly as <see cref="DashboardText"/>: the
/// daemon's in <c>tray/</c>, and the text the backend stores in English, such as step labels and errors, in
/// <c>stored/</c>. One catalogue serves the page and the daemon, so a translation is written once. The generated
/// catalogue reads a language when it is first used, and English stored text is found through the importer's
/// base-text index (<c>stored/base-text.json</c>), as the page's <c>stored()</c> finds it.
/// </remarks>
public sealed class TrayText
{
    private static readonly ConcurrentDictionary<string, TrayText> Cache = new(StringComparer.Ordinal);
    private static readonly Lazy<StoredIndex> Index = new(StoredIndex.Load);
    private readonly ITranslationManager _manager;
    private readonly NumberFormatInfo _numbers;

    private TrayText(string locale)
    {
        Locale = locale;
        // Only the separators differ, as Intl.NumberFormat writes them for the page.
        _numbers = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        _numbers.NumberDecimalSeparator = locale == "de" ? "," : ".";
        _numbers.NumberGroupSeparator = locale == "de" ? "." : ",";
        _manager = DashboardTextCatalog.CreateManager(locale);
        Messages = new DashboardText(_manager).Messages;
    }

    /// <summary>English, which the command line and the tests read.</summary>
    public static TrayText English => For("en");

    /// <summary>The text in <paramref name="locale"/>, <c>en</c> or <c>de</c>; English for any other.</summary>
    public static TrayText For(string locale) =>
        Cache.GetOrAdd(DashboardPreferences.Locales.Contains(locale) ? locale : "en", static key => new TrayText(key));

    /// <summary>The language of this text, <c>en</c> or <c>de</c>.</summary>
    public string Locale { get; }

    /// <summary>The catalogue's messages; the daemon's are <c>tray_*</c>.</summary>
    public DashboardTextMessages Messages { get; }

    /// <summary>A conjunction in this language, such as "a, b, and c" (<c>:runic:list</c>).</summary>
    public string List(IReadOnlyList<string> items) => Messages.common_list(items);

    /// <summary>A size in binary units with the language's decimal separator (<c>bytes</c> in <c>src/summary.ts</c>).</summary>
    public string Bytes(double? value, bool signed = false) => Reviews.ReviewSummary.Bytes(value, signed, _numbers);

    /// <summary>Text the backend wrote for people, such as a step label or an error, in this language.</summary>
    /// <remarks>Text that is not a message, such as a tool's output or a record from before text references, stays as it is.</remarks>
    public string Show(DisplayText? text) => text?.In(_manager.Current) ?? "";

    /// <summary>
    /// Text the backend stored in English, such as a step label or an error, in this language; text without a
    /// translation stays as it is. A value filled into it can be stored text itself, like the step label in
    /// "{label} failed".
    /// </summary>
    public string Stored(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var index = Index.Value;
        if (index.Exact.TryGetValue(text, out var exact)) return Format(exact, []) ?? text;
        if (Locale == "en") return text;
        foreach (var pattern in index.Patterns)
        {
            var match = pattern.Expression.Match(text);
            if (!match.Success) continue;
            var values = pattern.Names.Select((name, i) => TextArgument.CreateRmf2(name, new TextArgument("_", Stored(match.Groups[i + 1].Value))));
            return Format(pattern.Key, [.. values]) ?? text;
        }
        return text;
    }

    /// <summary>A stored message by its key, or null when the catalogue has none.</summary>
    private string? Format(string key, ReadOnlySpan<TextArgument> arguments)
    {
        var snapshot = _manager.Current;
        return snapshot.TryGetKey(key, out var found) ? snapshot.Format(found, arguments) : null;
    }

    /// <summary>The stored messages by their English text, as the page's <c>stored()</c> indexes them.</summary>
    private sealed record StoredIndex(IReadOnlyDictionary<string, string> Exact, IReadOnlyList<StoredPattern> Patterns)
    {
        private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.CultureInvariant);

        /// <summary>Reads <c>stored/base-text.json</c>, English text to message key, embedded in the assembly.</summary>
        public static StoredIndex Load()
        {
            const string name = "NixManager.Translations.stored.base-text.json";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidDataException($"{name} is not embedded.");
            return Create(JsonSerializer.Deserialize(stream, Operations.ManagerJson.Default.DictionaryStringString)
                ?? throw new InvalidDataException($"{name} is not an object."));
        }

        public static StoredIndex Create(IReadOnlyDictionary<string, string> english)
        {
            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var patterns = new List<StoredPattern>();
            foreach (var (text, key) in english)
            {
                var names = Placeholder.Matches(text).Select(match => match.Groups[1].Value).ToList();
                if (names.Count == 0)
                {
                    exact.TryAdd(text, key);
                    continue;
                }
                var source = string.Join(@"([\s\S]+?)", Placeholder.Split(text).Where((_, i) => i % 2 == 0).Select(Regex.Escape));
                patterns.Add(new StoredPattern(key, new Regex($"^{source}$", RegexOptions.CultureInvariant), names,
                    Placeholder.Replace(text, "").Length));
            }
            // The most specific message wins, so "{label} failed" does not take "Building {name} failed".
            return new StoredIndex(exact, [.. patterns.OrderByDescending(pattern => pattern.Literal)]);
        }
    }

    private sealed record StoredPattern(string Key, Regex Expression, IReadOnlyList<string> Names, int Literal);
}
