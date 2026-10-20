using NixManager.Domain;

namespace NixManager.Execution;

/// <summary>
/// File system failures as they are reported: <c>`${label}: ${error}`</c>, with the error text in the form Node
/// uses, such as <c>Error: ENOENT: no such file or directory, open 'PATH'</c>.
/// </summary>
public static class NodeErrors
{
    /// <summary>The text of <c>`${error}`</c> for an exception of a file system call on <paramref name="path"/>.</summary>
    /// <param name="syscall">The Node call that failed, such as <c>open</c>, <c>scandir</c> or <c>mkdir</c>.</param>
    public static string Describe(Exception error, string? path = null, string syscall = "open")
    {
        var target = path is null ? "" : $", {syscall} '{path}'";
        return error switch
        {
            ManagerException => error.Message,
            FileNotFoundException or DirectoryNotFoundException => $"Error: ENOENT: no such file or directory{target}",
            UnauthorizedAccessException => $"Error: EACCES: permission denied{target}",
            IOException when error.HResult == 17 || error.Message.Contains("exists", StringComparison.OrdinalIgnoreCase) => $"Error: EEXIST: file already exists{target}",
            _ => $"Error: {error.Message}",
        };
    }

    /// <summary><c>attempt(label, f)</c>: runs <paramref name="action"/>, failing as <c>LABEL: ERROR</c>.</summary>
    public static T Attempt<T>(string label, Func<T> action, string? path = null, string syscall = "open")
    {
        try { return action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ManagerException or InvalidOperationException or NotSupportedException)
        {
            throw new ManagerException($"{label}: {Describe(e, path, syscall)}");
        }
    }

    public static void Attempt(string label, Action action, string? path = null, string syscall = "open") =>
        Attempt(label, () => { action(); return 0; }, path, syscall);

    public static async Task<T> AttemptAsync<T>(string label, Func<Task<T>> action, string? path = null, string syscall = "open")
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ManagerException or InvalidOperationException or NotSupportedException)
        {
            throw new ManagerException($"{label}: {Describe(e, path, syscall)}");
        }
    }
}
