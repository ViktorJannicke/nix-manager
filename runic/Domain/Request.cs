using NixManager.Operations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NixManager.Domain;

/// <summary>A failure the manager reports to the user, optionally explained in plain words.</summary>
public sealed class ManagerException(string message, Explanation? explanation = null) : Exception(message)
{
    /// <summary>The failure in plain words, when it is a known one.</summary>
    public Explanation? Explanation { get; } = explanation;
}

/// <summary>The actions of <c>src/domain.ts</c>, spelled as they are stored.</summary>
public static class Actions
{
    public const string Build = "build";
    public const string Switch = "switch";
    public const string Boot = "boot";
    public const string Rollback = "rollback";
    public const string Gc = "gc";
    public const string T3codeUpdate = "t3code-update";
    public const string OpencodeUpdate = "opencode-update";
    public const string PinceUpdate = "pince-update";
    public const string Check = "check";
    public const string ReviewBuild = "review-build";
    public const string ReviewApply = "review-apply";

    public static readonly IReadOnlyList<string> All =
        [Build, Switch, Boot, Rollback, Gc, T3codeUpdate, OpencodeUpdate, PinceUpdate, Check, ReviewBuild, ReviewApply];

    /// <summary>English labels, as stored and shown by the Bun version (<c>actionLabels</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Build] = "Build configuration",
        [Switch] = "Build and switch",
        [Boot] = "Apply on next boot",
        [Rollback] = "Go back to the previous version",
        [Gc] = "Remove old restore points",
        [Check] = "Check for updates",
        [T3codeUpdate] = "Update T3 Code",
        [OpencodeUpdate] = "Update OpenCode",
        [PinceUpdate] = "Update PINCE",
        [ReviewBuild] = "Prepare changes",
        [ReviewApply] = "Apply changes",
    };

    public static string Label(string action) => Labels.TryGetValue(action, out var label) ? label : action;
    public static bool IsPackage(string action) => action.EndsWith("-update", StringComparison.Ordinal);
    public static bool IsReview(string action) => action is ReviewBuild or ReviewApply;
}

public static class Scopes
{
    public const string All = "all";
    public const string Home = "home";
    public const string System = "system";
    public static readonly IReadOnlyList<string> Values = [All, Home, System];
}

/// <summary>
/// One requested operation, stored with every operation record. The property names and the optional members
/// match <c>Request</c> in <c>src/domain.ts</c>, so records of either version read in the other.
/// </summary>
public sealed record Request
{
    public string Action { get; init; } = Actions.Build;
    public string Scope { get; init; } = Scopes.All;
    public string Host { get; init; } = "";
    public bool Update { get; init; }
    public bool Cleanup { get; init; }
    public bool Force { get; init; }
    public bool DryRun { get; init; }
    public IReadOnlyList<string> Extra { get; init; } = [];
    /// <summary>Generations to keep; <see cref="Create"/> sets 3, as <c>request()</c> does. Older records may lack it.</summary>
    public int? Retention { get; init; }
    public string? ReviewId { get; init; }
    /// <summary>Existing generation to retain for review (rollback).</summary>
    public int? Generation { get; init; }
    /// <summary>Flake inputs to update into a proposed lock file for review.</summary>
    public IReadOnlyList<string>? Inputs { get; init; }
    public string? Activation { get; init; }
    /// <summary>Build the pending source edits into the review.</summary>
    public bool? Edits { get; init; }
    /// <summary>Review going back from the change this operation applied.</summary>
    public string? Undo { get; init; }
    /// <summary>Apply the prepared edits right away.</summary>
    public string? Then { get; init; }

    /// <summary>Members of a stored request this version does not know, kept as they were.</summary>
    [JsonExtensionData, Runic.Application.Views.RunicIgnore]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    /// <summary>A request with the Bun version's defaults (<c>request()</c> in <c>src/domain.ts</c>).</summary>
    public static Request Create() => new() { Retention = 3 };

    [JsonIgnore] public bool IncludesHome => Scope != Scopes.System;
    [JsonIgnore] public bool IncludesSystem => Scope != Scopes.Home;
}

