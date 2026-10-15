using System.Text;
using NixManager.Domain;
using NixManager.Nix;
using NixManager.Operations;
using Runic.CommandLine.Processes;

namespace NixManager.Execution;

/// <summary>Output of a running step: text of one stream, or a build progress snapshot.</summary>
public abstract record OutputEvent;

/// <param name="Stream"><c>stdout</c> or <c>stderr</c>.</param>
/// <param name="Text">Whole lines, each ending with a line feed.</param>
public sealed record TextOutput(string Stream, string Text) : OutputEvent;

public sealed record ProgressOutput(BuildProgress Progress) : OutputEvent;

public sealed record CommandResult(int Code, string Stdout, string Stderr);

/// <summary>Runs one step's process (<c>Runner</c> in <c>src/process.ts</c>).</summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs <paramref name="command"/>, passing its output to <paramref name="output"/> as it arrives. Fails with
    /// <see cref="ManagerException"/> on a nonzero exit unless the command allows failure, and with
    /// <see cref="OperationCanceledException"/> after cancellation, once the process group has ended.
    /// </summary>
    Task<CommandResult> RunAsync(Command command, Action<OutputEvent> output, CancellationToken cancellationToken);
}

/// <summary>
/// Runs steps with Runic Command Line's process runner: an argument vector without a shell, closed standard
/// input, a new session (no controlling terminal), and on cancellation SIGTERM to the process group, a grace
/// period, then SIGKILL, as the Bun version does. Output is streamed line by line with backpressure; Nix's
/// structured log becomes readable lines and throttled progress snapshots.
/// </summary>
public sealed class ProcessCommandRunner(IProcessRunner? runner = null, TimeProvider? clock = null) : ICommandRunner
{
    /// <summary>The tail of each stream a result keeps, as the Bun version does.</summary>
    public const int CaptureLimit = 8 * 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(3);

    private readonly IProcessRunner _runner = runner ?? new ProcessRunner(new LocalExecutablePolicy());
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<CommandResult> RunAsync(Command command, Action<OutputEvent> output, CancellationToken cancellationToken)
    {
        if (command.Argv.Count == 0) throw new ManagerException($"{command.Label}: nothing to run.");
        // The process runner keeps no capture of its own: the handler keeps the converted text.
        var options = new ProcessExecutionOptions(standardOutputLimitBytes: 0, standardErrorLimitBytes: 0, drainGracePeriod: GracePeriod)
        {
            StandardInput = ProcessStandardInput.Closed,
            DetachFromTerminal = true,
        };
        var request = new ProcessRequest(command.Argv[0], command.Argv.Skip(1).ToArray(), command.Cwd,
            command.Env?.ToDictionary(pair => pair.Key, pair => (string?)pair.Value), options);
        var handler = new Handler(command, output, _clock);
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(request, handler, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        handler.Finish();
        switch (result.State)
        {
            case ProcessState.Cancelled:
                throw new OperationCanceledException(cancellationToken);
            case ProcessState.Exited:
                break;
            default:
                throw new ManagerException(
                    $"Cannot start {command.Argv[0]}: {result.Fault?.Message ?? result.State.ToString()}");
        }
        var code = result.ExitCode ?? -1;
        var stdout = handler.Stdout;
        var stderr = handler.Stderr;
        if (code != 0 && command.AllowFailure != true)
        {
            var tail = stderr.Length > 1500 ? stderr[^1500..] : stderr;
            throw new ManagerException($"{command.Label} failed (exit {code}). {tail.Trim()}", Explain.Explainer.Explain(stderr));
        }
        return new CommandResult(code, stdout, stderr);
    }

    private sealed class Handler(Command command, Action<OutputEvent> output, TimeProvider clock) : IProcessOutputHandler
    {
        private readonly NixProgress? _nix = command.Progress == true ? new NixProgress() : null;
        private readonly Capture _stdout = new(command.FullOutput == true ? int.MaxValue : CaptureLimit);
        private readonly Capture _stderr = new(CaptureLimit);
        private readonly bool _quiet = command.Quiet == true;
        private DateTimeOffset _lastProgress = DateTimeOffset.MinValue;

        private readonly Decoder _stdoutDecoder = new UTF8Encoding(false).GetDecoder();

        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();

        /// <summary>
        /// Standard output is kept as the child wrote it, from the raw bytes: the runner passes a line longer than
        /// 64 Ki characters in parts, so lines would break a large <c>nix eval --json</c> document.
        /// </summary>
        public ValueTask OnChunkAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
        {
            if (chunk.Channel != ProcessOutputChannel.StandardOutput) return ValueTask.CompletedTask;
            var chars = new char[_stdoutDecoder.GetCharCount(chunk.Bytes.Span, flush: false)];
            _stdoutDecoder.GetChars(chunk.Bytes.Span, chars, flush: false);
            _stdout.Append(new string(chars));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            if (line.Channel == ProcessOutputChannel.StandardOutput || _nix is null)
            {
                var stdout = line.Channel == ProcessOutputChannel.StandardOutput;
                var text = line.Text + "\n";
                if (!stdout) _stderr.Append(text);
                if (!_quiet) output(new TextOutput(stdout ? "stdout" : "stderr", text));
                return ValueTask.CompletedTask;
            }
            // Nix's structured log is converted to readable lines and throttled progress snapshots.
            if (_nix.Line(line.Text) is { } readable)
            {
                _stderr.Append(readable + "\n");
                if (!_quiet) output(new TextOutput("stderr", readable + "\n"));
            }
            if (!_quiet && clock.GetUtcNow() - _lastProgress > ProgressInterval)
            {
                _lastProgress = clock.GetUtcNow();
                output(new ProgressOutput(_nix.Snapshot()));
            }
            return ValueTask.CompletedTask;
        }

        public void Finish()
        {
            var rest = new char[_stdoutDecoder.GetCharCount([], flush: true)];
            _stdoutDecoder.GetChars([], rest, flush: true);
            if (rest.Length > 0) _stdout.Append(new string(rest));
            if (_nix is not null && !_quiet) output(new ProgressOutput(_nix.Snapshot()));
        }
    }

    /// <summary>The last <c>limit</c> characters of a stream.</summary>
    private sealed class Capture(int limit)
    {
        private readonly StringBuilder _text = new();

        public void Append(string text)
        {
            _text.Append(text);
            if (_text.Length > limit) _text.Remove(0, _text.Length - limit);
        }

        public override string ToString() => _text.ToString();
    }
}
