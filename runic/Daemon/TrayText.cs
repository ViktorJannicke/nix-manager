using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NixManager.Dashboard;

namespace NixManager.Daemon;

/// <summary>
/// The text the daemon shows itself, the status icon, its menu and the notifications, in the dashboard's language
/// (<c>t</c>, <c>tn</c> and <c>list</c> in <c>src/i18n.ts</c> as the Bun tray uses them).
/// </summary>
/// <remarks>
/// <para>
/// The messages are the dashboard's own Runic Translations catalogue (<c>Frontend/translations</c>), which the Vite
/// plugin compiles and checks for the page: the daemon's in <c>tray/</c>, and the text the backend stores in English,
/// such as step labels and errors, in <c>stored/</c>. One catalogue serves the page and the daemon, so a translation is
/// written once.
/// </para>
/// <para>
/// The daemon reads those files, embedded in the assembly, with <see cref="TrayCatalog"/>, a small reader for the part
/// of RMF2 they use, instead of the C# that Runic.Translations.Build generates for the whole catalogue: that code builds
/// the catalogue in one method of some 15,000 constructions, and compiling it took the idle daemon from about 85 to 140
/// MiB and doubled the time to its status icon. <see cref="TrayMessages"/> has the generated readable names, so moving
/// to the generated class, once it is cheaper, changes this file and nothing that uses it.
/// </para>
/// </remarks>
public sealed class TrayText
{
    private static readonly ConcurrentDictionary<string, TrayText> Cache = new(StringComparer.Ordinal);
    private static readonly Lazy<StoredIndex> Index = new(() => StoredIndex.Create(TrayCatalog.Load("stored", "en")));
    private readonly TrayCatalog _stored;
    private readonly NumberFormatInfo _numbers;

    private TrayText(string locale)
    {
        Locale = locale;
        // Only the separators differ, and the runtime may have no culture data (invariant globalization).
        _numbers = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        _numbers.NumberDecimalSeparator = locale == "de" ? "," : ".";
        _numbers.NumberGroupSeparator = locale == "de" ? "." : ",";
        Messages = new TrayMessages(TrayCatalog.Load("tray", locale), _numbers);
        _stored = TrayCatalog.Load("stored", locale);
    }

    /// <summary>English, which the command line and the tests read.</summary>
    public static TrayText English => For("en");

    /// <summary>The text in <paramref name="locale"/>, <c>en</c> or <c>de</c>; English for any other.</summary>
    public static TrayText For(string locale) =>
        Cache.GetOrAdd(DashboardPreferences.Locales.Contains(locale) ? locale : "en", static key => new TrayText(key));

    /// <summary>The language of this text, <c>en</c> or <c>de</c>.</summary>
    public string Locale { get; }

    /// <summary>The daemon's messages (<c>tray/</c>).</summary>
    public TrayMessages Messages { get; }

    /// <summary>A conjunction as <c>Intl.ListFormat</c> writes it: "a, b and c", "a, b und c".</summary>
    public string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} {(Locale == "de" ? "und" : "and")} {items[^1]}",
    };

    /// <summary>A size in binary units with the language's decimal separator (<c>bytes</c> in <c>src/summary.ts</c>).</summary>
    public string Bytes(double? value, bool signed = false) => Reviews.ReviewSummary.Bytes(value, signed, _numbers);

    /// <summary>
    /// Text the backend stored in English, such as a step label or an error, in this language; text without a
    /// translation stays as it is. A value filled into it can be stored text itself, like the step label in
    /// "{label} failed".
    /// </summary>
    public string Stored(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var index = Index.Value;
        if (index.Exact.TryGetValue(text, out var exact)) return _stored.Format(exact, _numbers);
        if (Locale == "en") return text;
        foreach (var pattern in index.Patterns)
        {
            var match = pattern.Expression.Match(text);
            if (!match.Success) continue;
            var values = pattern.Names.Select((name, i) => (name, (object)Stored(match.Groups[i + 1].Value))).ToArray();
            return _stored.Format(pattern.Key, _numbers, values);
        }
        return text;
    }

    /// <summary>The stored messages by their English text, as the page's <c>stored()</c> indexes them.</summary>
    private sealed record StoredIndex(IReadOnlyDictionary<string, string> Exact, IReadOnlyList<StoredPattern> Patterns)
    {
        public static StoredIndex Create(TrayCatalog english)
        {
            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var patterns = new List<StoredPattern>();
            foreach (var (key, message) in english.Messages)
            {
                if (message.Variants.Count != 1) continue;
                var parts = message.Variants[0].Parts;
                var names = parts.OfType<TrayCatalog.Placeholder>().Select(part => part.Name).ToList();
                var literal = string.Concat(parts.OfType<TrayCatalog.Literal>().Select(part => part.Text));
                if (names.Count == 0)
                {
                    exact.TryAdd(literal, key);
                    continue;
                }
                var source = string.Concat(parts.Select(part => part is TrayCatalog.Literal text ? Regex.Escape(text.Text) : @"([\s\S]+?)"));
                patterns.Add(new StoredPattern(key, new Regex($"^{source}$", RegexOptions.CultureInvariant), names, literal.Length));
            }
            // The most specific message wins, so "{label} failed" does not take "Building {name} failed".
            return new StoredIndex(exact, [.. patterns.OrderByDescending(pattern => pattern.Literal)]);
        }
    }

    private sealed record StoredPattern(string Key, Regex Expression, IReadOnlyList<string> Names, int Literal);
}

