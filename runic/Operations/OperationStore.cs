using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using NixManager.Domain;

namespace NixManager.Operations;

/// <summary>
/// The operation records in the state directory: <c>operations/ID.json</c> with
/// <c>.log</c> and <c>.undo</c> beside it, <c>updates.json</c> and the shared <c>operation.lock</c>. Every manager
/// process reads and writes the same files and takes the same lock.
/// </summary>
public sealed partial class OperationStore(Config config)
{
    /// <summary>Stored logs stop growing here.</summary>
    public const long LogLimit = 16 * 1024 * 1024;
    /// <summary>How much of a log a reader gets by default.</summary>
    public const int LogTail = 256 * 1024;
    public const int HistoryLength = 50;

    public const string InterruptedError =
        "The manager exited before recording completion. Check the log and active generations before retrying.";

    private (string Key, List<Operation> Operations)? _historyCache;

    public Config Config => config;
    public string Directory => Paths.Operations(config);

    [GeneratedRegex("^[0-9]+-[a-f0-9-]+$")]
    private static partial Regex IdPattern();

    public string PathOf(string id, string extension)
    {
        if (!IdPattern().IsMatch(id)) throw new ManagerException("Invalid operation ID.");
        return Path.Combine(Directory, $"{id}.{extension}");
    }

    /// <summary>A new operation ID: milliseconds since the epoch and a UUID, so names sort by start.</summary>
    public static string NewId(TimeProvider clock) =>
        $"{clock.GetUtcNow().ToUnixTimeMilliseconds()}-{Guid.NewGuid():D}";

    public void EnsureDirectory()
    {
        CreatePrivateDirectory(config.StateDir);
        CreatePrivateDirectory(Directory);
    }

    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(path);
        else System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Persist(Operation operation) => AtomicJson(PathOf(operation.Id, "json"), ManagerJson.Serialize(operation));

    /// <summary>Writes a file by renaming a private temporary file over it (<c>atomicJson</c>).</summary>
    public static void AtomicJson(string path, string json)
    {
        var temporary = $"{path}.{Guid.NewGuid():D}.tmp";
        try
        {
            WritePrivate(temporary, json + "\n", FileMode.CreateNew);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WritePrivate(string path, string text, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        if (mode == FileMode.Append) stream.Seek(0, SeekOrigin.End);
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
    }

    public void AppendLog(string id, string text) => WritePrivate(PathOf(id, "log"), text, FileMode.Append);

    public Operation? Read(string id)
    {
        var path = PathOf(id, "json");
        return File.Exists(path) ? ReadFile(path) : null;
    }

    private static Operation? ReadFile(string path)
    {
        try
        {
            return ManagerJson.Deserialize<Operation>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The latest operations for this checkout, newest first. A record still marked running whose process is gone
    /// reads as interrupted; <paramref name="live"/> replaces the record of the operation this process runs.
    /// </summary>
    public IReadOnlyList<Operation> History(Operation? live = null)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        // Every persisted change renames a file into the directory, which updates its modification time; reuse
        // the parsed list until then.
        var key = System.IO.Directory.GetLastWriteTimeUtc(Directory).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<Operation> operations;
        if (_historyCache is { } cache && cache.Key == key) operations = cache.Operations;
        else
        {
            operations = System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .Reverse()
                .Take(HistoryLength)
                .Select(name => ReadFile(Path.Combine(Directory, name)))
                .OfType<Operation>()
                .Where(operation => operation.Flake == config.Flake)
                .ToList();
            _historyCache = (key, operations);
        }
        return operations.Select(operation => WithLiveState(operation, live)).ToList();
    }

    private static Operation WithLiveState(Operation operation, Operation? live)
    {
        if (live is not null && operation.Id == live.Id) return live;
        if (operation.Status != OperationStatuses.Running || Native.Alive(operation.Pid)) return operation;
        var copy = operation.Clone();
        copy.Status = OperationStatuses.Interrupted;
        copy.Error = InterruptedError;
        return copy;
    }

    /// <summary>The end of an operation's log, from a whole line on.</summary>
    public LogResult Log(string id, int limit = LogTail)
    {
        var path = PathOf(id, "log");
        if (!File.Exists(path)) return new LogResult("", 0, false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var size = stream.Length;
        var length = (int)Math.Min(size, limit);
        var buffer = new byte[length];
        stream.Seek(size - length, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer);
        // Drop a partial first line (and any split UTF-8 sequence) from the tail.
        if (length < size) text = text[(text.IndexOf('\n') + 1)..];
        return new LogResult(text, size, length < size);
    }

    public UpdateReport? Updates()
    {
        try
        {
            var saved = ManagerJson.Deserialize<SavedUpdates>(File.ReadAllText(Path.Combine(config.StateDir, "updates.json")));
            return saved?.Flake == config.Flake ? saved.Report : null;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void SaveUpdates(UpdateReport report)
    {
        try
        {
            AtomicJson(Path.Combine(config.StateDir, "updates.json"), ManagerJson.Serialize(new SavedUpdates(config.Flake, report)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ManagerException($"Save update report: {e.Message}");
        }
    }

    /// <summary>
    /// Takes the operation lock that every manager process shares: an exclusive
    /// <c>flock</c> on <c>operation.lock</c>, which the system releases when the process ends.
    /// </summary>
    public IDisposable AcquireLock()
    {
        CreatePrivateDirectory(config.StateDir);
        var path = Path.Combine(config.StateDir, "operation.lock");
        FileStream stream;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            stream = new FileStream(path, options);
        }
        catch (IOException)
        {
            throw Busy();
        }
        if (!Native.TryLockExclusive(stream.SafeFileHandle))
        {
            stream.Dispose();
            throw Busy();
        }
        // Closing releases flock, including on crashes. Never unlink the lock inode.
        return stream;
    }

    /// <summary>Why an operation cannot start while another runs, in any manager process.</summary>
    internal static ManagerException Busy() => new("Another nix-manager operation is running. Wait for it to finish.");
}

public static partial class Native
{
    private const int LockExclusive = 2, LockNonBlocking = 4, Eperm = 1;

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(SafeFileHandle fd, int operation);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint RealPathCore(string path, nint resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(nint pointer);

    /// <summary>The path with every symbolic link resolved (<c>realpath(3)</c>), or null when it does not exist.</summary>
    public static string? RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        var resolved = RealPathCore(path, 0);
        if (resolved == 0) return null;
        try { return Marshal.PtrToStringUTF8(resolved); }
        finally { Free(resolved); }
    }

    public static bool TryLockExclusive(SafeFileHandle handle) =>
        OperatingSystem.IsWindows() || Flock(handle, LockExclusive | LockNonBlocking) == 0;

    /// <summary>Whether a process exists, as <c>process.kill(pid, 0)</c> tells (a foreign process counts).</summary>
    public static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        if (OperatingSystem.IsWindows())
        {
            try { using var _ = System.Diagnostics.Process.GetProcessById(pid); return true; }
            catch (ArgumentException) { return false; }
        }
        return Kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == Eperm;
    }
}
