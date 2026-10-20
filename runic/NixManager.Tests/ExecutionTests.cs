using System.Diagnostics;
using System.Text.Json;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Operations;

namespace NixManager.Tests;

/// <summary>Live process output, Nix's structured log and cancellation of real child processes.</summary>
public sealed class ExecutionTests
{
    private static string Nix(object message) => "@nix " + JsonSerializer.Serialize(message);

    private static Command Shell(string script, bool progress = false) =>
        new() { Label = DisplayText.Plain("fixture"), Argv = ["/bin/sh", "-c", script], Progress = progress ? true : null };

    [Test]
    public async Task Structured_Nix_logs_become_readable_lines_and_aggregated_progress()
    {
        // Each structured Nix log event is parsed in turn, and the progress after each one is checked.
        var progress = new NixProgress();
        await Assert.That(progress.Snapshot().Evaluating).IsTrue();
        await Assert.That(progress.Line("plain output")).IsEqualTo("plain output");
        await Assert.That(progress.Line(Nix(new { action = "msg", level = 3, msg = "\u001b[1mthis derivation will be built:\u001b[0m" })))
            .IsEqualTo("this derivation will be built:");
        await Assert.That(progress.Line(Nix(new { action = "msg", level = 4, msg = "evaluating file" }))).IsNull();
        await Assert.That(progress.Line(Nix(new { action = "start", id = 1, level = 0, parent = 0, text = "", type = 104 }))).IsNull();
        progress.Line(Nix(new { action = "result", id = 1, type = 105, fields = new[] { 1, 3, 1, 0 } }));
        await Assert.That(progress.Line(Nix(new
        {
            action = "start", id = 2, level = 3, parent = 0, type = 105,
            text = "building '/nix/store/6mzmgw0jjdrr5bwzhk96ff3jgg56mdx5-hello-2.12.drv'",
            fields = new object[] { "/nix/store/6mzmgw0jjdrr5bwzhk96ff3jgg56mdx5-hello-2.12.drv", "", 1, 1 },
        }))).IsEqualTo("building '/nix/store/6mzmgw0jjdrr5bwzhk96ff3jgg56mdx5-hello-2.12.drv'");
        progress.Line(Nix(new { action = "result", id = 2, type = 104, fields = new[] { "buildPhase" } }));
        await Assert.That(progress.Line(Nix(new { action = "result", id = 2, type = 101, fields = new[] { "compiling" } }))).IsEqualTo("hello-2.12> compiling");
        progress.Line(Nix(new { action = "start", id = 3, level = 0, parent = 0, text = "", type = 102 }));
        progress.Line(Nix(new { action = "result", id = 3, type = 106, fields = new[] { 101, 4096 } }));
        var snapshot = progress.Snapshot();
        await Assert.That(snapshot.Evaluating).IsFalse();
        await Assert.That(snapshot.Builds).IsEqualTo(new BuildCounts { Done = 1, Expected = 3, Running = 1, Failed = 0 });
        await Assert.That(snapshot.Bytes.Expected).IsEqualTo(4096);
        await Assert.That(snapshot.Current).IsEquivalentTo(["hello-2.12 (buildPhase)"]);
        progress.Line(Nix(new { action = "stop", id = 2 }));
        progress.Line(Nix(new { action = "result", id = 1, type = 105, fields = new[] { 2, 3, 0, 0 } }));
        await Assert.That(progress.Snapshot().Builds.Done).IsEqualTo(2);
        await Assert.That(progress.Snapshot().Current).IsEmpty();
        await Assert.That(progress.Line("@nix {not json")).IsEqualTo("@nix {not json");
    }

