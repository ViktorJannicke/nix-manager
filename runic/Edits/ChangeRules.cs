using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Nix;
using NixManager.Reviews;
using NixManager.Software;

namespace NixManager.Edits;

/// <summary>A pending edit in words (<c>describeChange</c>): English text, translated where it is shown.</summary>
public sealed record ChangeText(string Title, string Detail);

/// <summary>
/// Pending edits in words and in combination, shared by the dashboard and the command line. Texts are English; the
/// dashboard translates them where it shows them.
/// </summary>
public static partial class ChangeRules
{
    /// <summary>Whether a policy accepts a non-free package by its name; a predicate counts as accepting (<c>acceptsUnfree</c>).</summary>
    public static bool AcceptsUnfree(UnfreePolicy? policy, string? name) =>
        policy is null || policy.All || policy.Predicate || (name is not null && policy.Packages.Contains(name));

    /// <summary>
    /// Adds edits to a draft (<c>mergeChanges</c>). An edit replaces a pending edit of the same option, and
    /// cancels a pending opposite package edit instead of stacking. An option edit that
    /// <paramref name="unchanged"/> reports as a no-op only drops the pending edit.
    /// </summary>
    public static List<Change> Merge(IReadOnlyList<Change> current, IReadOnlyList<Change> next, Func<Change, bool>? unchanged = null)
    {
        unchanged ??= _ => false;
        var changes = current.ToList();
        foreach (var change in next)
        {
            switch (change)
            {
                case CreateModule:
                    if (!changes.Any(item => item is CreateModule && item.File == change.File)) changes.Add(change);
                    break;
                case PackageEdit package:
                    var opposite = changes.FindIndex(item =>
                        item is PackageEdit other && other.Attr == package.Attr && other.File == package.File && other.GetType() != package.GetType());
                    if (opposite >= 0) changes.RemoveAt(opposite);
                    else if (!changes.Contains(change)) changes.Add(change);
                    break;
                case OptionEdit option:
                    changes = changes.Where(item => !(item is OptionEdit other && other.Option == option.Option && other.Scope == option.Scope)).ToList();
                    if (!unchanged(change)) changes.Add(change);
                    break;
            }
        }
        return changes;
    }

    /// <summary>Removes an edit; removing a created file also removes the edits written into it (<c>dropChange</c>).</summary>
    public static List<Change> Drop(IReadOnlyList<Change> changes, int index)
    {
        var removed = index >= 0 && index < changes.Count ? changes[index] : null;
        return changes.Where((change, position) =>
            position != index && !(removed is CreateModule && change.File == removed.File)).ToList();
    }

