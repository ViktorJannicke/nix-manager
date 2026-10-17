using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Operations;

namespace NixManager.Dashboard;

/// <summary>The dashboard's preferences as stored, with the defaults of <c>src/preferences.ts</c> filled in.</summary>
/// <param name="Theme"><c>system</c>, <c>light</c> or <c>dark</c>.</param>
/// <param name="Language"><c>system</c>, <c>en</c> or <c>de</c>.</param>
/// <param name="Detail"><c>simple</c> or <c>detailed</c>; the first choice when none is stored.</param>
/// <param name="PrepareUpdates">Prepare updates the tray finds while plugged in and not busy.</param>
/// <param name="CleanUp">Remove older restore points of the apps once a week.</param>
/// <param name="History">Commit the files each applied change writes.</param>
/// <param name="Backup">Push those commits when the branch has an upstream.</param>
/// <param name="AppDetails">Name, describe and picture apps with Flathub's catalogue; on unless turned off.</param>
public sealed record DashboardPreferenceValues(string Theme, string Language, string Detail, bool PrepareUpdates, bool CleanUp,
    bool History, bool Backup, bool AppDetails);

/// <summary>
/// The dashboard's preferences in <c>preferences.json</c> (<c>src/preferences.ts</c>), shared with the Bun version,
/// the backend's first choice (<see cref="Preferences"/>) and the background tasks, which read
/// <c>prepareUpdates</c>, <c>cleanUp</c>, <c>history</c> and <c>backup</c> from the same file. Members this
/// dashboard does not know are kept.
/// </summary>
public static class DashboardPreferences
{
    public static readonly IReadOnlyList<string> Themes = ["system", "light", "dark"];
    public static readonly IReadOnlyList<string> Languages = ["system", "en", "de"];
    public static readonly IReadOnlyList<string> Details = ["simple", "detailed"];
    public static readonly IReadOnlyList<string> Locales = ["en", "de"];

    public static string PathOf(Config config) => Path.Combine(config.StateDir, "preferences.json");

    /// <summary>The stored preferences; writes the first choice when there is none yet, as <c>readPreferences</c> does.</summary>
    public static DashboardPreferenceValues Read(Config config)
    {
        var stored = Preferences.Read(config);
        return new DashboardPreferenceValues(Known(stored, "theme", Themes), Known(stored, "language", Languages),
            Known(stored, "detail", Details), Flag(stored, "prepareUpdates", false), Flag(stored, "cleanUp", false),
            Flag(stored, "history", false), Flag(stored, "backup", false), Flag(stored, "appDetails", true));
    }

    /// <exception cref="ManagerException">A value is not one of the known choices.</exception>
    public static void Save(Config config, PreferenceChange change)
    {
        if (change.Theme is { } theme && !Themes.Contains(theme)) throw new ManagerException($"Unknown theme: {theme}.");
        if (change.Language is { } language && !Languages.Contains(language)) throw new ManagerException($"Unknown language: {language}.");
        if (change.Detail is { } detail && !Details.Contains(detail)) throw new ManagerException($"Unknown detail level: {detail}.");
        // Writes the backend's first choice when there is none yet, as readPreferences does.
        var next = Preferences.Read(config);
        if (change.Theme is not null) next["theme"] = change.Theme;
        if (change.Language is not null) next["language"] = change.Language;
        if (change.Detail is not null) next["detail"] = change.Detail;
        if (change.PrepareUpdates is { } prepareUpdates) next["prepareUpdates"] = prepareUpdates;
        if (change.CleanUp is { } cleanUp) next["cleanUp"] = cleanUp;
        if (change.History is { } history) next["history"] = history;
        if (change.Backup is { } backup) next["backup"] = backup;
        if (change.AppDetails is { } appDetails) next["appDetails"] = appDetails;
        OperationStore.CreatePrivateDirectory(config.StateDir);
        OperationStore.AtomicJson(PathOf(config), next.ToJsonString(new JsonSerializerOptions(ManagerJson.Default.Options)));
    }

    /// <summary>The chosen language, or the desktop's from the locale variables (<c>resolveLocale</c>).</summary>
    public static string ResolveLocale(string language, Func<string, string?> environment) =>
        language != "system" && Locales.Contains(language)
            ? language
            : LocaleFrom([environment("LC_ALL"), environment("LC_MESSAGES"), environment("LANG")]);

    /// <summary>The first supported language among tags like <c>de-DE</c>, <c>de_DE.UTF-8</c> or <c>C</c>; English otherwise.</summary>
    public static string LocaleFrom(IEnumerable<string?> tags)
    {
        foreach (var tag in tags)
        {
            var language = tag?.Split(['-', '_', '.', '@', ':'])[0].ToLowerInvariant();
            if (string.IsNullOrEmpty(language) || language is "c" or "posix") continue;
            return Locales.Contains(language) ? language : "en";
        }
        return "en";
    }

    private static string Known(JsonObject stored, string name, IReadOnlyList<string> choices) =>
        stored[name] is JsonValue value && value.TryGetValue<string>(out var text) && choices.Contains(text) ? text : choices[0];

    private static bool Flag(JsonObject stored, string name, bool fallback) =>
        stored[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
}
