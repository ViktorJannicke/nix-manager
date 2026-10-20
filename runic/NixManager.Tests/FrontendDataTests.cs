using System.Text.RegularExpressions;
using NixManager.Edits;
using NixManager.Software;

namespace NixManager.Tests;

/// <summary>
/// The dashboard's curated features and Computer page settings (Frontend/src/lib/curated.ts and computer.ts) name
/// the options that <see cref="CuratedOptions"/> has the inventory read; once the Bun version is removed, these
/// files are the source of both, so a feature or setting added to one must be added to the other.
/// </summary>
public sealed partial class FrontendDataTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(RecordedFixtures.Directory, "frontend", name));

    [Test]
    public async Task Every_curated_feature_has_the_options_the_inventory_reads()
    {
        var text = Read("curated.ts");
        var blocks = ExperienceStart().Split(text[text.IndexOf("export const experiences", StringComparison.Ordinal)..]).Skip(1).ToList();
        var features = blocks.Select(block =>
        {
            var id = Quoted().Match(block).Groups[1].Value;
            var scope = Scope().Match(block).Groups[1].Value;
            var enable = Enable().Match(block).Groups[1].Value;
            var settings = Settings().Match(block).Groups[1].Value;
            return new CuratedExperienceOptions(id, scope,
                [.. Key().Matches(enable).Select(match => match.Groups[1].Value)],
                [.. Option().Matches(settings).Select(match => match.Groups[1].Value)]);
        }).ToList();

        await Assert.That(features.Select(feature => feature.Id)).IsEquivalentTo(CuratedOptions.Experiences.Select(feature => feature.Id),
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        foreach (var (feature, expected) in features.Zip(CuratedOptions.Experiences))
        {
            await Assert.That(feature.Scope).IsEqualTo(expected.Scope).Because(feature.Id);
            await Assert.That(feature.Enable).IsEquivalentTo(expected.Enable, TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(feature.Id);
            await Assert.That(feature.Settings).IsEquivalentTo(expected.Settings, TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(feature.Id);
        }
    }

    [Test]
    public async Task The_Computer_page_shows_the_options_the_inventory_reads()
    {
        var options = Option().Matches(Read("computer.ts")).Select(match => match.Groups[1].Value).Append("users.mutableUsers");
        await Assert.That(options).IsEquivalentTo(CuratedOptions.Computer, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(CuratedOptions.For(Targets.System)).Contains("users.mutableUsers");
    }

    [GeneratedRegex(@"\n  \{\n    id: ")]
    private static partial Regex ExperienceStart();

    [GeneratedRegex("^\"([^\"]+)\"")]
    private static partial Regex Quoted();

    [GeneratedRegex("scope: \"(home|system)\"")]
    private static partial Regex Scope();

    [GeneratedRegex(@"enable: \{(.*?)\n    \}", RegexOptions.Singleline)]
    private static partial Regex Enable();

    [GeneratedRegex(@"settings: \[(.*?)\]", RegexOptions.Singleline)]
    private static partial Regex Settings();

    [GeneratedRegex("\"([A-Za-z0-9_.-]+)\":")]
    private static partial Regex Key();

    [GeneratedRegex("option: \"([^\"]+)\"")]
    private static partial Regex Option();
}
