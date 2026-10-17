using System.Text.RegularExpressions;
using NixManager.Daemon;

namespace NixManager.Tests;

/// <summary>
/// Text the backend sends the dashboard, the setup assistant or the tray in English is translated where it is shown,
/// through the stored catalogue (Frontend/translations/stored, which <see cref="TrayText.Stored"/> reads as the page's
/// <c>stored()</c> does). These tests read the backend's sources for the errors its requests answer with, so an error
/// added without German fails here instead of reaching a German page in English.
/// </summary>
public sealed partial class TranslationCoverageTests
{
    private static readonly TrayText German = TrayText.For("de");

    /// <summary>The sources whose errors reach the page: its requests, and what they call that fails on its own.</summary>
    private static readonly string[] PageSources =
    [
        "ViewModels",
        "Dashboard",
        "Setup/SetupAssistantService.cs",
        "Reviews/ReviewService.cs",
        "Software/Apps.cs",
        "Execution/PrivilegedCommandRunner.cs",
        "Operations/OperationService.cs",
        "Operations/OperationStore.cs",
    ];

    /// <summary>
    /// Errors that stay English, each with why: they answer requests the page never makes, or they carry a message from
    /// the system, which is English anyway, as in the Bun version.
    /// </summary>
    private static readonly Dictionary<string, string> English = new()
    {
        ["Unknown theme: x."] = "the page offers only the known themes",
        ["Unknown language: x."] = "the page offers only the known languages",
        ["Unknown detail level: x."] = "the page offers only the known detail levels",
        ["x: unexpected result x."] = "a Polkit result the platform does not define",
        ["Save update report: x"] = "the system's I/O error",
    };

    [Test]
    public async Task Every_error_the_pages_requests_answer_with_is_translated()
    {
        var untranslated = new List<string>();
        foreach (var (file, line, text) in Errors())
        {
            if (English.ContainsKey(text) || German.Stored(text) != text) continue;
            untranslated.Add($"{file}:{line}: {text}");
        }
        await Assert.That(untranslated).IsEmpty();
        // The list holds only errors that still exist.
        await Assert.That(English.Keys.Except(Errors().Select(error => error.Text))).IsEmpty();
    }

    [Test]
    public async Task Holding_changes_that_are_no_longer_prepared_says_so_in_German()
    {
        await Assert.That(German.Stored("Those changes are no longer being prepared."))
            .IsEqualTo("Diese Änderungen werden nicht mehr vorbereitet.");
        await Assert.That(TrayText.English.Stored("Those changes are no longer being prepared."))
            .IsEqualTo("Those changes are no longer being prepared.");
        // A value inside is kept, and translated itself where it is stored text.
        var step = German.Stored("Activate system");
        await Assert.That(step).IsNotEqualTo("Activate system");
        await Assert.That(German.Stored("Activate system: Polkit is unavailable (no agent). An authentication agent and pkexec are required."))
            .IsEqualTo($"{step}: Polkit ist nicht verfügbar (no agent). Ein Authentifizierungsprogramm und pkexec werden benötigt.");
    }

    [Test]
    public async Task Every_catalogue_has_German_for_each_English_message()
    {
        foreach (var catalogue in new[] { "", "stored", "tray" })
        {
            var directory = Path.Combine(Root, "Frontend", "translations", catalogue);
            await Assert.That(Keys(Path.Combine(directory, "de.rmf2"))).IsEquivalentTo(Keys(Path.Combine(directory, "en.rmf2")))
                .Because($"translations/{catalogue}");
        }
    }

    /// <summary>The literal errors of <see cref="PageSources"/>, with each interpolated value as "x".</summary>
    private static IEnumerable<(string File, int Line, string Text)> Errors()
    {
        foreach (var source in PageSources)
        {
            var path = Path.Combine(Root, source);
            var files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.cs") : [path];
            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!Raises().IsMatch(lines[i])) continue;
                    foreach (Match literal in Literal().Matches(lines[i]))
                    {
                        // A literal with a conditional literal inside, which this simple reading cannot take apart.
                        if (literal.Groups[2].Value.Contains("{(", StringComparison.Ordinal)) continue;
                        var text = literal.Groups[1].Success ? Hole().Replace(literal.Groups[2].Value, "x") : literal.Groups[2].Value;
                        // Words, not names, keys or separators.
                        if (Words().IsMatch(text)) yield return (Path.GetRelativePath(Root, file), i + 1, text);
                    }
                }
            }
        }
    }

    private static HashSet<string> Keys(string file)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? group = null;
        foreach (var line in File.ReadAllLines(file))
        {
            if (GroupStart().Match(line) is { Success: true } start) group = start.Groups[1].Value;
            else if (line == "}") group = null;
            else if (Entry().Match(line) is { Success: true } entry && (entry.Groups[1].Length == 0) == (group is null))
                keys.Add(group is null ? entry.Groups[2].Value : $"{group}_{entry.Groups[2].Value}");
        }
        return keys;
    }

    /// <summary>The application's source folder (runic/), above the test's output.</summary>
    internal static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "NixManager.csproj"))) return directory.FullName;
        throw new InvalidOperationException("The application's sources are not above the tests.");
    }

    // A line that raises an error or sets the error a View shows.
    [GeneratedRegex(@"ManagerException\(|SoftwareFailure\(|\bError = ")]
    private static partial Regex Raises();

    // A regular or interpolated string literal without nested literals.
    [GeneratedRegex(@"(\$)?""((?:[^""\\\n]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Hole();

    [GeneratedRegex(@"\p{L}{2,} \p{L}{2,}")]
    private static partial Regex Words();

    [GeneratedRegex(@"^([A-Za-z_]\w*) \{$")]
    private static partial Regex GroupStart();

    [GeneratedRegex(@"^(  )?([A-Za-z_]\w*) =")]
    private static partial Regex Entry();
}
