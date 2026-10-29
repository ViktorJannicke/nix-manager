using System.Text.Json.Serialization;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Operations;
using NixManager.Software;
using Runic.CommandLine;

namespace NixManager.Cli;

/// <summary>
/// The result of the root command, payload type <c>nix-manager.run/1</c>. <see cref="Kind"/> names the answer and
/// only its members are present: a plan for <c>--dry-run</c>, an operation (with the update report for
/// <c>check</c>), the history or the status. A failed or cancelled operation is the failure data of the same type.
/// </summary>
public sealed record RunResult
{
    public const string PayloadType = "nix-manager.run/1";

    public const string PlanKind = "plan", OperationKind = "operation", HistoryKind = "history", StatusKind = "status",
        DashboardKind = "ui", TrayKind = "tray";

    /// <summary><c>plan</c>, <c>operation</c>, <c>history</c>, <c>status</c>, <c>ui</c> or <c>tray</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The host configuration the command used; absent for <c>ui</c> and <c>tray</c>.</summary>
    public string? Host { get; init; }

    /// <summary>The steps a preview (<c>--dry-run</c>) would run.</summary>
    public Plan? Plan { get; init; }

    /// <summary>The operation the command ran, as the history records it.</summary>
    public Operation? Operation { get; init; }

    /// <summary>The update report a <c>check</c> operation left; absent when there is none.</summary>
    public UpdateReport? Updates { get; init; }

    /// <summary>The recorded operations, newest first.</summary>
    public IReadOnlyList<Operation>? History { get; init; }

    public StatusDocument? Status { get; init; }
}

/// <summary>The result of <c>software search</c>, payload type <c>nix-manager.software-search/1</c>.</summary>
public sealed record SoftwareSearchResult(string Query, IReadOnlyList<CatalogEntry> Packages)
{
    public const string PayloadType = "nix-manager.software-search/1";
}

/// <summary>
/// The pending edits after a software command that reads or changes them, payload type
/// <c>nix-manager.software-draft/1</c>.
/// </summary>
public sealed record SoftwareDraftResult
{
    public const string PayloadType = "nix-manager.software-draft/1";

    /// <summary>The pending edits, the files they change and the edits that cannot be applied.</summary>
    public required RenderedDraft Draft { get; init; }

    /// <summary>The numbers of the edits this command added, as <c>software drop</c> takes them.</summary>
    public IReadOnlyList<int> Added { get; init; } = [];

    /// <summary>The edits this command removed.</summary>
    public IReadOnlyList<Change> Dropped { get; init; } = [];

    /// <summary>Human output shows the edited lines (<c>software pending --diff</c>); the JSON payload always has them.</summary>
    [JsonIgnore]
    public bool ShowDiff { get; init; }
}

/// <summary>
/// The failure data of a command that failed before an operation ran, type <c>nix-manager.error/1</c>: the error's
/// exact message, which the fault only summarises, and its explanation in plain words.
/// </summary>
public sealed record CliError(string Message, Explanation? Explanation)
{
    public const string DataType = "nix-manager.error/1";
}

/// <summary>
/// The command line's faults. A fault's message is a fixed summary of what failed: an error's own message may name
/// paths or other technical text, which a consumer-safe fault must not contain, so it is the failure data and the
/// human output instead. A fault code's number is the exit code its category has by default.
/// </summary>
internal static class CliFaults
{
    public const string InvalidCommandLine = "The command line is not valid.",
        ConfigurationUnavailable = "The configuration could not be loaded.",
        HistoryUnavailable = "The operation history could not be read.",
        OperationNotStarted = "The operation could not run.",
        OperationFailed = "The operation failed.",
        OperationCancelled = "The operation was cancelled.",
        SearchFailed = "The packages could not be searched.",
        DraftNotChanged = "The pending edits were not changed.",
        DraftUnavailable = "The pending edits could not be read.";

    public static string Code(CommandExitCategory category) => category switch
    {
        CommandExitCategory.Usage => "NM0002",
        CommandExitCategory.Validation => "NM0003",
        CommandExitCategory.Cancelled => "NM0004",
        CommandExitCategory.Unavailable => "NM0005",
        _ => "NM0010",
    };

    /// <summary>Human output of a failure, written before the fault's summary: the exact message and the explanation in plain words.</summary>
    private static string HumanText(string message, Explanation? explanation) =>
        message + "\n" + (explanation is { } e ? TerminalRenderer.ExplanationText(e) : "");

    /// <summary>
    /// A failure summarised as <paramref name="summary"/>; the failure data (<see cref="CliError"/>) and the human
    /// output carry the error's message and explanation.
    /// </summary>
    public static CommandOutcome<T> Failure<T>(CommandExitCategory category, string summary, ManagerException error) =>
        CommandOutcome.Failure<T>(category, new CommandFault(Code(category), summary), failureData:
            CommandFailureData.Create(CliError.DataType, new CliError(error.Message, error.Explanation), ManagerJsonContext.Default.CliError),
            humanOutput: HumanText(error.Message, error.Explanation));

