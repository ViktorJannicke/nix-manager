using System.Collections.Concurrent;
using System.Globalization;
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
/// daemon's in <c>tray/</c>, and the text the backend writes for people, such as step labels and errors, in
/// <c>stored/</c>, which travels as text references (<see cref="DisplayText"/>). One catalogue serves the page and
/// the daemon, so a translation is written once. The generated catalogue reads a language when it is first used.
/// </remarks>
public sealed class TrayText
{
    private static readonly ConcurrentDictionary<string, TrayText> Cache = new(StringComparer.Ordinal);
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
}
