using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Cli;
using NixManager.Execution;
using Runic.CommandLine.Testing;

namespace NixManager.Tests;

/// <summary>
/// The software edit commands that read the configuration (add, remove, set, unset) answer as the Bun version:
/// every case of <c>Fixtures/software-edit-cases.json</c> runs with the same answers from <c>nix</c> that
/// <c>record-bun-software-edits.py</c> gave the Bun version, and standard output, the notes and error on standard
/// error, the exit code and the saved draft are compared with <c>Fixtures/bun-software-edits.json</c>.
/// </summary>
/// <remarks>These need the edit engine (W300-033 edits slice): before it is merged they fail in its placeholders.</remarks>
public sealed class SoftwareEditTests
{
    private sealed record Recorded(string Name, string[] Args, int ExitCode, string Stdout, List<string> Stderr, string? Error, JsonArray? Draft);

    private sealed record Case(string Name, Dictionary<string, string>? Files, JsonNode Eval);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, Recorded> Recordings =
        JsonSerializer.Deserialize<List<Recorded>>(File.ReadAllText(Path.Combine(BunFixtures.Directory, "bun-software-edits.json")), Web)!
            .ToDictionary(r => r.Name);

    private static readonly Dictionary<string, Case> Cases =
        JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(BunFixtures.Directory, "software-edit-cases.json")), Web)!
            .ToDictionary(c => c.Name);

    public static IEnumerable<string> Names() => Recordings.Keys;

    /// <summary>Answers as the recorder's fake <c>nix</c>: the checkout is the flake source, evaluation prints the case's document.</summary>
    internal static FakeRunner Nix(BunFixtures fixtures, string evaluation) => new((command, _, _) => Task.FromResult(command.Argv switch
    {
        ["nix", "flake", "metadata", ..] => new CommandResult(0, $$"""{"path":"{{fixtures.Root}}"}""", ""),
        ["nix", "eval", ..] => new CommandResult(0, evaluation.Replace("<flake>", fixtures.Root, StringComparison.Ordinal), ""),
        _ => command.AllowFailure == true
            ? new CommandResult(1, "", $"Cannot start {command.Argv[0]}")
            : throw new Domain.ManagerException($"Cannot start {command.Argv[0]}"),
    }));

    [Test]
    [MethodDataSource(nameof(Names))]
    public async Task Edit_commands_answer_as_the_Bun_version(string name)
    {
        var recorded = Recordings[name];
        var test = Cases[name];
        using var fixtures = new BunFixtures(test.Files);
        var console = new TestCommandConsole { IsOutputRedirected = true, IsErrorRedirected = true };
        var session = new CliSession
        {
            Output = new CliOutput(),
            Environment = fixtures.Environment,
            // Relaxed escaping keeps <flake> as written, for the replacement.
            Runner = Nix(fixtures, test.Eval.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })),
        };

        var exitCode = await NixManagerCli.RunAsync(recorded.Args, session, console, handleSignals: false);

        var stderr = console.StandardError.Split('\n').Where(line => line.Trim().Length > 0).Select(fixtures.NormalizeText).ToList();
        await Assert.That(exitCode).IsEqualTo(recorded.ExitCode).Because(console.StandardError);
        await Assert.That(string.Join("\n", stderr)).IsEqualTo(string.Join("\n", recorded.Stderr));
        var stdout = fixtures.NormalizeText(console.StandardOutput);
        if (recorded.Stdout.TrimStart().StartsWith('{'))
            await Assert.That(BunFixtures.Difference(JsonNode.Parse(recorded.Stdout), JsonNode.Parse(stdout))).IsNull();
        else await Assert.That(stdout).IsEqualTo(recorded.Stdout);
        var draft = Path.Combine(fixtures.StateDir, "draft.json");
        if (recorded.Draft is null) await Assert.That(File.Exists(draft)).IsFalse();
        else
        {
            var saved = JsonNode.Parse(fixtures.NormalizeText(File.ReadAllText(draft)))!;
            await Assert.That(saved["flake"]!.GetValue<string>()).IsEqualTo("<flake>");
            // Member order matters: the review compares the draft's hash of JSON.stringify text.
            await Assert.That(saved["changes"]!.ToJsonString()).IsEqualTo(recorded.Draft.ToJsonString());
        }
    }
}
