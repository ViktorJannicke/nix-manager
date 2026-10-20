using NixManager.Operations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NixManager.Domain;

/// <summary>A failure the manager reports to the user, optionally explained in plain words.</summary>
/// <param name="text">The failure, shown in the reader's language; <see cref="Exception.Message"/> is its English.</param>
public sealed class ManagerException(DisplayText text, Explanation? explanation = null) : Exception(text.English)
{
    /// <summary>The failure, shown in the reader's language.</summary>
    public DisplayText Text { get; } = text;

    /// <summary>The failure in plain words, when it is a known one.</summary>
    public Explanation? Explanation { get; } = explanation;
}

/// <summary>The actions, spelled as they are stored.</summary>
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

    /// <summary>The catalogue messages that name the actions.</summary>
    private static readonly IReadOnlyDictionary<string, string> LabelKeys = new Dictionary<string, string>
    {
        [Build] = "stored_build_configuration",
        [Switch] = "stored_build_and_switch",
        [Boot] = "stored_apply_on_next_boot",
        [Rollback] = "stored_go_back_to_the_previous_version",
        [Gc] = "stored_remove_old_restore_points",
        [Check] = "stored_check_for_updates",
        [T3codeUpdate] = "stored_update_t3_code",
        [OpencodeUpdate] = "stored_update_opencode",
        [PinceUpdate] = "stored_update_pince",
        [ReviewBuild] = "stored_prepare_changes",
        [ReviewApply] = "stored_apply_changes",
    };

    /// <summary>The action's name for a person; an action this version does not know is named as it is stored.</summary>
    public static DisplayText Label(string action) => LabelKeys.TryGetValue(action, out var key) ? DisplayText.Of(key) : DisplayText.Plain(action);
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
/// are kept as stored, so records written by earlier versions still read.
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
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    /// <summary>A request with the default values.</summary>
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

    /// <summary>Checks a request, failing with a message when it is invalid.</summary>
    public static void Validate(Request r)
    {
        static void Fail(DisplayText message) => throw new ManagerException(message);
        if (!Actions.All.Contains(r.Action)) Fail(DisplayText.Of("stored_unknown_action", ("action", r.Action)));
        if (!Scopes.Values.Contains(r.Scope)) Fail(DisplayText.Of("stored_request_unknown_scope", ("scope", r.Scope)));
        if (r.Action == Actions.Boot && r.Scope != Scopes.System)
            Fail(DisplayText.Of("stored_request_boot_requires_system_scope"));
        if (Actions.IsPackage(r.Action))
        {
            if (r.Scope == Scopes.System) Fail(DisplayText.Of("stored_request_package_updates_apply_to_home"));
            if (r.Update || r.Cleanup || r.Extra.Count > 0)
                Fail(DisplayText.Of("stored_request_package_updates_cannot_combine"));
        }
        else if (r.Force) Fail(DisplayText.Of("stored_request_force_applies_to_package_updates"));
        if (r.Action is Actions.Rollback or Actions.Gc or Actions.Check && r.Update)
            Fail(DisplayText.Of("stored_request_cannot_update_inputs_during", ("action", r.Action)));
        if (r.Action is Actions.Rollback or Actions.Build or Actions.Check && r.Cleanup)
            Fail(DisplayText.Of("stored_request_cleanup_requires_activation"));
        if (r.Action is Actions.Gc or Actions.Check && r.Extra.Count > 0)
            Fail(DisplayText.Of("stored_request_action_takes_no_extra_arguments", ("action", r.Action)));
        if (r.Action == Actions.Rollback && r.Scope != Scopes.Home && r.Extra.Count > 0)
            Fail(DisplayText.Of("stored_request_system_rollback_takes_no_extra_arguments"));
        var retention = r.Retention ?? 3;
        if (retention is < 1 or > 100)
            Fail(DisplayText.Of("stored_request_retention_out_of_range"));
        if (r.Inputs is not null && r.Action != Actions.ReviewBuild)
            Fail(DisplayText.Of("stored_request_inputs_apply_to_reviewed_builds"));
        if (r.Inputs?.Any(name => !InputPattern().IsMatch(name)) == true)
            Fail(DisplayText.Of("stored_request_invalid_flake_input_name"));
        if (r.Activation is not null && r.Action != Actions.ReviewApply)
            Fail(DisplayText.Of("stored_request_activation_applies_to_reviewed_changes"));
        if (r.Edits == true && r.Action != Actions.ReviewBuild)
            Fail(DisplayText.Of("stored_request_edits_are_built_into_reviews"));
        if (r.Edits == true && r.Generation is not null)
            Fail(DisplayText.Of("stored_request_generation_review_cannot_include_edits"));
        if (r.Generation is not null && r.Action != Actions.ReviewBuild)
            Fail(DisplayText.Of("stored_request_generation_only_for_review"));
        if (r.Undo is not null)
        {
            if (r.Action != Actions.ReviewBuild) Fail(DisplayText.Of("stored_request_undo_is_prepared_as_review"));
            if (!ValidId(r.Undo)) Fail(DisplayText.Of("stored_request_valid_operation_id_required"));
            if (r.Edits == true || r.Generation is not null || r.Update || r.Inputs?.Count > 0)
                Fail(DisplayText.Of("stored_request_undo_review_cannot_include"));
        }
        if (r.Then is not null && (r.Action != Actions.ReviewBuild || r.Edits != true || r.Scope != Scopes.Home || r.Update || r.Inputs?.Count > 0))
            Fail(DisplayText.Of("stored_request_only_home_edits_in_one_step"));
        if (Actions.IsReview(r.Action))
        {
            if (r.ReviewId is not null && !ValidId(r.ReviewId)) Fail(DisplayText.Of("stored_request_valid_review_id_required"));
            if (r.Action == Actions.ReviewApply && r.ReviewId is null) Fail(DisplayText.Of("stored_request_valid_review_id_required"));
            if (r.Cleanup || r.Extra.Count > 0)
                Fail(DisplayText.Of("stored_request_reviews_cannot_combine"));
            if (r.Action == Actions.ReviewApply && r.Update) Fail(DisplayText.Of("stored_request_applying_review_cannot_update_inputs"));
            if (r.Generation is { } generation)
            {
                if (generation < 1) Fail(DisplayText.Of("stored_request_invalid_generation_number"));
                if (r.Scope == Scopes.All) Fail(DisplayText.Of("stored_request_select_home_or_system_generation"));
                if (r.Update || r.Inputs?.Count > 0) Fail(DisplayText.Of("stored_request_generation_review_cannot_update_inputs"));
            }
        }
    }
}