/// <summary>
/// The daemon's messages in <c>Frontend/translations/tray</c>, named as Runic.Translations.Build's readable surface
/// names them (<c>DashboardText.Messages.tray_open</c>), so the generated class can replace this one.
/// </summary>
#pragma warning disable IDE1006 // The generated readable names.
public sealed class TrayMessages(TrayCatalog catalog, NumberFormatInfo numbers)
{
    /// <summary>Every key this class formats, which the tests check against both languages.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "open", "check", "prepare", "quit", "checks_failed", "no_updates", "updates_available", "needs_attention",
        "operation_failed", "updates_ready", "nothing_changes", "apply_now", "apply_on_boot", "space_freed", "freed",
        "older_removed", "newest_kept", "ready_to_review", "build_finished", "finished", "completed_for", "more",
        "installs", "removes", "changes_settings", "updates_packages", "adds", "adds_packages", "removes_count",
        "removes_packages", "home_first_time", "system_new", "everything_new", "nothing_you_use", "sources",
        "needs_space", "frees_space", "downloaded", "restart_kernel",
    ];

    private string F(string key, params (string, object)[] values) => catalog.Format(key, numbers, values);

    public string tray_open => F("open");
    public string tray_check => F("check");
    public string tray_prepare => F("prepare");
    public string tray_quit => F("quit");
    public string tray_checks_failed => F("checks_failed");
    public string tray_no_updates => F("no_updates");
    public string tray_updates_available(long count) => F("updates_available", ("count", count));
    public string tray_needs_attention => F("needs_attention");
    public string tray_operation_failed => F("operation_failed");
    public string tray_updates_ready => F("updates_ready");
    public string tray_nothing_changes(string summary) => F("nothing_changes", ("summary", summary));
    public string tray_apply_now => F("apply_now");
    public string tray_apply_on_boot => F("apply_on_boot");
    public string tray_space_freed => F("space_freed");
    public string tray_freed(string size) => F("freed", ("size", size));
    public string tray_older_removed => F("older_removed");
    public string tray_newest_kept(long count) => F("newest_kept", ("count", count));
    public string tray_ready_to_review => F("ready_to_review");
    public string tray_build_finished => F("build_finished");
    public string tray_finished => F("finished");
    public string tray_completed_for(string action, string host) => F("completed_for", ("action", action), ("host", host));
    public string tray_more(long count) => F("more", ("count", count));
    public string tray_installs(string names) => F("installs", ("names", names));
    public string tray_removes(string names) => F("removes", ("names", names));
    public string tray_changes_settings(long count) => F("changes_settings", ("count", count));
    public string tray_updates_packages(long count) => F("updates_packages", ("count", count));
    public string tray_adds(long count) => F("adds", ("count", count));
    public string tray_adds_packages(long count) => F("adds_packages", ("count", count));
    public string tray_removes_count(long count) => F("removes_count", ("count", count));
    public string tray_removes_packages(long count) => F("removes_packages", ("count", count));
    public string tray_home_first_time => F("home_first_time");
    public string tray_system_new => F("system_new");
    public string tray_everything_new => F("everything_new");
    public string tray_nothing_you_use => F("nothing_you_use");
    public string tray_sources(long count) => F("sources", ("count", count));
    public string tray_needs_space(string size) => F("needs_space", ("size", size));
    public string tray_frees_space(string size) => F("frees_space", ("size", size));
    public string tray_downloaded(string size) => F("downloaded", ("size", size));
    public string tray_restart_kernel => F("restart_kernel");
}
#pragma warning restore IDE1006

/// <summary>
/// One message file of the dashboard's catalogue, embedded from <c>Frontend/translations/&lt;group&gt;/&lt;locale&gt;.rmf2</c>,
/// read as far as <c>tray/</c> and <c>stored/</c> use RMF2: one-line patterns with <c>{$name}</c> and
/// <c>{$name :integer}</c> placeholders and <c>\{ \} \\</c> escapes, and a plural selection on one integer input with
/// <c>one</c> and <c>*</c> variants. The Translations compiler checks the files in the frontend build; anything beyond
/// this subset fails <see cref="Load"/> and the tests, rather than changing the text silently.
/// </summary>
public sealed class TrayCatalog
{
    public abstract record Part;
    public sealed record Literal(string Text) : Part;
    public sealed record Placeholder(string Name) : Part;
    public sealed record Variant(string? Key, IReadOnlyList<Part> Parts);
    /// <param name="Selector">The input a plural variant is chosen by, or null for one pattern.</param>
    /// <param name="Integers">Inputs formatted as integers, with the language's grouping.</param>
    public sealed record Message(string? Selector, IReadOnlyList<Variant> Variants, IReadOnlySet<string> Integers);

