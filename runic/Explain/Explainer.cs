using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Operations;

namespace NixManager.Explain;

/// <summary>
/// Common Nix failures in words someone who does not know Nix can act on, as catalogue messages; names and the
/// configuration's own words taken from the output stay as they are.
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
                Title = DisplayText.Of("stored_the_disk_is_full"),
                Body = DisplayText.Of("stored_nix_ran_out_of_space_remove_old"),
            }
            : null,
        text => AuthenticationCancelled().IsMatch(text)
            ? new Explanation
            {
                Title = DisplayText.Of("stored_authentication_was_cancelled"),
                Body = DisplayText.Of("stored_changing_the_system_needs_an_administrator_password"),
            }
            : null,
        text =>
        {
            var names = Unique(UnfreeRefusal().Matches(text).Select(match => PackageName(match.Groups[1].Value)));
            if (names.Count == 0) return null;
            return new Explanation
            {
                Title = names.Count == 1
                    ? DisplayText.Of("stored_is_not_free_software", ("names", JoinNames(names)))
                    : DisplayText.Of("stored_are_not_free_software", ("names", JoinNames(names))),
                Body = DisplayText.Of("stored_your_configuration_only_allows_software_with_a"),
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
                Title = DisplayText.Of("stored_has_known_security_problems", ("name", PackageName(package))),
                Body = issues.Count > 0
                    ? DisplayText.Of("stored_nixpkgs_marks_as_insecure_choose_another_package", ("package", package), ("issues", string.Join("; ", issues.Take(3))))
                    : DisplayText.Of("stored_nixpkgs_marks_as_insecure_choose_another_package_2", ("package", package)),
            };
        },
        text =>
        {
            var match = PlatformRefusal().Match(text);
            return match.Success
                ? new Explanation
                {
                    Title = DisplayText.Of("stored_does_not_run_on_this_computer", ("name", PackageName(match.Groups[1].Value))),
                    Body = DisplayText.Of("stored_the_package_is_not_made_for_this"),
                }
                : null;
        },
        text =>
        {
            var match = BrokenRefusal().Match(text);
            return match.Success
                ? new Explanation
                {
                    Title = DisplayText.Of("stored_does_not_work_in_this_version_of", ("name", PackageName(match.Groups[1].Value))),
                    Body = DisplayText.Of("stored_it_is_marked_as_broken_remove_it"),
                }
                : null;
        },
        text =>
        {
            var match = Renamed().Match(text);
            return match.Success
                ? new Explanation
                {
                    Title = DisplayText.Of("stored_is_now_called", ("old", match.Groups[1].Value), ("new", match.Groups[2].Value)),
                    Body = DisplayText.Of("stored_replace_with_in_your_configuration", ("old", match.Groups[1].Value), ("new", match.Groups[2].Value)),
                }
                : null;
        },
        text =>
        {
            var match = Removed().Match(text);
            if (!match.Success) return null;
            // Nix may add why, such as ", use foo instead"; that reason is shown in Nix's words.
            var reason = Sentence($"It was removed from nixpkgs{match.Groups[2].Value}");
            return new Explanation
            {
                Title = DisplayText.Of("stored_is_no_longer_available", ("name", match.Groups[1].Value)),
                Body = reason == "It was removed from nixpkgs."
                    ? DisplayText.Of("stored_it_was_removed_from_nixpkgs_remove_it")
                    : DisplayText.Of("stored_remove_it_from_your_configuration", ("reason", DisplayText.Plain(reason))),
            };
        },
        text =>
        {
            var match = Untracked().Match(text);
            if (!match.Success) return null;
            var file = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return new Explanation
            {
                Title = DisplayText.Of("stored_is_not_part_of_the_flake_yet", ("file", file)),
                Body = DisplayText.Of("stored_flakes_only_see_files_that_git_tracks", ("file", file)),
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
                Title = DisplayText.Of("stored_no_longer_exists", ("option", match.Groups[1].Value)),
                Body = hint.Length > 0
                    ? unknown
                        ? DisplayText.Of("stored_remove_the_setting", ("hint", hint))
                        : DisplayText.Of("stored_remove_it_from", ("hint", hint), ("file", file))
                    : unknown
                        ? DisplayText.Of("stored_remove_the_setting_2")
                        : DisplayText.Of("stored_remove_it_from_2", ("file", file)),
            };
        },
        text =>
        {
            var match = OptionMissing().Match(text);
            if (!match.Success) return null;
            var files = DefinedInFiles(text);
            return new Explanation
            {
                Title = DisplayText.Of("stored_the_setting_does_not_exist", ("option", match.Groups[1].Value)),
                Body = files.Count > 0
                    ? DisplayText.Of("stored_it_may_have_been_renamed_or_removed_2", ("files", JoinNames(files)))
                    : DisplayText.Of("stored_it_may_have_been_renamed_or_removed"),
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
                Title = DisplayText.Of("stored_has_a_value_it_does_not_accept", ("option", match.Groups[1].Value)),
                Body = files.Count > 0
                    ? DisplayText.Of("stored_it_expects_it_is_set_in", ("type", type), ("files", JoinNames(files)))
                    : DisplayText.Of("stored_it_expects", ("type", type)),
            };
        },
        text =>
        {
            var match = ConflictingDefinitions().Match(text);
            if (!match.Success) return null;
            var files = DefinedInFiles(text);
            return new Explanation
            {
                Title = DisplayText.Of("stored_is_set_to_different_values", ("option", match.Groups[1].Value)),
                Body = files.Count > 0
                    ? DisplayText.Of("stored_only_one_value_can_apply_it_is", ("files", JoinNames(files)))
                    : DisplayText.Of("stored_only_one_value_can_apply_remove_all"),
            };
        },
        text =>
        {
            var match = AccessedWithoutValue().Match(text);
            return match.Success
                ? new Explanation
                {
                    Title = DisplayText.Of("stored_needs_a_value", ("option", match.Groups[1].Value)),
                    Body = DisplayText.Of("stored_nothing_in_your_configuration_sets_it_and"),
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
                    ? DisplayText.Of("stored_the_configuration_reports_a_problem")
                    : DisplayText.Of("stored_the_configuration_reports_problems", ("count", problems.Count)),
                // The configuration's own words, shown as they are.
                Body = DisplayText.Plain(string.Join(" ", problems)),
            };
        },
        text =>
        {
            var files = Unique(Clobbered().Matches(text).Select(match => match.Groups[1].Value));
            if (files.Count == 0) return null;
            return files.Count == 1
                ? new Explanation
                {
                    Title = DisplayText.Of("stored_a_file_is_in_the_way"),
                    Body = DisplayText.Of("stored_home_manager_manages_but_a_file_already", ("files", JoinNames(files))),
                }
                : new Explanation
                {
                    Title = DisplayText.Of("stored_files_are_in_the_way", ("count", files.Count)),
                    Body = DisplayText.Of("stored_home_manager_manages_but_files_already_exist", ("files", JoinNames(files))),
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
                Title = DisplayText.Of("stored_two_packages_contain_the_same_file"),
                Body = DisplayText.Of("stored_and_both_provide_remove_one_of_them", ("first", PackageName(first[0])), ("second", PackageName(second[0])), ("file", file)),
            };
        },
        text =>
        {
            var match = BuildFailure().Match(text);
            if (!match.Success) return null;
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return new Explanation
            {
                Title = DisplayText.Of("stored_building_failed", ("name", PackageName(name))),
                Body = DisplayText.Of("stored_its_build_log_shows_why_when_the"),
            };
        },
        text => Download().IsMatch(text)
            ? new Explanation
            {
                Title = DisplayText.Of("stored_a_download_failed"),
                Body = DisplayText.Of("stored_check_the_internet_connection_and_try_again"),
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
                Title = DisplayText.Of("stored_does_not_exist", ("name", name)),
                Body = similar.Count > 0
                    ? DisplayText.Of("stored_your_configuration_refers_to_which_does_not_2", ("name", name), ("names", JoinNames(similar)))
                    : DisplayText.Of("stored_your_configuration_refers_to_which_does_not", ("name", name)),
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
