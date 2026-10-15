using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Cli;
using Runic.CommandLine.Testing;

namespace NixManager.Tests;

/// <summary>
/// The command line keeps the Bun version's syntax, JSON documents, exit codes and error messages: every case of
/// <c>Fixtures/cli-cases.json</c> runs here and is compared with the Bun version's output recorded in
/// <c>Fixtures/bun-cli.json</c> (<c>record-bun-cli.py</c>) over the same stored operations.
/// </summary>
public sealed class CliParityTests
{
    private sealed record Recorded(string Name, string[] Args, int ExitCode, string Stdout, string? Error);

    private static readonly Dictionary<string, Recorded> Recordings =
        JsonSerializer.Deserialize<List<Recorded>>(File.ReadAllText(Path.Combine(BunFixtures.Directory, "bun-cli.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ToDictionary(r => r.Name);

    public static IEnumerable<string> Cases() => Recordings.Keys;

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(BunFixtures fixtures, string[] args)
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
        using var fixtures = new BunFixtures();

        var (exitCode, stdout, stderr) = await RunAsync(fixtures, recorded.Args);

        await Assert.That(exitCode).IsEqualTo(recorded.ExitCode).Because(stderr);
        var error = stderr.Split('\n').FirstOrDefault(line => line.StartsWith("Error: ", StringComparison.Ordinal));
        await Assert.That(error is null ? null : fixtures.NormalizeText(error)).IsEqualTo(recorded.Error);
        if (recorded.Stdout.TrimStart().StartsWith('{') || recorded.Stdout.TrimStart().StartsWith('['))
        {
            var expected = BunFixtures.NormalizeJson(JsonNode.Parse(recorded.Stdout));
            var actual = BunFixtures.NormalizeJson(JsonNode.Parse(fixtures.NormalizeText(stdout)));
            await Assert.That(BunFixtures.Difference(expected, actual)).IsNull();
        }
        else await Assert.That(fixtures.NormalizeText(stdout)).IsEqualTo(BunFixtures.NormalizeRecordedText(recorded.Stdout));
    }
}
