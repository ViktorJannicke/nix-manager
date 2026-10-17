using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Nix;
using NixManager.Software;

namespace NixManager.Tests;

/// <summary>
/// Replays the corpus <c>Fixtures/record-bun-edits.ts</c> recorded with the Bun version's edit engine
/// (<c>Fixtures/bun-edits.json</c>) and requires the same results and error messages, as JSON text.
/// </summary>
public sealed class EditDifferentialTests
{
    private static JsonNode? Edit(SourceEdit edit) =>
        new JsonObject { ["source"] = edit.Source, ["start"] = edit.Start, ["end"] = edit.End, ["text"] = edit.Text };

    private static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(item => (JsonNode)item)]);

    private static JsonNode Lists(IEnumerable<PackageList> lists) => new JsonArray([.. lists.Select(list =>
    {
        var json = new JsonObject { ["binding"] = list.Binding };
        if (list.Scope is not null) json["scope"] = list.Scope;
        json["conditional"] = list.Conditional;
        json["start"] = list.Start;
        json["end"] = list.End;
        json["items"] = new JsonArray([.. list.Items.Select(item =>
        {
            var entry = new JsonObject();
            if (item.Attr is not null) entry["attr"] = item.Attr;
            if (item.Path is not null) entry["path"] = Strings(item.Path);
            entry["text"] = item.Text;
            entry["start"] = item.Start;
            entry["end"] = item.End;
            return (JsonNode)entry;
        })]);
        return (JsonNode)json;
    })]);

    private static JsonNode Hunks(IEnumerable<Hunk> hunks) => new JsonArray([.. hunks.Select(hunk => (JsonNode)new JsonObject
    {
        ["line"] = hunk.Line,
        ["lines"] = new JsonArray([.. hunk.Lines.Select(line => (JsonNode)new JsonObject { ["kind"] = line.Kind, ["text"] = line.Text })]),
    })]);

    private static JsonNode Changes(IEnumerable<Change> changes) => new JsonArray([.. changes.Select(change => (JsonNode)change.ToJson())]);

    private static List<Change> ChangeList(JsonNode? node) => node!.AsArray().Select(item => Change.FromJson(item!.AsObject())).ToList();

    private static List<string> StringList(JsonNode? node) => node!.AsArray().Select(item => item!.GetValue<string>()).ToList();

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    private static JsonNode? Optional(string? text) => text is null ? null : JsonValue.Create(text);

    private static ModuleFile Module(JsonNode? node) =>
        new(Text(node!["file"])!, node["names"] is { } names ? StringList(names) : [], node["active"]?.GetValue<bool>());

    private static JsonNode? Run(string op, JsonArray args)
    {
        var source = args.Count > 0 && args[0] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        switch (op)
        {
            case "packageLists": return Lists(PackageLists.Find(Nix.Syntax.NixSyntax.Parse(source).Root));
            case "moduleShape":
            {
                var shape = ModuleOptions.Shape(source);
                return new JsonObject
                {
                    ["editable"] = shape.Editable, ["names"] = Strings(shape.Names), ["settings"] = Strings(shape.Settings),
                    ["imports"] = Strings(shape.Imports), ["importable"] = shape.Importable,
                };
            }
            case "addPackage": return Edit(PackageLists.AddPackage(source, Text(args[1])!, args.Count > 2 && args[2] is not null ? StringList(args[2]) : null));
            case "removePackage": return Edit(PackageLists.RemovePackage(source, Text(args[1])!));
            case "appendItem":
            {
                (int, int) Range(JsonNode node) => (node["start"]!.GetValue<int>(), node["end"]!.GetValue<int>());
                return Edit(PackageLists.AppendItem(source, Range(args[1]!), args[2] is null ? null : Range(args[2]!), Text(args[3])!));
            }
            case "setOption": return Edit(ModuleOptions.SetOption(source, Text(args[1])!, args[2]?.DeepClone()));
            case "startList": return Edit(ModuleOptions.StartList(source, Text(args[1])!, Text(args[2])!));
            case "addImport": return Edit(ModuleOptions.AddImport(source, Text(args[1])!));
            case "unsetOption": return Edit(ModuleOptions.UnsetOption(source, Text(args[1])!));
            case "renderValue": return ModuleOptions.RenderValue(args[0]?.DeepClone());
            case "diffLines": return Hunks(args[2] is null ? Drafts.DiffLines(source, Text(args[1])!) : Drafts.DiffLines(source, Text(args[1])!, args[2]!.GetValue<int>()));
            case "importPath": return Drafts.ImportPath(source, Text(args[1])!);
            case "sortLocale": return Strings(StringList(args[0]).OrderBy(item => item, Comparer<string>.Create(JsText.LocaleCompare)));
            case "rankImporters":
            {
                var imports = args[2] is JsonObject map
                    ? map.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)StringList(entry.Value))
                    : null;
                return Strings(ChangeRules.RankImporters(StringList(args[0]), Text(args[1])!, imports));
            }
            case "rankModules":
                return new JsonArray([.. ChangeRules.RankModules(args[0]!.AsArray().Select(Module).ToList(), Text(args[1])!, Text(args[2])!, Text(args[3]))
                    .Select(module => (JsonNode)new JsonObject
                    {
                        ["file"] = module.File, ["names"] = Strings(module.Names), ["active"] = module.Active is { } active ? JsonValue.Create(active) : null,
                    })]);
            case "optionTopic": return ChangeRules.OptionTopic(source);
            case "freePath": return ChangeRules.FreePath(StringList(args[0]).ToHashSet(), Text(args[1])!, Text(args[2]), Text(args[3])!);
            case "featureModule": return Optional(ChangeRules.FeatureModule(args[0]!.AsArray().Select(Module).ToList(), Text(args[1])!));
            case "newFileProblem": return Optional(ChangeRules.NewFileProblem(StringList(args[0]).ToHashSet(), Text(args[1])!));
            case "origin": return ChangeRules.Origin(source);
            case "showValue": return ChangeRules.ShowValue(args[0]?.DeepClone());
            case "describeChange":
            {
                var described = ChangeRules.Describe(Change.FromJson(args[0]!.AsObject()));
                return new JsonObject { ["title"] = described.Title, ["detail"] = described.Detail };
            }
            case "mergeChanges":
                return Changes(ChangeRules.Merge(ChangeList(args[0]), ChangeList(args[1]), args[2]!.GetValue<bool>() ? _ => true : null));
            case "dropChange": return Changes(ChangeRules.Drop(ChangeList(args[0]), args[1]!.GetValue<int>()));
            case "changesScope": return ChangeRules.ChangesScope(ChangeList(args[0]));
            case "oneStepProblem": return Optional(ChangeRules.OneStepProblem(ChangeList(args[0])));
            case "renderDraft":
            {
                var flake = Directory.CreateTempSubdirectory("nix-manager-edits-").FullName;
                try
                {
                    foreach (var (name, content) in args[0]!.AsObject())
                    {
                        var path = Path.Combine(flake, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        var body = Text(content)!;
                        if (body.StartsWith("->", StringComparison.Ordinal)) File.CreateSymbolicLink(path, body[2..]);
                        else File.WriteAllText(path, body);
                    }
                    var changes = ChangeList(args[1]);
                    var (files, problems) = Drafts.Render(flake, changes);
                    return new JsonObject
                    {
                        ["files"] = new JsonArray([.. files.Select(file => (JsonNode)new JsonObject
                        {
                            ["path"] = file.Path, ["baseHash"] = file.BaseHash, ["created"] = file.Created is { } created ? JsonValue.Create(created) : null,
                            ["before"] = file.Before, ["after"] = file.After, ["hunks"] = Hunks(file.Hunks),
                        })]),
                        ["problems"] = new JsonArray([.. problems.Select(problem => (JsonNode)new JsonObject { ["index"] = problem.Index, ["message"] = problem.Message })]),
                        ["hash"] = Change.Hash(changes),
                    };
                }
                finally
                {
                    Directory.Delete(flake, true);
                }
            }
            case "sourcePath":
            {
                var flake = Directory.CreateTempSubdirectory("nix-manager-edits-").FullName;
                try
                {
                    return Drafts.SourcePath(flake, source).Replace(flake, "<flake>", StringComparison.Ordinal);
                }
                finally
                {
                    Directory.Delete(flake, true);
                }
            }
        }
        throw new InvalidOperationException($"Unknown operation {op}");
    }

    /// <summary>Every case whose C# result differs from the recorded one, as text for the failure message.</summary>
    internal static List<string> Differences(string recording)
    {
        var differences = new List<string>();
        foreach (var item in JsonNode.Parse(File.ReadAllText(recording))!.AsArray())
        {
            var op = Text(item!["op"])!;
            var args = item["args"]!.AsArray();
            var expected = item.AsObject().ContainsKey("error")
                ? $"error: {Text(item["error"])}"
                : JsJson.Stringify(item["result"]);
            string actual;
            try
            {
                actual = JsJson.Stringify(Run(op, args));
            }
            catch (ManagerException error)
            {
                actual = $"error: {error.Message}";
            }
            if (actual != expected)
                differences.Add($"{op} {Text(item["file"]) ?? JsJson.Stringify(args)}\n  Bun: {expected}\n  C#:  {actual}");
        }
        return differences;
    }

    [Test]
    public async Task The_edit_engine_matches_the_Bun_version_on_the_recorded_corpus()
    {
        var differences = Differences(Path.Combine(BunFixtures.Directory, "bun-edits.json"));
        if (differences.Count > 0) Assert.Fail($"{differences.Count} differences:\n{string.Join("\n", differences.Take(20))}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// A one-off recording of a real flake (<c>record-bun-edits.ts --files DIR --out FILE</c>), replayed when
    /// <c>NIX_MANAGER_BUN_EDITS</c> names it.
    /// </summary>
    [Test]
    public async Task The_edit_engine_matches_the_Bun_version_on_a_recorded_flake()
    {
        var recording = Environment.GetEnvironmentVariable("NIX_MANAGER_BUN_EDITS");
        Skip.When(string.IsNullOrEmpty(recording), "NIX_MANAGER_BUN_EDITS is not set.");
        var differences = Differences(recording!);
        if (differences.Count > 0) Assert.Fail($"{differences.Count} differences:\n{string.Join("\n", differences.Take(20))}");
        await Task.CompletedTask;
    }
}
