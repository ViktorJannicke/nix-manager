using System.Text.RegularExpressions;
using NixManager.Operations;

namespace NixManager.Explain;

/// <summary>
/// Common Nix failures in words someone who does not know Nix can act on (<c>src/explain.ts</c>). The text is
/// English, as the Bun version stores it, and translated where it is shown.
/// </summary>
public static partial class Explainer
{
    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]")]
    private static partial Regex Ansi();

    // Nix indents the lines of a message below its first one.
    [GeneratedRegex("^ {7}", RegexOptions.Multiline)]
    private static partial Regex NixIndent();

    /// <summary>The explanation for a failure's output, or null when no rule knows it.</summary>
    public static Explanation? Explain(string text)
    {
        var plain = NixIndent().Replace(Ansi().Replace(text, ""), "");
        foreach (var rule in Rules)
            if (rule(plain) is { } explanation) return explanation;
        return null;
    }

    /// <summary>The package name of a derivation name, as <c>builtins.parseDrvName</c> splits it (<c>packageName</c>).</summary>
    public static string PackageName(string name) =>
        PackageNameShape().Match(name) is { Success: true } match ? match.Groups[1].Value : name;

    private static readonly Func<string, Explanation?>[] Rules =
    [
        text => DiskFull().IsMatch(text)
            ? new Explanation
            {
                Title = "The disk is full",
                Body = "Nix ran out of space. Remove old restore points on the Restore points page, then try again.",
            }
            : null,
        text => AuthenticationCancelled().IsMatch(text)
            ? new Explanation
            {
                Title = "Authentication was cancelled",
                Body = "Changing the system needs an administrator password. Try again and enter it when asked.",
            }
            : null,
        text =>
        {
            var names = Unique(UnfreeRefusal().Matches(text).Select(match => PackageName(match.Groups[1].Value)));
            if (names.Count == 0) return null;
            return new Explanation
            {
                Title = names.Count == 1
                    ? $"{JoinNames(names)} is not free software"
                    : $"{JoinNames(names)} are not free software",
                Body = "Your configuration only allows software with a free license. Allow non-free software to install it, or remove it from your edits.",
                Fix = new Fix { Kind = "allow-unfree", Packages = names },
            };
        },
        text =>
        {
            var match = InsecureRefusal().Match(text);
            if (!match.Success) return null;
            var package = match.Groups[1].Value;
            // Each issue can be paragraphs long; its first sentence names it.
            var issues = IssueBullet().Split(match.Groups[2].Value)
                .Select(issue => FirstSentenceOf(Spaces().Replace(issue.Trim(), " ")))
                .Where(issue => issue.Length > 0)
                .ToList();
            return new Explanation
            {
                Title = $"{PackageName(package)} has known security problems",
                Body = issues.Count > 0
                    ? $"Nixpkgs marks {package} as insecure ({string.Join("; ", issues.Take(3))}). Choose another package, or allow this version in your configuration if you need it."
                    : $"Nixpkgs marks {package} as insecure. Choose another package, or allow this version in your configuration if you need it.",
            };
        },
        text =>
        {
            var match = PlatformRefusal().Match(text);
            return match.Success
                ? new Explanation
                {
                    Title = $"{PackageName(match.Groups[1].Value)} does not run on this computer",
                    Body = "The package is not made for this kind of processor or operating system. Remove it from your configuration.",
                }
                : null;
        },
        text =>
        {
            var match = BrokenRefusal().Match(text);
            return match.Success
                ? new Explanation
                {
                    Title = $"{PackageName(match.Groups[1].Value)} does not work in this version of nixpkgs",
                    Body = "It is marked as broken. Remove it for now; a later update may fix it.",
                }
                : null;
        },
        text =>
        {
            var match = Renamed().Match(text);
            return match.Success
                ? new Explanation
                {
                    Title = $"{match.Groups[1].Value} is now called {match.Groups[2].Value}",
                    Body = $"Replace {match.Groups[1].Value} with {match.Groups[2].Value} in your configuration.",
                }
                : null;
        },
        text =>
        {
            var match = Removed().Match(text);
            if (!match.Success) return null;
            // Nix may add why, such as ", use foo instead".
            var reason = Sentence($"It was removed from nixpkgs{match.Groups[2].Value}");
            return new Explanation
            {
                Title = $"{match.Groups[1].Value} is no longer available",
                Body = reason == "It was removed from nixpkgs."
                    ? "It was removed from nixpkgs. Remove it from your configuration."
                    : $"{reason} Remove it from your configuration.",
            };
        },
        text =>
        {
            var match = Untracked().Match(text);
            if (!match.Success) return null;
            var file = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return new Explanation
            {
                Title = $"{file} is not part of the flake yet",
                Body = $"Flakes only see files that Git tracks. Run git add {file} in your configuration folder, then try again.",
            };
        },
        text =>
        {
            var match = OptionNoEffect().Match(text);
            if (!match.Success) return null;
            var file = SourcePrefix().Replace(match.Groups[2].Value, "");
            var hint = match.Groups[3].Value.Length > 0 ? Sentence(match.Groups[3].Value) : "";
            var unknown = file == "<unknown-file>";
            return new Explanation
            {
                Title = $"{match.Groups[1].Value} no longer exists",
                Body = hint.Length > 0
                    ? unknown
                        ? $"{hint} Remove the setting."
                        : $"{hint} Remove it from {file}."
                    : unknown
                        ? "Remove the setting."
                        : $"Remove it from {file}.",
            };
        },
        text =>
        {
            var match = OptionMissing().Match(text);
            if (!match.Success) return null;
            var files = DefinedInFiles(text);
            return new Explanation
            {
                Title = $"The setting {match.Groups[1].Value} does not exist",
                Body = files.Count > 0
                    ? $"It may have been renamed or removed in this version, or it belongs to a module your configuration does not import. It is set in {JoinNames(files)}."
                    : "It may have been renamed or removed in this version, or it belongs to a module your configuration does not import.",
            };
        },
        text =>
        {
            var match = OptionType().Match(text);
            if (!match.Success) return null;
            var files = DefinedInFiles(text);
            var type = TypeSuffix().Replace(match.Groups[2].Value, " in a particular format", 1);
            return new Explanation
            {
                Title = $"{match.Groups[1].Value} has a value it does not accept",
                Body = files.Count > 0
                    ? $"It expects {type}. It is set in {JoinNames(files)}."
                    : $"It expects {type}.",
            };
        },
        text =>
        {
            var match = ConflictingDefinitions().Match(text);
            if (!match.Success) return null;
            var files = DefinedInFiles(text);
            return new Explanation
            {
                Title = $"{match.Groups[1].Value} is set to different values",
                Body = files.Count > 0
                    ? $"Only one value can apply. It is set in {JoinNames(files)}. Remove all but one of them."
                    : "Only one value can apply. Remove all but one of them.",
            };
        },
        text =>
        {
            var match = AccessedWithoutValue().Match(text);
            return match.Success
                ? new Explanation
                {
                    Title = $"{match.Groups[1].Value} needs a value",
                    Body = "Nothing in your configuration sets it, and it has no default. Set it, or remove what depends on it.",
                }
                : null;
        },
        text =>
        {
            var match = FailedAssertions().Match(text);
            if (!match.Success) return null;
            var problems = AssertionBullet().Split(match.Groups[1].Value)
                .Where(item => item.Trim().Length > 0)
                .Select(Sentence)
                .ToList();
            return new Explanation
            {
                Title = problems.Count == 1
                    ? "The configuration reports a problem"
                    : $"The configuration reports {problems.Count} problems",
                // The configuration's own words, shown as they are.
                Body = string.Join(" ", problems),
            };
        },
        text =>
        {
            var files = Unique(Clobbered().Matches(text).Select(match => match.Groups[1].Value));
            if (files.Count == 0) return null;
            return files.Count == 1
                ? new Explanation
                {
                    Title = "A file is in the way",
                    Body = $"Home Manager manages {JoinNames(files)}, but a file already exists there. Move it somewhere else or delete it, then apply again.",
                }
                : new Explanation
                {
                    Title = $"{files.Count} files are in the way",
                    Body = $"Home Manager manages {JoinNames(files)}, but files already exist there. Move them somewhere else or delete them, then apply again.",
                };
        },
        text =>
        {
            var match = SubpathConflict().Match(text);
            if (!match.Success) return null;
            var first = StoreName(match.Groups[1].Value).Split('/');
            var second = StoreName(match.Groups[2].Value).Split('/');
            var file = string.Join("/", first.Skip(1));
            return new Explanation
            {
                Title = "Two packages contain the same file",
                Body = $"{PackageName(first[0])} and {PackageName(second[0])} both provide {file}. Remove one of them.",
            };
        },
        text =>
        {
            var match = BuildFailure().Match(text);
            if (!match.Success) return null;
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return new Explanation
            {
                Title = $"Building {PackageName(name)} failed",
                Body = "Its build log shows why. When the package comes from nixpkgs, this is rarely caused by your settings: try again after an update, or remove it for now.",
            };
        },
        text => Download().IsMatch(text)
            ? new Explanation
            {
                Title = "A download failed",
                Body = "Check the internet connection and try again. If it keeps failing, the download server may be unavailable.",
            }
            : null,
        text =>
        {
            var match = AttributeMissing().Match(text);
            if (!match.Success) return null;
            var similar = match.Groups[2].Success
                ? SimilarSeparator().Split(match.Groups[2].Value).Where(name => name.Length > 0).ToList()
                : [];
            var name = match.Groups[1].Value;
            return new Explanation
            {
                Title = $"{name} does not exist",
                Body = similar.Count > 0
                    ? $"Your configuration refers to {name}, which does not exist. Check the spelling; similar names are {JoinNames(similar)}."
                    : $"Your configuration refers to {name}, which does not exist. Check the spelling.",
            };
        },
    ];

    /// <summary>"a, b and c", as the messages list names (<c>list</c>).</summary>
    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[names.Count - 1]}",
        };

    private static List<string> Unique(IEnumerable<string> items) => items.Distinct().ToList();

    /// <summary>One line, with a capital and a full stop (<c>sentence</c>).</summary>
    private static string Sentence(string text)
    {
        var trimmed = Spaces().Replace(text.Trim(), " ");
        if (trimmed.Length == 0) return "";
        var end = ".!?".Contains(trimmed[^1]) ? "" : ".";
        return $"{char.ToUpperInvariant(trimmed[0])}{trimmed[1..]}{end}";
    }

    /// <summary>The first sentence of single-line text, without its full stop (<c>issues</c> in the insecure rule).</summary>
    private static string FirstSentenceOf(string normalized)
    {
        var sentence = FirstSentenceShape().Match(normalized).Value;
        return sentence.EndsWith('.') ? sentence[..^1] : sentence;
    }

    /// <summary>Files named in a module system message, flake-relative where possible (<c>definedIn</c>).</summary>
    private static List<string> DefinedInFiles(string text) =>
        Unique(DefinedIn().Matches(text)
            .Select(match => SourcePrefix().Replace(match.Groups[1].Value, ""))
            .Where(file => file != "<unknown-file>"));

    private static string StoreName(string path) => StorePrefix().Replace(path, "", 1);

    [GeneratedRegex("^(.+?)-(?![a-zA-Z])")]
    private static partial Regex PackageNameShape();

    [GeneratedRegex("^/nix/store/[a-z0-9]{32}-")]
    private static partial Regex StorePrefix();

    [GeneratedRegex("^/nix/store/[a-z0-9]{32}-source/")]
    private static partial Regex SourcePrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex("^\\s*- In `([^']+)'", RegexOptions.Multiline)]
    private static partial Regex DefinedIn();

    [GeneratedRegex("^\\s*- ", RegexOptions.Multiline)]
    private static partial Regex IssueBullet();

    [GeneratedRegex(@"^.*?(?:\.(?=\s)|\z)")]
    private static partial Regex FirstSentenceShape();

    [GeneratedRegex("^- ", RegexOptions.Multiline)]
    private static partial Regex AssertionBullet();

    [GeneratedRegex(", | or ")]
    private static partial Regex SimilarSeparator();

    [GeneratedRegex(" matching the pattern .*")]
    private static partial Regex TypeSuffix();

    [GeneratedRegex("No space left on device")]
    private static partial Regex DiskFull();

    [GeneratedRegex("Request dismissed|Not authorized|Error executing command as another user")]
    private static partial Regex AuthenticationCancelled();

    [GeneratedRegex("Refusing to evaluate package '([^']+)' in \\S+ because it has an unfree license")]
    private static partial Regex UnfreeRefusal();

    [GeneratedRegex("Refusing to evaluate package '([^']+)' in \\S+ because it is marked as insecure(?:\\s*Known issues:\\n([\\s\\S]*?)\\n\\s*You can install it anyway)?")]
    private static partial Regex InsecureRefusal();

    [GeneratedRegex("Refusing to evaluate package '([^']+)' in \\S+ because it is not available on the requested hostPlatform")]
    private static partial Regex PlatformRefusal();

    [GeneratedRegex("Refusing to evaluate package '([^']+)' in \\S+ because it has problems:[\\s\\S]*?- broken")]
    private static partial Regex BrokenRefusal();

    [GeneratedRegex("error: '([^']+)' has been renamed to(?:/replaced by)? '([^']+)'")]
    private static partial Regex Renamed();

    [GeneratedRegex("error: '([^']+)' has been removed(.*)")]
    private static partial Regex Removed();

    [GeneratedRegex(@"Path '([^']+)' in the repository ""[^""]+"" is not tracked by Git|error: path '/nix/store/[a-z0-9]{32}-source/([^']+)' does not exist")]
    private static partial Regex Untracked();

    [GeneratedRegex("The option definition `([^']+)' in `([^']+)' no longer has any effect; please remove it\\.\\n?([^\\n]*)")]
    private static partial Regex OptionNoEffect();

    [GeneratedRegex("The option `([^']+)' does not exist\\.")]
    private static partial Regex OptionMissing();

    [GeneratedRegex("A definition for option `([^']+)' is not of type `(.+?)'\\. Definition values:")]
    private static partial Regex OptionType();

    [GeneratedRegex("The option `([^']+)' has conflicting definition values:")]
    private static partial Regex ConflictingDefinitions();

    [GeneratedRegex("The option `([^']+)' was accessed but has no value defined")]
    private static partial Regex AccessedWithoutValue();

    [GeneratedRegex("Failed assertions:\\n([\\s\\S]*?)(?:\\n\\s*\\n|\\z)")]
    private static partial Regex FailedAssertions();

    [GeneratedRegex("Existing file '([^']+)' would be clobbered(?! by backing up)")]
    private static partial Regex Clobbered();

    [GeneratedRegex("two given paths contain a conflicting subpath:\\s*`?(\\S+?)'? and\\s*`?(\\S+?)'?\\n")]
    private static partial Regex SubpathConflict();

    [GeneratedRegex("Cannot build '/nix/store/[a-z0-9]{32}-([^']+)\\.drv'|builder for '/nix/store/[a-z0-9]{32}-([^']+)\\.drv' failed")]
    private static partial Regex BuildFailure();

    [GeneratedRegex("unable to download|Could not resolve host|Couldn't resolve host|Connection timed out|Network is unreachable")]
    private static partial Regex Download();

    [GeneratedRegex("error: attribute '([^']+)' missing(?:[\\s\\S]*?Did you mean (?:one of )?(.+?)\\?)?")]
    private static partial Regex AttributeMissing();
}