    // String(value) of a JSON value, as Array.prototype.join writes its items (null as empty).
    private static string JsString(JsonNode? value) => value switch
    {
        null => "",
        JsonArray items => string.Join(',', items.Select(JsString)),
        JsonObject => "[object Object]",
        _ => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => JsJson.Stringify(value),
        },
    };

    private static bool Is(JsonNode? value, JsonValueKind kind) => value is JsonValue && value.GetValueKind() == kind;

    /// <summary>A value as a short phrase: on, off, not set, or its JSON (<c>showValue</c>).</summary>
    public static string ShowValue(JsonNode? value)
    {
        if (Is(value, JsonValueKind.True)) return "on";
        if (Is(value, JsonValueKind.False)) return "off";
        if (value is null || Is(value, JsonValueKind.Null)) return "not set";
        if (value is JsonArray items) return items.Count > 0 ? string.Join(", ", items.Select(JsString)) : "empty";
        return Is(value, JsonValueKind.String) ? $"\"{value.GetValue<string>()}\"" : JsJson.Stringify(JsOrder.Normalize(value));
    }

    /// <summary>An option edit in words (<c>describeOption</c>).</summary>
    private static string DescribeOption(string option, JsonNode? value, string? label)
    {
        var empty = value is JsonArray { Count: 0 };
        var unset = value is null || Is(value, JsonValueKind.Null);
        if (string.IsNullOrEmpty(label))
        {
            if (Is(value, JsonValueKind.True)) return $"{option} on";
            if (Is(value, JsonValueKind.False)) return $"{option} off";
            if (unset) return $"{option} not set";
            if (empty) return $"{option} empty";
            return $"{option} {ShowValue(value)}";
        }
        if (Is(value, JsonValueKind.True)) return $"{label}: {option} on";
        if (Is(value, JsonValueKind.False)) return $"{label}: {option} off";
        if (unset) return $"{label}: {option} not set";
        if (empty) return $"{label}: {option} empty";
        return $"{label}: {option} {ShowValue(value)}";
    }

    [GeneratedRegex(@"^pkgs\.")]
    private static partial Regex PkgsPrefix();

    /// <summary>A pending edit in words: what it does, and where (<c>describeChange</c>).</summary>
    public static ChangeText Describe(Change change) => change switch
    {
        AddPackage add => new($"Install {PkgsPrefix().Replace(add.Attr, "", 1)}", add.File),
        RemovePackage remove => new($"Remove {PkgsPrefix().Replace(remove.Attr, "", 1)}", remove.File),
        SetOption set => new(DescribeOption(set.Option, set.Value, set.Label), set.File),
        UnsetOption unset => new($"Reset {unset.Option} to its default", unset.File),
        CreateModule create => new($"Create {create.File.Split('/')[^1]}", $"{create.File}, imported from {create.Importer}"),
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    /// <summary>The scope a review must build to include every edit (<c>changesScope</c>): <c>all</c>, <c>home</c> or <c>system</c>.</summary>
    public static string ChangesScope(IReadOnlyList<Change> changes)
    {
        var scopes = changes.Select(change => change.Scope).ToHashSet();
        return scopes.Count > 1 ? "all" : scopes.Contains(Targets.System) ? Targets.System : Targets.Home;
    }

    private static string Directory(string file)
    {
        var parts = file.Split('/');
        return string.Join('/', parts[..^1]);
    }

    // Array.prototype.sort with a comparator: stable.
    private static List<T> Sort<T>(IEnumerable<T> items, Comparison<T> compare) =>
        items.OrderBy(item => item, Comparer<T>.Create(compare)).ToList();

    /// <summary>
    /// The files a new module is best imported from, best first (<c>rankImporters</c>): the one importing most of
    /// its neighbours already (per <paramref name="imports"/>), the folder's default.nix, a file in the same
    /// folder, then the closest folder above. Ties sort by <c>localeCompare</c> (<see cref="JsText.LocaleCompare"/>).
    /// </summary>
    public static List<string> RankImporters(IReadOnlyList<string> importers, string file, IReadOnlyDictionary<string, IReadOnlyList<string>>? imports = null)
    {
        var folder = Directory(file);
        int Score(string candidate)
        {
            var neighbours = imports is not null && imports.TryGetValue(candidate, out var paths) ? paths.Count(path => Directory(path) == folder) : 0;
            if (neighbours > 0) return 1000 + neighbours;
            var at = Directory(candidate);
            var inside = folder == at || folder.StartsWith($"{at}/", StringComparison.Ordinal) || at.Length == 0;
            var depth = at.Length > 0 ? at.Split('/').Length : 0;
            return (inside ? 100 + depth * 10 : 0) + (candidate.EndsWith("/default.nix", StringComparison.Ordinal) || candidate == "default.nix" ? 5 : 0);
        }
        return Sort(importers, (a, b) => Score(b) - Score(a) is var order and not 0 ? order : JsText.LocaleCompare(a, b));
    }

    [GeneratedRegex("home")]
    private static partial Regex HomeFile();

    [GeneratedRegex("nixos|system")]
    private static partial Regex SystemFile();

    [GeneratedRegex("hosts/")]
    private static partial Regex HostFile();

    [GeneratedRegex("(^|/)(pkgs|overlays?|data|tests?|docs)/")]
    private static partial Regex NotModule();

    /// <summary>
    /// Module files that could hold an option, best first (<c>rankModules</c>): files the host imports, the last
    /// one used, files of the same kind of configuration, then files that already set options under the same
    /// name or live in a folder of that name. Packages, overlays, data, tests and docs are left out.
    /// </summary>
    public static List<ModuleFile> RankModules(IReadOnlyList<ModuleFile> modules, string scope, string option, string? last = null)
    {
        var head = option.Split('.')[0];
        var own = scope == Targets.Home ? HomeFile() : SystemFile();
        var other = scope == Targets.Home ? SystemFile() : HomeFile();
        int Score(ModuleFile module) =>
            (module.Active == true ? 200 : module.Active == false ? -200 : 0) +
            (module.File == last ? 100 : 0) +
            (own.IsMatch(module.File) ? 20 : 0) -
            (other.IsMatch(module.File) ? 40 : 0) +
            (module.Names.Any(name => name.Split('.')[0] == head) ? 6 : 0) +
            (module.File.Split('/').Contains(head) ? 4 : 0) -
            (HostFile().IsMatch(module.File) ? 3 : 0);
        return Sort(modules.Where(module => !NotModule().IsMatch(module.File)),
            (a, b) => Score(b) - Score(a) is var order and not 0 ? order : JsText.LocaleCompare(a.File, b.File));
    }

    [GeneratedRegex(@"^(programs|services|hardware|virtualisation|security)\z")]
    private static partial Regex TopicGroup();

    /// <summary>The topic of an option, which names a module of its own: <c>fzf</c> for <c>programs.fzf.enable</c> (<c>optionTopic</c>).</summary>
    public static string OptionTopic(string option)
    {
        var parts = option.Split('.');
        var next = parts.Length > 1 ? parts[1] : null;
        return TopicGroup().IsMatch(parts[0]) && !string.IsNullOrEmpty(next) ? next : parts[0];
    }

    [GeneratedRegex("[^a-z0-9+-]+")]
    private static partial Regex SlugBreak();

    [GeneratedRegex(@"^-|-\z")]
    private static partial Regex SlugEdge();

    /// <summary>A path for a new module named <paramref name="name"/> in the folder of <paramref name="near"/>, not in <paramref name="known"/> (<c>freePath</c>).</summary>
    public static string FreePath(IReadOnlySet<string> known, string name, string? near, string fallback)
    {
        var folder = !string.IsNullOrEmpty(near) ? Directory(near) : fallback;
        var slug = SlugEdge().Replace(SlugBreak().Replace(name.ToLowerInvariant(), "-"), "");
        if (slug.Length == 0) slug = "module";
        for (var n = 1; ; n++)
        {
            var file = $"{(folder.Length > 0 ? $"{folder}/" : "")}{slug}{(n > 1 ? $"-{n}" : "")}.nix";
            if (!known.Contains(file)) return file;
        }
    }

    /// <summary>
    /// The module a feature's options belong in, from candidates ranked best first (<c>featureModule</c>): one
    /// named after the feature, or the best candidate unless it sits among several single-topic modules. Null
    /// means a new file.
    /// </summary>
    public static string? FeatureModule(IReadOnlyList<ModuleFile> modules, string name)
    {
        var usable = modules.Where(module => module.Active != false).ToList();
        var named = usable.FirstOrDefault(module => module.File.Split('/')[^1] == $"{name}.nix");
        if (named is not null) return named.File;
        if (usable.Count == 0) return null;
        var best = usable[0];
        var folder = Directory(best.File);
        var siblings = usable.Count(module => module.Active == true && Directory(module.File) == folder);
        return siblings >= 3 ? null : best.File;
    }

    [GeneratedRegex(@"^([A-Za-z0-9_+-][A-Za-z0-9._+-]*/)*[A-Za-z0-9_+-][A-Za-z0-9._+-]*\.nix\z")]
    private static partial Regex NewFile();

    /// <summary>Why a path cannot be a new module file, if it cannot (<c>newFileProblem</c>).</summary>
    public static string? NewFileProblem(IReadOnlySet<string> known, string file)
    {
        if (!NewFile().IsMatch(file)) return "Use a path inside the flake ending in .nix, with letters, digits, dots, dashes and slashes.";
        if (known.Contains(file)) return "This file exists already.";
        return null;
    }

    [GeneratedRegex("^/nix/store/[a-z0-9]{32}-[^/]+/")]
    private static partial Regex StorePrefix();

    [GeneratedRegex("^modules/")]
    private static partial Regex ModulesPrefix();

    /// <summary>"Home Manager: programs/git.nix" style names for module files outside the flake (<c>origin</c>).</summary>
    public static string Origin(string file)
    {
        var inner = StorePrefix().Replace(file, "", 1);
        if (inner == file) return file;
        return inner.StartsWith("nixos/modules/", StringComparison.Ordinal)
            ? $"NixOS: {inner[14..]}"
            : $"Home Manager: {ModulesPrefix().Replace(inner, "", 1)}";
    }

    /// <summary>
    /// Why edits cannot be prepared and applied in one step, or null when they can (<c>oneStepProblem</c>): only
    /// installing and removing your own apps (and allowing non-free software for them) qualifies. Those change
    /// no kernel, boot loader or service, and Undo takes them back.
    /// </summary>
    public static string? OneStepProblem(IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) return "There is nothing to install.";
        if (changes.Any(change => change.Scope != Targets.Home)) return "It changes the computer itself, not just your apps.";
        var simple = changes.All(change =>
            change is PackageEdit or CreateModule ||
            (change is SetOption { Option: "nixpkgs.config.allowUnfree" } set && Is(set.Value, JsonValueKind.True)));
        return simple ? null : "It changes settings, which can restart programs you are using.";
    }

    /// <summary>Why a prepared review is not applied in one step, checking what it turned out to change (<c>oneStepReviewProblem</c>).</summary>
    public static string? OneStepReviewProblem(BuildReview review)
    {
        if (review.Edits is null || review.Inputs is not null || review.Undo is not null || review.Source != ReviewSources.Checkout)
            return "It is not a change of your apps.";
        if (review.System is not null) return "It changes the computer itself, not just your apps.";
        var changed = review.Edits.Effects.SelectMany(effect => effect.Options.Select(option => option.Option));
        if (changed.Any(option => option != "nixpkgs.config.allowUnfree")) return "It changes settings, which can restart programs you are using.";
        return OneStepProblem(review.Edits.Changes);
    }
}