    /// <summary>
    /// The outcome of an operation that ran: success, or a failure whose data is the operation's result
    /// (<see cref="RunResult.PayloadType"/>), which holds its exact error and explanation. The operation wrote its error
    /// to its output as it ended, so the human output is only the explanation.
    /// </summary>
    public static CommandOutcome<RunResult> Operation(RunResult result)
    {
        var operation = result.Operation!;
        if (operation.Status == OperationStatuses.Succeeded) return CommandOutcome.Success(result);
        var cancelled = operation.Status == OperationStatuses.Cancelled;
        var category = cancelled ? CommandExitCategory.Cancelled : CommandExitCategory.CommandFailure;
        var fault = new CommandFault(Code(category), cancelled ? OperationCancelled : OperationFailed);
        var human = operation.Status == OperationStatuses.Failed && operation.Explanation is { } explanation
            ? TerminalRenderer.ExplanationText(explanation)
            : null;
        CommandFailureData data;
        try
        {
            data = CommandFailureData.Create(RunResult.PayloadType, result, ManagerJsonContext.Default.RunResult);
        }
        catch (CommandProtocolException)
        {
            // Failure data is bounded (64 KiB); an operation too large for it is still in the history.
            return CommandOutcome.Failure<RunResult>(category, fault, null, human);
        }
        return CommandOutcome.Failure<RunResult>(category, fault, humanOutput: human, failureData: data);
    }
}

/// <summary>Human output of the command line's results.</summary>
internal static class CliPresenters
{
    public static CommandCatalogBuilder Register(CommandCatalogBuilder builder)
    {
        foreach (var path in new[] { "run", "software prepare", "software apply" }) builder.Present<RunResult>(path, Run);
        builder.Present<SoftwareSearchResult>("software search", Search);
        foreach (var path in new[] { "software add", "software remove", "software set", "software unset" })
            builder.Present<SoftwareDraftResult>(path, Edited);
        builder.Present<SoftwareDraftResult>("software pending", (result, console, _, token) => Write(console, SoftwareCli.PendingText(result.Draft, result.ShowDiff), token));
        builder.Present<SoftwareDraftResult>("software drop", (result, console, _, token) =>
        {
            var left = result.Draft.Changes.Count;
            return Write(console, $"Dropped: {ChangeRules.Describe(result.Dropped[0]).Title.English}. {left} pending {Plural(left, "edit", "edits")} left.\n", token);
        });
        builder.Present<SoftwareDraftResult>("software clear", (result, console, _, token) =>
        {
            var dropped = result.Dropped.Count;
            return Write(console, dropped > 0 ? $"Dropped {dropped} pending {Plural(dropped, "edit", "edits")}.\n" : "No pending edits.\n", token);
        });
        return builder;
    }

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private static ValueTask Write(ICommandConsole console, string text, CancellationToken cancellationToken) =>
        text.Length == 0 ? ValueTask.CompletedTask : console.WriteOutAsync(text.AsMemory(), cancellationToken);

    /// <summary>An operation presented itself while it ran (<see cref="TerminalRenderer"/>); the other answers are text.</summary>
    private static ValueTask Run(RunResult result, ICommandConsole console, System.Globalization.CultureInfo culture, CancellationToken cancellationToken) =>
        Write(console, result.Kind switch
        {
            RunResult.PlanKind => CliText.Plan(result.Plan!),
            RunResult.HistoryKind => CliText.History(result.History!, result.Host ?? "", console.IsOutputRedirected ? 0 : WindowWidth()),
            RunResult.StatusKind => CliText.Status(result.Status!),
            _ => "",
        }, cancellationToken);

    private static int WindowWidth()
    {
        try { return Console.WindowWidth; }
        catch (IOException) { return 0; }
    }

    private static ValueTask Search(SoftwareSearchResult result, ICommandConsole console, System.Globalization.CultureInfo culture, CancellationToken cancellationToken)
    {
        if (result.Packages.Count == 0) return Write(console, $"No packages match \"{result.Query}\".\n", cancellationToken);
        var text = new System.Text.StringBuilder();
        foreach (var entry in result.Packages)
            text.Append($"{entry.A}{(entry.V is { Length: > 0 } v ? $" {v}" : "")}{(entry.B ? " (broken)" : "")}{(entry.F ? "" : " (unfree)")}\n    {entry.D ?? "No description."}\n");
        return Write(console, text.ToString(), cancellationToken);
    }

    /// <summary>The edits a command added, and how many are pending.</summary>
    private static ValueTask Edited(SoftwareDraftResult result, ICommandConsole console, System.Globalization.CultureInfo culture, CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder();
        foreach (var number in result.Added)
        {
            var described = ChangeRules.Describe(result.Draft.Changes[number - 1]);
            text.Append($"+ {described.Title.English} ({described.Detail.English})\n");
        }
        var count = result.Draft.Changes.Count;
        text.Append($"{count} pending {Plural(count, "edit", "edits")}. Run nix-manager software prepare to build {Plural(count, "it", "them")}.\n");
        return Write(console, text.ToString(), cancellationToken);
    }
}
