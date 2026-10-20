using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Cli;
using Runic.CommandLine.Testing;

namespace NixManager.Tests;

/// <summary>
/// The command line keeps the Bun version's syntax, JSON documents, exit codes and error messages: every case of
/// <c>Fixtures/cli-cases.json</c> runs here and is compared with the Bun version's output recorded in
/// <c>Fixtures/recorded-cli.json</c> (<c>record-bun-cli.py</c>) over the same stored operations.
/// </summary>
public sealed class CliParityTests
{
    private sealed record Recorded(string Name, string[] Args, int ExitCode, string Stdout, string? Error);

    /// <param name="Volatile">Members whose values differ between runs, such as an operation's ID and times; compared as present only.</param>
    private sealed record Case(string Name, Dictionary<string, string>? Files, List<string>? Volatile = null);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Every recorded case of every <c>bun-cli*.json</c>.</summary>
    private static readonly Dictionary<string, Recorded> Recordings = System.IO.Directory
        .EnumerateFiles(RecordedFixtures.Directory, "recorded-cli*.json")
        .SelectMany(file => JsonSerializer.Deserialize<List<Recorded>>(File.ReadAllText(file), Web)!)
        .ToDictionary(r => r.Name);

    /// <summary>The files each case adds to the checkout (<c>cli-cases*.json</c>).</summary>
    private static readonly Dictionary<string, Case> CaseFiles = System.IO.Directory
        .EnumerateFiles(RecordedFixtures.Directory, "cli-cases*.json")
        .SelectMany(file => JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(file), Web)!)
        .ToDictionary(c => c.Name);

    /// <summary>The document with the values of <paramref name="names"/> replaced, at any depth.</summary>
    private static JsonNode? WithoutVolatile(JsonNode? node, IReadOnlyCollection<string> names)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var (name, child) in value.ToList())
                    value[name] = names.Contains(name) ? JsonValue.Create("<volatile>") : WithoutVolatile(child?.DeepClone(), names);
                return value;
            case JsonArray array:
                return new JsonArray([.. array.Select(item => WithoutVolatile(item?.DeepClone(), names))]);
            default:
                return node;
        }
    }

    public static IEnumerable<string> Cases() => Recordings.Keys;

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(RecordedFixtures fixtures, string[] args)
    {
        var console = new TestCommandConsole { IsOutputRedirected = true, IsErrorRedirected = true };
        var session = new CliSession
        {
            Output = new CliOutput(),
            Environment = fixtures.Environment,
            // Nothing may run in these cases.
            Runner = new FakeRunner((command, _, _) => throw new InvalidOperationException($"Unexpected command {command.Label}")),
        };
        var exitCode = await NixManagerCli.RunAsync(args, session, console, handleSignals: false);
        return (exitCode, console.StandardOutput, console.StandardError);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task The_command_line_answers_as_the_Bun_version(string name)
    {
        var recorded = Recordings[name];
        var @case = CaseFiles.GetValueOrDefault(name);
        using var fixtures = new RecordedFixtures(@case?.Files);
        var volatileNames = @case?.Volatile ?? [];

        var (exitCode, stdout, stderr) = await RunAsync(fixtures, recorded.Args);

        await Assert.That(exitCode).IsEqualTo(recorded.ExitCode).Because(stderr);
        var error = stderr.Split('\n').FirstOrDefault(line => line.StartsWith("Error: ", StringComparison.Ordinal));
        await Assert.That(error is null ? null : fixtures.NormalizeText(error)).IsEqualTo(recorded.Error);
        if (recorded.Stdout.TrimStart().StartsWith('{') || recorded.Stdout.TrimStart().StartsWith('['))
        {
            var expected = WithoutVolatile(RecordedFixtures.NormalizeJson(JsonNode.Parse(recorded.Stdout)), volatileNames);
            var actual = WithoutVolatile(RecordedFixtures.NormalizeJson(JsonNode.Parse(fixtures.NormalizeText(stdout))), volatileNames);
            await Assert.That(RecordedFixtures.Difference(expected, actual)).IsNull();
        }
        else await Assert.That(fixtures.NormalizeText(stdout)).IsEqualTo(RecordedFixtures.NormalizeRecordedText(recorded.Stdout));
    }
}