    [Test]
    public async Task Output_arrives_line_by_line_while_the_process_runs()
    {
        var events = new List<(TimeSpan At, OutputEvent Event)>();
        var clock = Stopwatch.StartNew();
        var result = await new ProcessCommandRunner().RunAsync(
            Shell("echo first; echo 'to stderr' >&2; sleep 0.5; printf 'last without newline'"),
            e => { lock (events) events.Add((clock.Elapsed, e)); }, CancellationToken.None);

        await Assert.That(result.Code).IsEqualTo(0);
        // The result keeps standard output exactly as written, including a final line without a newline.
        await Assert.That(result.Stdout).IsEqualTo("first\nlast without newline");
        await Assert.That(result.Stderr).IsEqualTo("to stderr\n");
        var text = events.Select(e => e.Event).OfType<TextOutput>().ToList();
        await Assert.That(text.Select(t => (t.Stream, t.Text)))
            .IsEquivalentTo([("stdout", "first\n"), ("stderr", "to stderr\n"), ("stdout", "last without newline\n")]);
        // The first line arrived before the process slept, not after it exited.
        await Assert.That(events[0].At).IsLessThan(events[^1].At - TimeSpan.FromMilliseconds(300));
    }

    [Test]
    public async Task A_line_longer_than_the_runner_passes_at_once_stays_whole()
    {
        // nix eval --json prints a document of megabytes on one line; Runic passes lines over 64 Ki characters in parts.
        var result = await new ProcessCommandRunner().RunAsync(
            Shell("""printf '["'; head -c 200000 /dev/zero | tr '\0' 'x'; printf '","\303\244"]\n'""") with { Quiet = true },
            _ => { }, CancellationToken.None);

        await Assert.That(result.Stdout.Length).IsEqualTo(200000 + 9);
        await Assert.That(result.Stdout).DoesNotContain("x\nx");
        await Assert.That(System.Text.Json.Nodes.JsonNode.Parse(result.Stdout)![1]!.GetValue<string>()).IsEqualTo("ä");
    }

    [Test]
    public async Task Structured_stderr_is_converted_and_reports_progress()
    {
        var lines = new[]
        {
            Nix(new { action = "msg", level = 0, msg = "error: attribute missing" }),
            Nix(new { action = "start", id = 7, level = 0, parent = 0, text = "", type = 104 }),
            Nix(new { action = "result", id = 7, type = 105, fields = new[] { 0, 2, 0, 0 } }),
        };
        var script = string.Concat(lines.Select(line => $"printf '%s\\n' '{line}' >&2;"));
        var events = new List<OutputEvent>();

        var result = await new ProcessCommandRunner().RunAsync(Shell(script, progress: true), events.Add, CancellationToken.None);

        await Assert.That(result.Stderr).IsEqualTo("error: attribute missing\n");
        await Assert.That(string.Concat(events.OfType<TextOutput>().Select(t => t.Text))).IsEqualTo("error: attribute missing\n");
        await Assert.That(events.OfType<ProgressOutput>().Last().Progress.Builds.Expected).IsEqualTo(2);
    }

    [Test]
    public async Task A_failing_command_reports_its_exit_code_error_output_and_explanation()
    {
        var error = await Assert.That(async () => _ = await new ProcessCommandRunner().RunAsync(
                Shell("echo 'error: writing to file: No space left on device' >&2; exit 3"), _ => { }, CancellationToken.None))
            .Throws<ManagerException>();

        await Assert.That(error!.Message).IsEqualTo("fixture failed (exit 3). error: writing to file: No space left on device");
        await Assert.That(error.Explanation!.Title.English).IsEqualTo("The disk is full");
    }

    [Test]
    public async Task A_command_may_allow_failure()
    {
        var result = await new ProcessCommandRunner().RunAsync(Shell("exit 4") with { AllowFailure = true }, _ => { }, CancellationToken.None);
        await Assert.That(result.Code).IsEqualTo(4);
    }

    [Test]
    public async Task Cancellation_ends_the_whole_process_group()
    {
        // A duration only this test uses, to find its sleep among the system's processes.
        var marker = $"30.{Random.Shared.Next(100_000, 999_999)}";
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        // A grandchild in the same group, which a kill of the child alone would leave behind.
        var run = new ProcessCommandRunner().RunAsync(
            Shell($"sleep {marker} & echo started; wait"),
            e => { if (e is TextOutput { Text: "started\n" }) started.TrySetResult(); }, cancellation.Token);
        await started.Task;
        var clock = Stopwatch.StartNew();

        await cancellation.CancelAsync();

        await Assert.That(async () => _ = await run).Throws<OperationCanceledException>();
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(Directory.EnumerateDirectories("/proc").Any(p => Read(Path.Combine(p, "cmdline")).Contains(marker))).IsFalse();
    }

    private static string Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
