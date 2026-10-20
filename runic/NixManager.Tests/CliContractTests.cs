using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Cli;
using Runic.CommandLine.Testing;

namespace NixManager.Tests;

/// <summary>
/// The command line's contract: syntax, results in human and JSON output, faults and exit codes. Every case of
/// <c>Fixtures/cli-cases*.json</c> runs over the same stored operations and is compared with the output recorded in
/// <c>Fixtures/recorded-cli*.json</c>: standard output (a <c>runic.commandline/1</c> envelope is compared as JSON),
/// standard error and the exit code.
/// </summary>
/// <remarks>
/// To record the cases again after a deliberate change, run the tests with <c>NIX_MANAGER_RECORD_CLI</c> set to an
/// empty directory: each case writes its output there as <c>NAME.json</c> instead of comparing it, to be reviewed
/// and copied into the recorded fixtures.
/// </remarks>
public sealed class CliContractTests
{
    private sealed record Recorded(string Name, string[] Args, int ExitCode, string Stdout, string Stderr);

    /// <param name="Volatile">Members whose values differ between runs, such as an operation's ID and times; compared as present only.</param>
    private sealed record Case(string Name, string[] Args, Dictionary<string, string>? Files, List<string>? Volatile = null);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The envelope's request ID differs in every run.</summary>
    private static readonly string[] EnvelopeVolatile = ["requestId"];

    /// <summary>Every recorded case of every <c>recorded-cli*.json</c>.</summary>
    private static readonly Dictionary<string, Recorded> Recordings = System.IO.Directory
        .EnumerateFiles(RecordedFixtures.Directory, "recorded-cli*.json")
        .SelectMany(file => JsonSerializer.Deserialize<List<Recorded>>(File.ReadAllText(file), Web)!)
        .ToDictionary(r => r.Name);

    /// <summary>The command lines and the files each case adds to the checkout (<c>cli-cases*.json</c>).</summary>
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

    public static IEnumerable<string> Cases() => CaseFiles.Keys;

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(RecordedFixtures fixtures, string[] args)
    {
        var console = new TestCommandConsole { IsOutputRedirected = true, IsErrorRedirected = true };
        var session = new CliSession
        {
            Environment = fixtures.Environment,
            // Nothing may run in these cases.
            Runner = new FakeRunner((command, _, _) => throw new InvalidOperationException($"Unexpected command {command.Label}")),
        };
        var exitCode = await NixManagerCli.RunAsync(args, session, console, handleSignals: false);
        return (exitCode, console.StandardOutput, console.StandardError);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task The_command_line_answers_as_recorded(string name)
    {
        var @case = CaseFiles[name];
        using var fixtures = new RecordedFixtures(@case.Files);
        var volatileNames = (@case.Volatile ?? []).Concat(EnvelopeVolatile).ToList();

        var (exitCode, stdout, stderr) = await RunAsync(fixtures, @case.Args);
        stdout = fixtures.NormalizeText(stdout);
        stderr = fixtures.NormalizeText(stderr);

        if (Environment.GetEnvironmentVariable("NIX_MANAGER_RECORD_CLI") is { Length: > 0 } record)
        {
            await File.WriteAllTextAsync(Path.Combine(record, $"{name}.json"),
                JsonSerializer.Serialize(new Recorded(name, @case.Args, exitCode, stdout, stderr), Web));
            return;
        }
        var recorded = Recordings[name];
        await Assert.That(recorded.Args).IsEquivalentTo(@case.Args);
        await Assert.That(exitCode).IsEqualTo(recorded.ExitCode).Because(stderr);
        await Assert.That(stderr).IsEqualTo(recorded.Stderr);
        if (recorded.Stdout.TrimStart().StartsWith('{') || recorded.Stdout.TrimStart().StartsWith('['))
        {
            var expected = WithoutVolatile(RecordedFixtures.NormalizeJson(JsonNode.Parse(recorded.Stdout)), volatileNames);
            var actual = WithoutVolatile(RecordedFixtures.NormalizeJson(JsonNode.Parse(stdout)), volatileNames);
            await Assert.That(RecordedFixtures.Difference(expected, actual)).IsNull();
        }
        else await Assert.That(stdout).IsEqualTo(RecordedFixtures.NormalizeRecordedText(recorded.Stdout));
    }

    [Test]
    public async Task Every_case_is_recorded() =>
        await Assert.That(CaseFiles.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(Recordings.Keys.Order(StringComparer.Ordinal));
}
