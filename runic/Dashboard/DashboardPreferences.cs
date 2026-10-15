using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Operations;

namespace NixManager.Dashboard;

/// <summary>The theme and language of the dashboard as stored, with the defaults filled in.</summary>
public sealed record DashboardPreferenceValues(string Theme, string Language);

/// <summary>
/// The dashboard's preferences in <c>preferences.json</c> (<c>src/preferences.ts</c>), shared with the Bun version
/// and the backend's first choice (<see cref="Preferences"/>). Members this dashboard does not set are kept.
/// </summary>
public static class DashboardPreferences
{
    public static readonly IReadOnlyList<string> Themes = ["system", "light", "dark"];
    public static readonly IReadOnlyList<string> Languages = ["system", "en", "de"];
    public static readonly IReadOnlyList<string> Locales = ["en", "de"];

    public static string PathOf(Config config) => Path.Combine(config.StateDir, "preferences.json");

    public static DashboardPreferenceValues Read(Config config)
    {
        var stored = Stored(config);
        return new DashboardPreferenceValues(Known(stored, "theme", Themes), Known(stored, "language", Languages));
    }

    /// <exception cref="ManagerException">A value is not one of the known choices.</exception>
    public static void Save(Config config, PreferenceChange change)
    {
        if (change.Theme is { } theme && !Themes.Contains(theme)) throw new ManagerException($"Unknown theme: {theme}.");
        if (change.Language is { } language && !Languages.Contains(language)) throw new ManagerException($"Unknown language: {language}.");
        // Writes the backend's first choice when there is none yet, as readPreferences does.
        Preferences.Read(config);
        var next = Stored(config);
        if (change.Theme is not null) next["theme"] = change.Theme;
        if (change.Language is not null) next["language"] = change.Language;
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

    private static JsonObject Stored(Config config)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(PathOf(config))) as JsonObject ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }
}
