using System.Text.Json;
using System.Text.RegularExpressions;
using NixManager.Daemon;
using NixManager.Domain;
using NixManager.Operations;

namespace NixManager.Tests;

/// <summary>
/// Text the backend writes for people is a text reference to a message of the dashboard's catalogue
/// (<see cref="DisplayText"/>), shown in the reader's language by the page and by <see cref="TrayText.Show"/>. These
/// tests read the backend's sources for the messages it names, so a message that is missing, or named with other
/// inputs than it has, fails here instead of when the backend first writes it.
/// </summary>
public sealed partial class TranslationCoverageTests
{
    private static readonly TrayText German = TrayText.For("de");

    [Test]
    public async Task Every_message_the_backend_names_is_in_the_catalogue_with_its_inputs()
    {
        var messages = Messages();
        var calls = Calls().ToList();
        // The scan finds the calls: Plan.cs alone names a few dozen messages.
        await Assert.That(calls.Count).IsGreaterThan(300);
        var problems = new List<string>();
        foreach (var (file, line, keys, inputs) in calls)
            foreach (var key in keys)
            {
                if (!messages.TryGetValue(key, out var placeholders)) problems.Add($"{file}:{line}: {key} is not in the catalogue");
                else if (inputs is not null && !placeholders.SetEquals(inputs))
                    problems.Add($"{file}:{line}: {key} takes {string.Join(", ", placeholders.Order())}, not {string.Join(", ", inputs.Order())}");
            }
        await Assert.That(problems).IsEmpty();
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

    [Test]
    public async Task A_message_is_shown_in_German_with_a_message_inside_translated_too()
    {
        var held = DisplayText.Of("stored_those_changes_are_no_longer_being_prepared");
        await Assert.That(German.Show(held)).IsEqualTo("Diese Änderungen werden nicht mehr vorbereitet.");
        await Assert.That(TrayText.English.Show(held)).IsEqualTo("Those changes are no longer being prepared.");
        await Assert.That(held.English).IsEqualTo("Those changes are no longer being prepared.");

        // The step label inside is a message itself; a value from the system stays as it is.
        var step = DisplayText.Of("stored_activate_system");
        var polkit = DisplayText.Of("stored_polkit_is_unavailable_an_authentication_agent_and", ("label", step), ("reason", "no agent"));
        await Assert.That(polkit.English).IsEqualTo("Activate system: Polkit is unavailable (no agent). An authentication agent and pkexec are required.");
        await Assert.That(German.Show(polkit))
            .IsEqualTo($"{German.Show(step)}: Polkit ist nicht verfügbar (no agent). Ein Authentifizierungsprogramm und pkexec werden benötigt.");
        await Assert.That(German.Show(step)).IsNotEqualTo("Activate system");
    }

    [Test]
    public async Task A_reference_survives_storing_and_a_record_without_one_reads_as_its_English()
    {
        var failed = DisplayText.Of("stored_failed", ("label", DisplayText.Of("stored_build_home_configuration")));
        var json = ManagerJson.Serialize(failed);
        await Assert.That(json).StartsWith("{");
        var read = ManagerJson.Deserialize<DisplayText>(json)!;
        await Assert.That(read).IsEqualTo(failed);
        await Assert.That(German.Show(read)).IsEqualTo("Fehlgeschlagen: Benutzerkonfiguration bauen");

        // Records written before text references hold English, which every language shows as it is.
        var legacy = ManagerJson.Deserialize<DisplayText>("\"Build home configuration failed\"")!;
        await Assert.That(legacy).IsEqualTo(DisplayText.Plain("Build home configuration failed"));
        await Assert.That(German.Show(legacy)).IsEqualTo("Build home configuration failed");
        await Assert.That(ManagerJson.Serialize(legacy)).IsEqualTo("\"Build home configuration failed\"");
    }

    [Test]
    public async Task A_message_named_wrongly_fails_where_it_is_written()
    {
        await Assert.That(() => DisplayText.Of("stored_no_such_message")).Throws<ArgumentException>();
        await Assert.That(() => DisplayText.Of("stored_failed", ("step", "x"))).Throws<ArgumentException>();
    }

    /// <summary>The placeholders of every message of the catalogue, by generated key (<c>group_name</c>).</summary>
    private static Dictionary<string, HashSet<string>> Messages()
    {
        var messages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var catalogue in new[] { "", "stored", "tray" })
        {
            string? group = null;
            HashSet<string>? current = null;
            foreach (var line in File.ReadAllLines(Path.Combine(Root, "Frontend", "translations", catalogue, "en.rmf2")))
            {
                if (GroupStart().Match(line) is { Success: true } start) (group, current) = (start.Groups[1].Value, null);
                else if (line == "}") (group, current) = (null, null);
                else if (Entry().Match(line) is { Success: true } entry && (entry.Groups[1].Length == 0) == (group is null))
                {
                    var name = entry.Groups[2].Value;
                    var key = (catalogue, group) switch
                    {
                        ("", null) => name,
                        ("", _) => $"{group}_{name}",
                        _ => $"{catalogue}_{name}",
                    };
                    messages[key] = current = [];
                    Add(line[entry.Length..]);
                }
                else if (!line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith('@')) Add(line);

                void Add(string text)
                {
                    foreach (Match placeholder in Placeholder().Matches(text)) current?.Add(placeholder.Groups[1].Value);
                }
            }
        }
        return messages;
    }