public static partial class RequestValidation
{
    [GeneratedRegex("^[a-f0-9-]{8,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex InputPattern();

    public static bool ValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>The checks of <c>validate</c> in <c>src/domain.ts</c>, with the same messages.</summary>
    public static void Validate(Request r)
    {
        static void Fail(string message) => throw new ManagerException(message);
        if (!Actions.All.Contains(r.Action)) Fail($"Unknown action: {r.Action}.");
        if (!Scopes.Values.Contains(r.Scope)) Fail($"Unknown scope: {r.Scope}.");
        if (r.Action == Actions.Boot && r.Scope != Scopes.System)
            Fail("boot requires system scope.");
        if (Actions.IsPackage(r.Action))
        {
            if (r.Scope == Scopes.System) Fail("Package updates apply to home packages.");
            if (r.Update || r.Cleanup || r.Extra.Count > 0)
                Fail("Package updates cannot combine with update, cleanup or extra arguments.");
        }
        else if (r.Force) Fail("--force applies to package updates.");
        if (r.Action is Actions.Rollback or Actions.Gc or Actions.Check && r.Update)
            Fail($"Cannot update inputs during {r.Action}.");
        if (r.Action is Actions.Rollback or Actions.Build or Actions.Check && r.Cleanup)
            Fail("Cleanup requires successful activation or the gc action.");
        if (r.Action is Actions.Gc or Actions.Check && r.Extra.Count > 0)
            Fail($"{r.Action} takes no extra arguments.");
        if (r.Action == Actions.Rollback && r.Scope != Scopes.Home && r.Extra.Count > 0)
            Fail("System rollback takes no extra arguments.");
        var retention = r.Retention ?? 3;
        if (retention is < 1 or > 100)
            Fail("Retention must be between 1 and 100 generations.");
        if (r.Inputs is not null && r.Action != Actions.ReviewBuild)
            Fail("Input selection applies to reviewed builds.");
        if (r.Inputs?.Any(name => !InputPattern().IsMatch(name)) == true)
            Fail("Invalid flake input name.");
        if (r.Activation is not null && r.Action != Actions.ReviewApply)
            Fail("The activation mode applies to reviewed changes.");
        if (r.Edits == true && r.Action != Actions.ReviewBuild)
            Fail("Pending edits are built into reviews.");
        if (r.Edits == true && r.Generation is not null)
            Fail("A generation review cannot include edits.");
        if (r.Generation is not null && r.Action != Actions.ReviewBuild)
            Fail("A generation can only be selected for review.");
        if (r.Undo is not null)
        {
            if (r.Action != Actions.ReviewBuild) Fail("Undoing a change is prepared as a review.");
            if (!ValidId(r.Undo)) Fail("A valid operation ID is required.");
            if (r.Edits == true || r.Generation is not null || r.Update || r.Inputs?.Count > 0)
                Fail("An undo review cannot include edits, generations or input updates.");
        }
        if (r.Then is not null && (r.Action != Actions.ReviewBuild || r.Edits != true || r.Scope != Scopes.Home || r.Update || r.Inputs?.Count > 0))
            Fail("Only edits of the home configuration are applied in one step.");
        if (Actions.IsReview(r.Action))
        {
            if (r.ReviewId is not null && !ValidId(r.ReviewId)) Fail("A valid review ID is required.");
            if (r.Action == Actions.ReviewApply && r.ReviewId is null) Fail("A valid review ID is required.");
            if (r.Cleanup || r.Extra.Count > 0)
                Fail("Reviewed workflows cannot combine with cleanup or extra arguments.");
            if (r.Action == Actions.ReviewApply && r.Update) Fail("Applying a review cannot update inputs.");
            if (r.Generation is { } generation)
            {
                if (generation < 1) Fail("Invalid generation number.");
                if (r.Scope == Scopes.All) Fail("Select either the home or the system generation.");
                if (r.Update || r.Inputs?.Count > 0) Fail("A generation review cannot update inputs.");
            }
        }
    }
}
