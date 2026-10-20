using NixManager.Domain;

namespace NixManager.Execution;

/// <summary>File system failures in words: what failed, <c>STEP: REASON</c>, and why, naming the file.</summary>
public static class FileErrors
{
    /// <summary>Why a file system call on <paramref name="path"/> failed.</summary>
    public static DisplayText Describe(Exception error, string? path = null) => (error, path) switch
    {
        (ManagerException manager, _) => manager.Text,
        (FileNotFoundException or DirectoryNotFoundException, { } file) => DisplayText.Of("stored_path_does_not_exist", ("path", file)),
        (UnauthorizedAccessException, { } file) => DisplayText.Of("stored_path_permission_denied", ("path", file)),
        (IOException, { } file) when error.HResult == 17 || error.Message.Contains("exists", StringComparison.OrdinalIgnoreCase) =>
            DisplayText.Of("stored_already_exists", ("path", file)),
        // .NET's own message, which names the file.
        _ => DisplayText.Plain(error.Message),
    };

    /// <summary>Runs <paramref name="action"/>, failing as <c>STEP: REASON</c>.</summary>
    /// <param name="step">What is being done, such as "Remember the folder".</param>
    /// <param name="path">The file it reads or writes, named in the reason.</param>
    public static T Attempt<T>(DisplayText step, Func<T> action, string? path = null)
    {
        try { return action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ManagerException or InvalidOperationException or NotSupportedException)
        {
            throw new ManagerException(DisplayText.Of("stored_step_failed_because", ("step", step), ("reason", Describe(e, path))));
        }
    }

    public static void Attempt(DisplayText step, Action action, string? path = null) =>
        Attempt(step, () => { action(); return 0; }, path);

    public static async Task<T> AttemptAsync<T>(DisplayText step, Func<Task<T>> action, string? path = null)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ManagerException or InvalidOperationException or NotSupportedException)
        {
            throw new ManagerException(DisplayText.Of("stored_step_failed_because", ("step", step), ("reason", Describe(e, path))));
        }
    }
}
