using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Observation;

namespace NixManager.Reviews;

/// <summary>A commit of the configuration, as the dashboard's change history shows it (<c>HistoryEntry</c>).</summary>
public sealed record HistoryEntry
{
    public required string Revision { get; init; }
    public required string Subject { get; init; }
    /// <summary>The rest of the message, such as every part of a change.</summary>
    public required string Body { get; init; }
    public required string Date { get; init; }
    public required string Author { get; init; }
    /// <summary>Made by Nix Manager after applying a change.</summary>
    public bool Applied { get; init; }
}

/// <summary>The configuration's Git history of applied changes.</summary>
public static partial class ChangeHistory
{
    /// <summary>Marks the commits Nix Manager makes, so the history can tell them apart.</summary>
    private const string Trailer = "Applied-with";

    /// <summary>
    /// A commit message from what a change did: the first part as subject, every part in the body (<c>changeMessage</c>).
    /// Commit messages are English.
    /// </summary>
    public static string ChangeMessage(IReadOnlyList<DisplayText> changes)
    {
        var parts = changes.Select(change => change.English).ToList();
        var subject = parts.Count > 1 ? $"{parts[0]} and {parts.Count - 1} more" : parts.Count == 1 ? parts[0] : DisplayText.Of("stored_apply_the_configuration").English;
        return parts.Count > 1 ? $"{subject}\n\n{string.Join('\n', parts.Select(change => $"- {change}"))}" : subject;
    }

    private static string LastLine(string text) => text.Trim().Split('\n')[^1];

    /// <summary>
    /// Commits exactly the files an apply wrote, with a message saying what it did, and pushes when
    /// <paramref name="backup"/> is on and the branch has an upstream (<c>recordChange</c>). Git failures do not
    /// fail it: the change is applied already, so it returns what happened, or null when the files did not change.
    /// </summary>
    /// <param name="run">Runs the Git commands; the operation service passes its quiet runner.</param>
    public static async Task<DisplayText?> RecordChangeAsync(Config config, IReadOnlyList<string> paths, IReadOnlyList<DisplayText> changes, bool backup,
        Func<Command, Task<CommandResult>> run)
    {
        Task<CommandResult> Git(DisplayText label, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null) =>
            run(new Command { Label = label, Argv = ["git", "-C", config.Flake, .. args], AllowFailure = true, Env = env });
        static DisplayText NotKept(string output) => DisplayText.Of("stored_history_not_kept", ("reason", LastLine(output)));
        if ((await Git(DisplayText.Of("stored_find_the_git_repository"), ["rev-parse", "--is-inside-work-tree"]).ConfigureAwait(false)).Code != 0)
            return DisplayText.Of("stored_history_not_a_git_repository");
        var added = await Git(DisplayText.Of("stored_stage_the_changed_files"), ["add", "--all", "--", .. paths]).ConfigureAwait(false);
        if (added.Code != 0) return NotKept(added.Stderr);
        var head = (await Git(DisplayText.Of("stored_find_the_last_commit"), ["rev-parse", "--verify", "--quiet", "HEAD"]).ConfigureAwait(false)).Code == 0;
        if (head && (await Git(DisplayText.Of("stored_look_for_changes"), ["diff", "--cached", "--quiet", "HEAD", "--", .. paths]).ConfigureAwait(false)).Code == 0)
            return null;
        // Without a Git identity, commits are made in Nix Manager's name.
        string[] identity = (await Git(DisplayText.Of("stored_read_the_git_identity"), ["config", "user.email"]).ConfigureAwait(false)).Stdout.Trim().Length > 0
            ? []
            : ["-c", "user.name=Nix Manager", "-c", "user.email=nix-manager@localhost"];
        var message = ChangeMessage(changes);
        var committed = await Git(DisplayText.Of("stored_keep_the_change_in_the_history"),
            [.. identity, "commit", "--quiet", "--only", "--message", message, "--trailer", $"{Trailer}: Nix Manager", "--", .. paths]).ConfigureAwait(false);
        if (committed.Code != 0) return NotKept(committed.Stderr.Length > 0 ? committed.Stderr : committed.Stdout);
        var recorded = DisplayText.Of("stored_history_kept", ("subject", message.Split('\n')[0]));
        if (!backup) return recorded;
        var upstream = await Git(DisplayText.Of("stored_find_the_backup"), ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"]).ConfigureAwait(false);
        if (upstream.Code != 0) return DisplayText.Of("stored_history_kept_no_remote", ("recorded", recorded));
        var pushed = await Git(DisplayText.Of("stored_back_up_the_configuration"), ["push", "--quiet"], new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" }).ConfigureAwait(false);
        return pushed.Code == 0
            ? DisplayText.Of("stored_history_kept_backed_up", ("recorded", recorded), ("upstream", upstream.Stdout.Trim()))
            : DisplayText.Of("stored_history_kept_backup_failed", ("recorded", recorded), ("reason", LastLine(pushed.Stderr)));
    }

    [GeneratedRegex("^Applied-with:.*$", RegexOptions.Multiline)]
    private static partial Regex TrailerLine();

    /// <summary>The newest commits of the configuration, or none outside a Git repository (<c>readHistory</c>).</summary>
    public static async Task<IReadOnlyList<HistoryEntry>> ReadAsync(string flake, int limit = 50, CancellationToken cancellationToken = default)
    {
        var format = string.Join("%x00", "%H", "%s", "%cI", "%an", $"%(trailers:key={Trailer},valueonly,separator=%x20)", "%b");
        var text = await Repository.GitAsync(flake, ["log", $"--max-count={limit}", $"--format={format}%x1e"], cancellationToken).ConfigureAwait(false);
        return (text ?? "")
            .Split('\x1e')
            .Select(record => record.StartsWith('\n') ? record[1..] : record)
            .Where(record => record.Length > 0)
            .Select(record =>
            {
                var parts = record.Split('\0');
                string? Part(int index) => parts.Length > index ? parts[index] : null;
                return new HistoryEntry
                {
                    Revision = Part(0) ?? "",
                    Subject = Part(1) ?? "",
                    Body = TrailerLine().Replace(Part(5) ?? "", "", 1).Trim(),
                    Date = Part(2) ?? "",
                    Author = Part(3) ?? "",
                    Applied = (Part(4)?.Trim().Length ?? 0) > 0,
                };
            })
            .ToList();
    }
}
