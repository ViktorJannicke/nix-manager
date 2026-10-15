using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
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
/// The messages come from the dashboard's own Runic Translations catalogue (<c>Frontend/translations</c>, compiled into
/// C# as <see cref="DashboardText"/> by <c>Runic.Translations.Build</c>): the daemon's in <c>tray/</c>, and the text the
/// backend stores in English, such as step labels and errors, in <c>stored/</c>, which <see cref="Stored"/> finds by its
/// English text as the frontend's <c>stored()</c> does. One catalogue serves the page and the daemon, so a translation
/// is written once.
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
        _manager = DashboardTextCatalog.CreateManager(locale);
        // Only the decimal separator differs, and the runtime may have no culture data (invariant globalization).
        _numbers = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        _numbers.NumberDecimalSeparator = locale == "de" ? "," : ".";
        Messages = new DashboardText(_manager).Messages;
    }

    /// <summary>English, which the command line and the tests read.</summary>
    public static TrayText English => For("en");

    /// <summary>The text in <paramref name="locale"/>, <c>en</c> or <c>de</c>; English for any other.</summary>
    public static TrayText For(string locale) =>
        Cache.GetOrAdd(DashboardPreferences.Locales.Contains(locale) ? locale : "en", static key => new TrayText(key));

    /// <summary>The language of this text, <c>en</c> or <c>de</c>.</summary>
    public string Locale { get; }

    /// <summary>The catalogue's messages; the daemon's are the <c>tray_</c> ones.</summary>
    public DashboardTextMessages Messages { get; }

    /// <summary>A conjunction as <c>Intl.ListFormat</c> writes it: "a, b and c", "a, b und c".</summary>
    public string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} {(Locale == "de" ? "und" : "and")} {items[^1]}",
    };

    /// <summary>A size in binary units with the language's decimal separator (<c>bytes</c> in <c>src/summary.ts</c>).</summary>
    public string Bytes(double? value, bool signed = false) =>
        Reviews.ReviewSummary.Bytes(value, signed, _numbers);

    /// <summary>
    /// Text the backend stored in English, such as a step label or an error, in this language; text without a
    /// translation stays as it is. A value filled into it can be stored text itself, like the step label in
    /// "{label} failed".
    /// </summary>
    public string Stored(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var index = Index.Value;
        var snapshot = _manager.Current;
        if (index.Exact.TryGetValue(text, out var exact)) return Format(snapshot, exact, []);
        if (Locale == "en") return text;
        foreach (var pattern in index.Patterns)
        {
            var match = pattern.Expression.Match(text);
            if (!match.Success) continue;
            var values = pattern.Names.Select((name, i) => (name, Stored(match.Groups[i + 1].Value))).ToArray();
            return Format(snapshot, pattern.Key, values);
        }
        return text;
    }

    private static string Format(ITranslationSnapshot snapshot, string key, (string Name, string Value)[] values)
    {
        if (!snapshot.TryGetKey(key, out var translationKey)) return key;
        var arguments = values.Select(value => TextArgument.CreateRmf2(value.Name, new TextArgument("_", value.Value))).ToArray();
        return snapshot.Format(translationKey, arguments);
    }

    /// <summary>
    /// The stored messages by their English text, read from the catalogue's English source (embedded from
    /// <c>Frontend/translations/stored/en.rmf2</c>, which <c>scripts/import-bun-catalog.py</c> writes one message per line).
    /// </summary>
    private sealed record StoredIndex(IReadOnlyDictionary<string, string> Exact, IReadOnlyList<StoredPattern> Patterns)
    {
        private static readonly Regex Line = new(@"^([a-z0-9_]+) = (.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Placeholder = new(@"\{\$(\w+)\}", RegexOptions.CultureInvariant);

        public static StoredIndex Load()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NixManager.StoredEnglish.rmf2")
                ?? throw new InvalidOperationException("The stored catalogue is not embedded.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var patterns = new List<StoredPattern>();
            while (reader.ReadLine() is { } line)
            {
                if (Line.Match(line) is not { Success: true } match) continue;
                var key = "stored_" + match.Groups[1].Value;
                var pattern = match.Groups[2].Value;
                var names = Placeholder.Matches(pattern).Select(m => m.Groups[1].Value).ToList();
                if (names.Count == 0)
                {
                    exact.TryAdd(Unescape(pattern), key);
                    continue;
                }
                var pieces = Placeholder.Split(pattern).Where((_, i) => i % 2 == 0).Select(piece => Regex.Escape(Unescape(piece)));
                var literal = Placeholder.Replace(pattern, "").Length;
                patterns.Add(new StoredPattern(key, new Regex($"^{string.Join(@"([\s\S]+?)", pieces)}$", RegexOptions.CultureInvariant), names, literal));
            }
            // The most specific message wins, so "{label} failed" does not take "Building {name} failed".
            return new StoredIndex(exact, [.. patterns.OrderByDescending(pattern => pattern.Literal)]);
        }

        private static string Unescape(string pattern) =>
            pattern.Replace(@"\{", "{", StringComparison.Ordinal).Replace(@"\}", "}", StringComparison.Ordinal).Replace(@"\\", @"\", StringComparison.Ordinal);
    }

    private sealed record StoredPattern(string Key, Regex Expression, IReadOnlyList<string> Names, int Literal);
}
