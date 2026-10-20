using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Runic.CommandLine.Processes;

namespace NixManager.Observation;

/// <summary>The checkout's Git state and file hashes (<c>git</c>, <c>hashText</c>, <c>readLock</c>, <c>repositoryState</c> in <c>src/observe.ts</c>).</summary>
public static partial class Repository
{
    /// <summary>SHA-256 of text as UTF-8, in lowercase hex (<c>hashText</c>).</summary>
    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));

    public static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The checkout's flake.lock, or empty text without one (<c>readLock</c>).</summary>
    public static string ReadLock(string flake)
    {
        try { return File.ReadAllText(Path.Combine(flake, "flake.lock")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static readonly IProcessRunner Processes = new ProcessRunner(new LocalExecutablePolicy());

    private static ProcessRequest Git(string flake, IEnumerable<string> args, int outputLimit) =>
        new("git", ["-C", flake, .. args], options: new ProcessExecutionOptions(standardOutputLimitBytes: outputLimit, standardErrorLimitBytes: 0)
        {
            StandardInput = ProcessStandardInput.Closed,
        });

    /// <summary>
    /// Runs <c>git -C FLAKE ARGS</c> with standard input closed and standard error discarded; its output, or
    /// null when it failed or wrote more than a capture holds.
    /// </summary>
    public static async Task<string?> GitAsync(string flake, IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        var result = await Processes.RunAsync(Git(flake, args, ProcessExecutionOptions.MaximumOutputLimitBytes), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result is { State: ProcessState.Exited, ExitCode: 0, StandardOutput.IsTruncated: false } ? result.StandardOutput.Text : null;
    }

    /// <summary>
    /// The SHA-256 of what <c>git -C FLAKE ARGS</c> writes, read as it streams so output of any size is hashed
    /// whole; null when it failed.
    /// </summary>
    private static async Task<string?> GitHashAsync(string flake, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var hash = new Hasher();
        var result = await Processes.RunAsync(Git(flake, args, 0), hash, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result is { State: ProcessState.Exited, ExitCode: 0 } ? hash.Finish() : null;
    }

    private sealed class Hasher : IProcessOutputHandler, IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public ValueTask OnChunkAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
        {
            if (chunk.Channel == ProcessOutputChannel.StandardOutput) _hash.AppendData(chunk.Bytes.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }

    public static Task<string?> GitAsync(string flake, params string[] args) => GitAsync(flake, (IEnumerable<string>)args);

    /// <summary>The checkout's Git state and a fingerprint of its content (<c>repositoryState</c>).</summary>
    public static async Task<RepositoryState> StateAsync(string flake, CancellationToken cancellationToken = default)
    {
        var status = GitAsync(flake, ["status", "--porcelain=v1", "--untracked-files=normal"], cancellationToken);
        var branch = GitAsync(flake, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        var head = GitAsync(flake, ["log", "-1", "--format=%H%x00%s%x00%cI"], cancellationToken);
        var lockDiff = GitAsync(flake, ["status", "--porcelain=v1", "--", "flake.lock"], cancellationToken);
        // Status alone misses further edits to an already modified file.
        var diff = GitHashAsync(flake, ["diff", "HEAD", "--binary"], cancellationToken);
        var upstream = GitAsync(flake, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], cancellationToken);
        await Task.WhenAll(status, branch, head, lockDiff, diff, upstream).ConfigureAwait(false);
        var lockText = ReadLock(flake);
        var parts = (await head.ConfigureAwait(false))?.Trim().Split('\0') ?? [];
        var statusText = await status.ConfigureAwait(false);
        var known = statusText ?? "unknown";
        var fingerprint = HashText($"{lockText}\0{known}\0{await diff.ConfigureAwait(false) ?? ""}");
        string? Part(int index) => parts.Length > index && parts[index].Length > 0 ? parts[index] : null;
        return new RepositoryState
        {
            Fingerprint = fingerprint,
            Clean = statusText == "",
            Changes = string.IsNullOrEmpty(statusText) ? [] : statusText.Split('\n').Where(line => line.Length > 0).ToList(),
            Branch = (await branch.ConfigureAwait(false))?.Trim() is { Length: > 0 } name ? name : null,
            Revision = Part(0),
            Subject = Part(1),
            CommittedAt = Part(2),
            Upstream = (await upstream.ConfigureAwait(false))?.Trim() is { Length: > 0 } remote ? remote : null,
            LockModified = !string.IsNullOrWhiteSpace(await lockDiff.ConfigureAwait(false)),
            LockHash = HashText(lockText),
        };
    }

    [GeneratedRegex(@"-(\d+)-link$")]
    private static partial Regex GenerationLink();

    /// <summary>The generation number a profile link points to, such as 79 for system-79-link (<c>profileGeneration</c>).</summary>
    public static int? ProfileGeneration(string profile)
    {
        try
        {
            var target = new FileInfo(profile).LinkTarget;
            var match = target is null ? null : GenerationLink().Match(target);
            return match is { Success: true } && int.TryParse(match.Groups[1].Value, out var number) && number > 0 ? number : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