    /// <summary>
    /// The calls <c>DisplayText.Of("key", ("input", value), ...)</c> in the application's sources: the keys named in
    /// the first argument (both of a conditional), and the input names, or null when they are not all written out.
    /// </summary>
    private static IEnumerable<(string File, int Line, IReadOnlyList<string> Keys, IReadOnlyList<string>? Inputs)> Calls()
    {
        var sources = Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
            .Where(file => Path.GetRelativePath(Root, file).Split(Path.DirectorySeparatorChar) is var parts
                && parts[0] is not ("NixManager.Tests" or "obj" or "bin"))
            .Order(StringComparer.Ordinal);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (Match call in Call().Matches(text))
            {
                var arguments = Arguments(text, call.Index + call.Length);
                if (arguments.Count == 0) continue;
                var keys = KeyLiteral().Matches(arguments[0]).Select(match => match.Groups[1].Value).ToList();
                // A key chosen at run time, such as in DisplayText itself.
                if (keys.Count == 0) continue;
                var names = arguments.Skip(1).Select(argument => InputName().Match(argument)).ToList();
                var inputs = names.All(name => name.Success) ? names.Select(name => name.Groups[1].Value).ToList() : null;
                yield return (Path.GetRelativePath(Root, file), text[..call.Index].Count(c => c == '\n') + 1, keys, inputs);
            }
        }
    }

    /// <summary>The top-level arguments of the call whose argument list starts at <paramref name="start"/>.</summary>
    private static List<string> Arguments(string text, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var from = start;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                // A literal, verbatim or not; the braces and parentheses inside do not count.
                var verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 1] == '$' && text[i - 2] == '@'));
                for (i++; i < text.Length; i++)
                {
                    if (!verbatim && text[i] == '\\') i++;
                    else if (text[i] == '"' && !(verbatim && i + 1 < text.Length && text[i + 1] == '"')) break;
                    else if (verbatim && text[i] == '"') i++;
                }
            }
            else if (c == '\'') i = text.IndexOf('\'', i + (text[i + 1] == '\\' ? 3 : 2));
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth-- == 0)
                {
                    if (text[from..i].Trim().Length > 0) arguments.Add(text[from..i]);
                    return arguments;
                }
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(text[from..i]);
                from = i + 1;
            }
        }
        return [];
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

    [GeneratedRegex(@"\bDisplayText\.Of\(")]
    private static partial Regex Call();

    // A key literal, such as "stored_failed"; not a value the condition of a conditional compares with, as "boot" in
    // DisplayText.Of(mode == "boot" ? "stored_a" : "stored_b").
    [GeneratedRegex(@"(?<!(?:==|!=|\bis|\bor|\band|\bnot)\s*)""([a-z][a-z0-9_]*)""")]
    private static partial Regex KeyLiteral();

    // An input, such as ("label", step).
    [GeneratedRegex(@"^\s*\(\s*""(\w+)""\s*,", RegexOptions.Singleline)]
    private static partial Regex InputName();

    [GeneratedRegex(@"\{\$(\w+)")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^([A-Za-z_]\w*) \{$")]
    private static partial Regex GroupStart();

    [GeneratedRegex(@"^(  )?([A-Za-z_]\w*) =")]
    private static partial Regex Entry();
}
