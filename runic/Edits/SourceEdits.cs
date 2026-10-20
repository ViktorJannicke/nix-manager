using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Explain;
using NixManager.Nix;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Software;

namespace NixManager.Edits;

/// <summary>The flake snapshot an edited review was staged from, and the files it changed (<c>Staged</c>).</summary>
public sealed record Staged(string Original, IReadOnlyList<EditedFile> Files);

/// <summary>
/// Pending edits in a review: staged into a copy of the flake, verified by evaluation, written into the checkout
/// on apply and put back by an undo.
/// </summary>
/// <remarks>Every <c>run</c> parameter runs quiet commands.</remarks>
public static partial class SourceEdits
{
    /// <summary>
    /// Options that are written but never read: the
    /// <c>alsoSet</c> keys of the experiences, whose value cannot be evaluated while their feature is off.
    /// </summary>
    public static IReadOnlySet<string> UnreadOptions() => new HashSet<string>(StringComparer.Ordinal) { "hardware.nvidia.open" };

    [GeneratedRegex("""\G("(?:[^"\\]|\\.)*"|[A-Za-z_][A-Za-z0-9_'-]*)(\.|\z)""")]
    private static partial Regex AttrName();

    /// <summary>Splits <c>pkgs.kdePackages."7zz"</c> into its names, or null when it is not an attribute path (<c>splitAttr</c>).</summary>
    public static List<string>? SplitAttr(string text)
    {
        var names = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var match = AttrName().Match(text, index);
            if (!match.Success) return null;
            var name = match.Groups[1].Value;
            if (name.StartsWith('"'))
            {
                try { name = JsonNode.Parse(name)!.GetValue<string>(); }
                catch (JsonException) { return null; }
            }
            names.Add(name);
            index = match.Index + match.Length;
            if (match.Groups[2].Length == 0 && index < text.Length) return null;
        }
        return names.Count > 0 ? names : null;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Copies the flake's tracked files into the review and applies the edits to the copy (<c>stageSources</c>).
    /// Each edited file must be visible to Nix and match the checkout; a created file must not exist yet.
    /// </summary>
    public static async Task<Staged> StageSourcesAsync(Config config, Request r, IReadOnlyList<Change> changes, Func<Command, Task<CommandResult>> run)
    {
        if (changes.Count == 0) throw new ManagerException("There are no pending edits.");
        foreach (var change in changes)
            if (change is PackageEdit { Attr: var attr } && SplitAttr(attr)?[0] != "pkgs")
                throw new ManagerException($"{attr} is not a package from pkgs.");
        var (files, problems) = Drafts.Render(config.Flake, changes);
        if (problems.Count > 0)
        {
            var first = $"{changes[problems[0].Index].Subject}: {problems[0].Message}";
            throw new ManagerException(problems.Count > 1 ? $"{first} (and {problems.Count - 1} more problems)" : first);
        }
        if (files.Count == 0) throw new ManagerException("The pending edits do not change any file.");
        var original = await Evaluation.FlakeSourceAsync(config.Flake, run).ConfigureAwait(false);
        foreach (var file in files)
        {
            if (file.Created == true)
            {
                if (Exists(Path.Combine(original, file.Path)) || Exists(Path.Combine(config.Flake, file.Path)))
                    throw new ManagerException($"{file.Path} already exists.");
                continue;
            }
            string? tracked;
            try { tracked = File.ReadAllText(Path.Combine(original, file.Path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { tracked = null; }
            if (tracked is null)
                throw new ManagerException($"{file.Path} is not tracked by Git, so Nix cannot read it. Add it with git add first.");
            if (Repository.HashText(tracked) != file.BaseHash)
                throw new ManagerException($"{file.Path} changed while the edits were being prepared.");
        }
        var copy = Path.Combine(Paths.ReviewRoot(config, r.ReviewId!), "source");
        await run(new Command { Label = "Copy flake source", Argv = ["cp", "-rT", "--no-preserve=mode", original, copy], Quiet = true }).ConfigureAwait(false);
        foreach (var file in files)
        {
            try
            {
                var target = Path.Combine(copy, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, file.After).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ManagerException($"Write {file.Path}: {e.Message}");
            }
        }
        return new Staged(original, files);
    }

    private static List<EditedPackage> Packages(ConfigurationState state) =>
        state.Packages.SelectMany(entry => entry.Items.Select(item => new EditedPackage
        {
            Out = item.Out, Name = item.Name, Version = item.Version, Description = item.Description, File = entry.File,
        })).ToList();

    /// <summary>Packages in <paramref name="a"/> that <paramref name="b"/> lacks, counting duplicates.</summary>
    private static List<EditedPackage> Minus(List<EditedPackage> a, List<EditedPackage> b)
    {
        var rest = b.Select(item => $"{item.File}\0{item.Out}").ToList();
        return a.Where(item =>
        {
            var index = rest.IndexOf($"{item.File}\0{item.Out}");
            if (index < 0) return true;
            rest.RemoveAt(index);
            return false;
        }).ToList();
    }

    /// <summary>A JavaScript value read from JSON: <see cref="Present"/> is false for <c>undefined</c>.</summary>
    private readonly record struct Js(bool Present, JsonNode? Node)
    {
        public static Js Of(JsonElement element) =>
            element.ValueKind == JsonValueKind.Undefined ? default : new(true, JsonNode.Parse(element.GetRawText()));
    }

    /// <summary><c>isDeepStrictEqual</c> for JSON values: objects regardless of member order.</summary>
    private static bool DeepEqual(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(pair => y.TryGetPropertyValue(pair.Key, out var other) && DeepEqual(pair.Value, other)),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(pair => DeepEqual(pair.First, pair.Second)),
        (JsonValue x, JsonValue y) => x.GetValueKind() == y.GetValueKind() && x.GetValueKind() switch
        {
            JsonValueKind.Number => x.GetValue<double>() == y.GetValue<double>(),
            JsonValueKind.String => x.GetValue<string>() == y.GetValue<string>(),
            _ => true,
        },
        _ => false,
    };

    private static bool DeepEqual(Js a, Js b) => a.Present == b.Present && (!a.Present || DeepEqual(a.Node, b.Node));

    /// <summary>The value is the wanted one; list options merge every definition, so the wanted items must be present.</summary>
    private static bool Includes(Js after, Js wanted) =>
        DeepEqual(after, wanted)
        || (after.Node is JsonArray items && wanted.Node is JsonArray want && want.All(item => items.Any(x => DeepEqual(x, item))));

    /// <summary>A value for a message: its JSON, shortened to 80 characters, or "nothing" (<c>show</c>).</summary>
    private static string Show(Js value)
    {
        if (!value.Present) return "nothing";
        var text = JsJson.Stringify(value.Node);
        return text.Length > 80 ? $"{text[..77]}..." : text;
    }

    [GeneratedRegex("^/nix/store/[a-z0-9]{32}-source/flake\\.nix$")]
    private static partial Regex InlineModule();

    // lib.nixosSystem and homeManagerConfiguration attribute modules written inline in the flake to the input's
    // own flake.nix.
    private static string Origin(string file) => InlineModule().IsMatch(file) ? "a module written directly in flake.nix" : file;

    private static string Joined(IReadOnlyList<string> items) =>
        items.Count <= 1 ? items.FirstOrDefault() ?? "" : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    private static string Sentence(string text) => text.Length == 0 ? text : $"{char.ToUpperInvariant(text[0])}{text[1..]}";

    /// <summary>
    /// Evaluates every edited configuration before and after the edits (<c>verifyEdits</c>). Each change must have
    /// its effect in the configuration that is being reviewed: a package appears in (or leaves) its file's list,
    /// and an option takes the new value.
    /// </summary>
    public static async Task<ReviewEdits> VerifyEditsAsync(Config config, Request r, Staged staged, IReadOnlyList<Change> changes,
        Func<Command, Task<CommandResult>> run)
    {
        var edited = await Evaluation.FlakeSourceAsync($"path:{Path.Combine(Paths.ReviewRoot(config, r.ReviewId!), "source")}", run).ConfigureAwait(false);
        var scopes = new[] { Targets.Home, Targets.System }.Where(scope => changes.Any(change => change.Scope == scope)).ToList();
        if (scopes.Any(scope => scope != r.Scope && r.Scope != Scopes.All))
            throw new ManagerException("The pending edits change a configuration this review does not build.");
        var host = r.Host is { Length: > 0 } ? r.Host : config.HostKey;

        async Task<EditEffect> Verify(string scope)
        {
            var own = changes.Where(change => change.Scope == scope).ToList();
            var options = own.OfType<OptionEdit>().Select(change => change.Option).ToList();
            var attrs = own.OfType<PackageEdit>().Select(change => (IReadOnlyList<string>)SplitAttr(change.Attr)!.Skip(1).ToList()).ToList();
            var attribute = Evaluation.ConfigurationAttr(config, host, scope);
            // Package settings must not replace the package set the flake builds.
            var settings = options.Where(option => option.StartsWith("nixpkgs.config.", StringComparison.Ordinal)).Select(option => option.Split('.')[2]).ToList();
            var nixpkgs = settings.Count > 0;
            // Some options cannot be read before the feature they belong to is on.
            var unread = UnreadOptions();
            var evaluations = await Task.WhenAll(
                Evaluation.EvaluateStateAsync(staged.Original, attribute, scope,
                    new StateRequest { Options = options.Where(option => !unread.Contains(option)).ToList(), Attrs = attrs, Nixpkgs = nixpkgs }, run),
                Evaluation.EvaluateStateAsync(edited, attribute, scope, new StateRequest { Options = options, Attrs = attrs, Nixpkgs = nixpkgs }, run)).ConfigureAwait(false);
            var (before, after) = (evaluations[0], evaluations[1]);
            var added = Minus(Packages(after), Packages(before));
            var removed = Minus(Packages(before), Packages(after));
            var name = scope == Targets.Home ? $"{host}'s home configuration" : $"{host}'s system";
            if (before.Nixpkgs is { } beforeSet && after.Nixpkgs is { } afterSet)
            {
                var keys = beforeSet.Config.AsObject().Select(pair => pair.Key).Concat(afterSet.Config.AsObject().Select(pair => pair.Key)).Distinct().ToList();
                Js Member(JsonNode set, string key) => set.AsObject().TryGetPropertyValue(key, out var value) ? new(true, value) : default;
                var changed = keys.Where(key => !key.StartsWith('_') && !settings.Contains(key)
                    && !DeepEqual(Member(beforeSet.Config, key), Member(afterSet.Config, key))).ToList();
                if (beforeSet.Names != afterSet.Names) changed.Add("the packages the flake adds");
                if (changed.Count > 0)
                    throw new ManagerException($"Setting nixpkgs.config in {name} also changes {string.Join(", ", changed)}.", new Explanation
                    {
                        Title = "This setting would change more than it says",
                        Body = $"In {name}, nixpkgs.config in a module makes the configuration import nixpkgs again, which also changes {string.Join(", ", changed)}. Set {string.Join(", ", settings)} where the flake imports nixpkgs instead, for example import nixpkgs {{ config.{settings[0]} = true; }} in flake.nix.",
                    });
            }
            // Settings are checked first: a setting without effect explains a package that is still refused better
            // than the refusal does. A created file is checked through the settings and packages written into it.
            foreach (var change in own.OfType<OptionEdit>())
            {
                var option = change.Option;
                var now = after.Options.GetValueOrDefault(option);
                // Removing an entry, such as a user from users.users, leaves no setting behind.
                if (change is UnsetOption && now?.Exists != true && before.Options.GetValueOrDefault(option)?.Exists == true) continue;
                if (now is not { Exists: true }) throw new ManagerException($"{option} is not an option of {name}.");
                if (change is SetOption set)
                {
                    if (now.Error == true) throw new ManagerException($"{option} no longer evaluates after the edit.");
                    var others = now.Files.Where(file => file != change.File).ToList();
                    var wanted = new Js(true, set.Value);
                    if (!Includes(Js.Of(now.Value), wanted))
                    {
                        var files = Sentence(Joined(others.Select(Origin).ToList()));
                        throw new ManagerException(
                            $"{option} is {Show(Js.Of(now.Value))} instead of {Show(wanted)}. Another definition takes precedence, or {change.File} is not part of {name}.",
                            now.Files.Contains(change.File) && others.Count > 0
                                ? new Explanation
                                {
                                    Title = $"{option} is also set elsewhere",
                                    Body = others.Count == 1
                                        ? $"{files} sets {option} too, and that value wins. Change it there instead of adding a new definition."
                                        : $"{files} set {option} too, and that value wins. Change it there instead of adding a new definition.",
                                }
                                : null);
                    }
                    if (!now.Files.Contains(change.File))
                        throw new ManagerException($"{change.File} is not part of {name}, so {option} is unchanged.");
                    if (now.HasEffective && !Includes(Js.Of(now.Effective), wanted))
                    {
                        var setting = option.Length > "nixpkgs.config.".Length ? option["nixpkgs.config.".Length..] : "";
                        throw new ManagerException(
                            $"{option} is set in {change.File}, but {name} takes its packages from a nixpkgs the flake imports itself.",
                            new Explanation
                            {
                                Title = "This setting does not reach your packages",
                                Body = $"{name} gets its packages from a nixpkgs that the flake imports itself, so nixpkgs.config in modules has no effect. Set {setting} where nixpkgs is imported instead, for example import nixpkgs {{ config.{setting} = {Show(wanted)}; }} in flake.nix.",
                            });
                    }
                }
                else if (now.Files.Contains(change.File))
                    throw new ManagerException($"{option} is still set in {change.File}.");
            }
            var index = 0;
            foreach (var change in own.OfType<PackageEdit>())
            {
                var position = index++;
                var adding = change is AddPackage;
                var resolved = (adding ? after : before).Resolved;
                var output = position < resolved.Count ? resolved[position] : null;
                if (string.IsNullOrEmpty(output))
                {
                    // Evaluating it again without tryEval yields Nix's reason, such as an unfree license.
                    var path = string.Join('.', SplitAttr(change.Attr)!.Skip(1).Select(NixText.Attr));
                    var result = await run(new Command
                    {
                        Label = "Evaluate package",
                        Argv = ["nix", "eval", "--raw", $"path:{(adding ? edited : staged.Original)}#{attribute}.pkgs.{path}.outPath"],
                        Quiet = true,
                        AllowFailure = true,
                    }).ConfigureAwait(false);
                    var found = Explainer.Explain(result.Stderr);
                    var explanation = found?.Fix is { } fix ? found with { Fix = fix with { Scope = scope } } : found;
                    throw new ManagerException(
                        explanation is not null
                            ? $"{change.Attr} cannot be added to {name}: {explanation.Title}."
                            : $"{change.Attr} does not exist in {name}'s packages.",
                        explanation);
                }
                var list = adding ? added : removed;
                if (!list.Any(item => item.Out == output && item.File == change.File))
                    throw new ManagerException(adding
                        ? $"{change.Attr} was added to {change.File}, but {name} does not include it. Is the file part of this configuration?"
                        : $"Removing {change.Attr} from {change.File} does not remove it from {name}.");
            }
            return new EditEffect
            {
                Scope = scope,
                Added = added,
                Removed = removed,
                Options = options.Select(option => new OptionEffect
                {
                    Option = option,
                    Before = before.Options.GetValueOrDefault(option)?.Value ?? default,
                    After = after.Options.GetValueOrDefault(option)?.Value ?? default,
                }).ToList(),
            };
        }

        var effects = await Task.WhenAll(scopes.Select(Verify)).ConfigureAwait(false);
        return new ReviewEdits
        {
            Changes = [.. changes],
            Hash = Change.Hash(changes),
            Files = staged.Files.Select(file => file.ToDraftFile()).ToList(),
            Effects = [.. effects],
        };
    }

    /// <summary>
    /// Writes a review's edited files into the checkout unless any changed since (<c>writeSources</c>). Created
    /// files are added to Git's index as intended, so the flake sees them.
    /// </summary>
    public static async Task WriteSourcesAsync(Config config, string reviewId, ReviewEdits edits, Func<Command, Task<CommandResult>> run)
    {
        try
        {
            var root = Path.Combine(Paths.ReviewRoot(config, reviewId), "source");
            var writes = edits.Files.Select(file =>
            {
                var path = Drafts.SourcePath(config.Flake, file.Path);
                var current = File.Exists(path) ? File.ReadAllText(path) : null;
                if (file.Created == true ? current is not null : current is null || Repository.HashText(current) != file.BaseHash)
                    throw new ManagerException(file.Created == true
                        ? $"{file.Path} was created after this review was prepared. Prepare the edits again."
                        : $"{file.Path} changed after this review was prepared. Prepare the edits again.");
                return (Path: path, Data: File.ReadAllBytes(Path.Combine(root, file.Path)), Mode: file.Created == true ? ReviewStore.NewFileMode : ReviewStore.ModeOf(path));
            }).ToList();
            foreach (var write in writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(write.Path)!);
                ReviewStore.AtomicWrite(write.Path, write.Data, write.Mode);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ManagerException($"Write edited files: {e.Message}");
        }
        var created = edits.Files.Where(file => file.Created == true).Select(file => file.Path).ToList();
        if (created.Count > 0 && Exists(Path.Combine(config.Flake, ".git")))
            await run(new Command { Label = "Add created files to Git", Argv = ["git", "-C", config.Flake, "add", "--intent-to-add", "--", .. created], Quiet = true })
                .ConfigureAwait(false);
    }

    /// <summary>A module file of the checkout, or its flake.lock (<c>checkoutPath</c>).</summary>
    public static string CheckoutPath(Config config, string path) =>
        path == "flake.lock" ? Path.Combine(config.Flake, path) : Drafts.SourcePath(config.Flake, path);

    /// <summary>Checkout files as they are now, keyed by flake-relative path; absent files are null (<c>readSources</c>).</summary>
    public static Dictionary<string, string?> ReadSources(Config config, IEnumerable<string> paths)
    {
        var sources = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var file = CheckoutPath(config, path);
            sources[path] = File.Exists(file) ? File.ReadAllText(file) : null;
        }
        return sources;
    }

    /// <summary>
    /// What writing the files in <paramref name="before"/> replaced: their earlier content, and a hash of each file
    /// now (<c>undoFiles</c>).
    /// </summary>
    public static (List<UndoFile> Files, Dictionary<string, string> Contents) UndoFiles(Config config, IReadOnlyDictionary<string, string?> before)
    {
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = ReadSources(config, before.Keys).Select(pair =>
        {
            var earlier = before[pair.Key];
            if (earlier is not null) contents[pair.Key] = earlier;
            return new UndoFile { Path = pair.Key, Created = earlier is null, Written = pair.Value is null ? null : Repository.HashText(pair.Value) };
        }).ToList();
        return (files, contents);
    }

    /// <summary>
    /// Puts back the files an applied change wrote, unless any changed since (<c>restoreSources</c>). Files the
    /// change created are deleted and leave Git's index.
    /// </summary>
    public static async Task RestoreSourcesAsync(Config config, UndoPoint point, IReadOnlyDictionary<string, string> contents, Func<Command, Task<CommandResult>> run)
    {
        try
        {
            var now = ReadSources(config, point.Files.Select(file => file.Path));
            foreach (var file in point.Files)
            {
                if (!file.Created && !contents.ContainsKey(file.Path))
                    throw new ManagerException($"The earlier content of {file.Path} is missing.");
                var current = now[file.Path];
                if (current is null ? file.Written is not null : Repository.HashText(current) != file.Written)
                    throw new ManagerException($"{file.Path} changed after the change was applied, so it cannot be put back automatically.", new Explanation
                    {
                        Title = $"{file.Path} was edited since",
                        Body = "Undo puts back the files exactly as they were and would lose those edits. Go back on the Restore points page instead, which leaves your files as they are.",
                    });
            }
            foreach (var file in point.Files)
            {
                var path = CheckoutPath(config, file.Path);
                if (file.Created)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    ReviewStore.AtomicWrite(path, contents[file.Path], ReviewStore.ModeOf(path));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ManagerException($"Restore files: {e.Message}");
        }
        // Git's index follows, so the flake sees exactly the files it saw before.
        var removed = point.Files.Where(file => file.Created).Select(file => file.Path).ToList();
        var recreated = point.Files.Where(file => !file.Created && file.Written is null).Select(file => file.Path).ToList();
        if (Exists(Path.Combine(config.Flake, ".git")))
        {
            if (removed.Count > 0)
                await run(new Command
                {
                    Label = "Remove created files from Git",
                    Argv = ["git", "-C", config.Flake, "rm", "--cached", "--quiet", "--ignore-unmatch", "--", .. removed],
                    Quiet = true,
                }).ConfigureAwait(false);
            if (recreated.Count > 0)
                await run(new Command { Label = "Add restored files to Git", Argv = ["git", "-C", config.Flake, "add", "--intent-to-add", "--", .. recreated], Quiet = true })
                    .ConfigureAwait(false);
        }
    }

    /// <summary>The draft without the changes a review applied (<c>remaining</c>).</summary>
    public static List<Change> Remaining(IReadOnlyList<Change> draft, IReadOnlyList<Change> applied)
    {
        var rest = applied.ToList();
        return draft.Where(change => !rest.Remove(change)).ToList();
    }
}