    private static readonly Regex Simple = new(@"^([a-z0-9_]+) = (.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Open = new(@"^([a-z0-9_]+) =$", RegexOptions.CultureInvariant);
    private static readonly Regex Input = new(@"^\s+\.input \{\$(\w+) :integer\}$", RegexOptions.CultureInvariant);
    private static readonly Regex Selection = new(@"^\s+\.match \$(\w+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Case = new(@"^\s+(one|\*) \{\{(.*)\}\}$", RegexOptions.CultureInvariant);
    private static readonly Regex Token = new(@"\\[{}\\]|\{\$(\w+)( :integer)?\}", RegexOptions.CultureInvariant);

    private TrayCatalog(IReadOnlyDictionary<string, Message> messages) => Messages = messages;

    public IReadOnlyDictionary<string, Message> Messages { get; }

    /// <summary>Reads <c>&lt;group&gt;/&lt;locale&gt;.rmf2</c> from the assembly.</summary>
    /// <exception cref="InvalidDataException">The file uses RMF2 beyond the subset read here.</exception>
    public static TrayCatalog Load(string group, string locale)
    {
        var name = $"NixManager.Translations.{group}.{locale}.rmf2";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"{name} is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd(), name);
    }

    /// <exception cref="InvalidDataException">The source uses RMF2 beyond the subset read here.</exception>
    public static TrayCatalog Parse(string source, string name)
    {
        var messages = new Dictionary<string, Message>(StringComparer.Ordinal);
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('@')) continue;
            if (Simple.Match(line) is { Success: true } simple)
            {
                var integers = new HashSet<string>(StringComparer.Ordinal);
                messages[simple.Groups[1].Value] = new Message(null, [new Variant(null, Pattern(simple.Groups[2].Value, integers))], integers);
                continue;
            }
            if (Open.Match(line) is not { Success: true } open) throw new InvalidDataException($"{name}:{i + 1}: not read: {line}");
            string? selector = null;
            var inputs = new HashSet<string>(StringComparer.Ordinal);
            var variants = new List<Variant>();
            while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ", StringComparison.Ordinal))
            {
                var part = lines[++i].TrimEnd('\r');
                if (Input.Match(part) is { Success: true } input) inputs.Add(input.Groups[1].Value);
                else if (Selection.Match(part) is { Success: true } match) selector = match.Groups[1].Value;
                else if (Case.Match(part) is { Success: true } variant)
                    variants.Add(new Variant(variant.Groups[1].Value, Pattern(variant.Groups[2].Value, inputs)));
                else throw new InvalidDataException($"{name}:{i + 1}: not read: {part}");
            }
            if (selector is null || !inputs.Contains(selector) || variants.Count == 0 || variants[^1].Key != "*")
                throw new InvalidDataException($"{name}: {open.Groups[1].Value} needs an integer .input, its .match and a * variant.");
            messages[open.Groups[1].Value] = new Message(selector, variants, inputs);
        }
        return new TrayCatalog(messages);
    }

    private static List<Part> Pattern(string text, HashSet<string> integers)
    {
        var parts = new List<Part>();
        var literal = new StringBuilder();
        var at = 0;
        foreach (Match token in Token.Matches(text))
        {
            literal.Append(text, at, token.Index - at);
            at = token.Index + token.Length;
            if (token.Value[0] == '\\')
            {
                literal.Append(token.Value[1]);
                continue;
            }
            if (literal.Length > 0) parts.Add(new Literal(literal.ToString()));
            literal.Clear();
            parts.Add(new Placeholder(token.Groups[1].Value));
            if (token.Groups[2].Success) integers.Add(token.Groups[1].Value);
        }
        literal.Append(text, at, text.Length - at);
        if (literal.Length > 0) parts.Add(new Literal(literal.ToString()));
        return parts;
    }

    /// <summary>
    /// Formats <paramref name="key"/> with <paramref name="values"/>: integers with the language's grouping, and the
    /// plural variant by the English and German rule (<c>one</c> for 1). An unknown key is returned as it is.
    /// </summary>
    public string Format(string key, NumberFormatInfo numbers, params (string Name, object Value)[] values)
    {
        if (!Messages.TryGetValue(key, out var message)) return key;
        object? Value(string name) => values.FirstOrDefault(value => value.Name == name).Value;
        var variant = message.Selector is { } selector
            ? message.Variants.FirstOrDefault(v => v.Key == "one" && Convert.ToInt64(Value(selector), CultureInfo.InvariantCulture) == 1)
              ?? message.Variants[^1]
            : message.Variants[0];
        var text = new StringBuilder();
        foreach (var part in variant.Parts)
            text.Append(part switch
            {
                Literal literal => literal.Text,
                Placeholder number when message.Integers.Contains(number.Name) =>
                    Convert.ToInt64(Value(number.Name), CultureInfo.InvariantCulture).ToString("N0", numbers),
                Placeholder placeholder => Convert.ToString(Value(placeholder.Name), CultureInfo.InvariantCulture),
                _ => "",
            });
        return text.ToString();
    }
}
