using System.Text.RegularExpressions;

namespace NixManager.Domain;

/// <summary>One step of a plan: a process to start, or an internal step.</summary>
public sealed record Command
{
    public required DisplayText Label { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public string? Cwd { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public bool? AllowFailure { get; init; }
    public bool? Quiet { get; init; }
    /// <summary>Keep all of stdout instead of its last 8 MiB.</summary>
    public bool? FullOutput { get; init; }
    /// <summary>Parse Nix's structured log for progress; argv must request it.</summary>
    public bool? Progress { get; init; }
    public string? Internal { get; init; }
}

public sealed record Plan(IReadOnlyList<Command> Steps, IReadOnlyList<DisplayText> Notes);

/// <summary>Steps the manager performs itself instead of starting a process (<c>InternalStep</c>).</summary>
public static class InternalSteps
{
    public const string Compare = "compare", WriteLock = "write-lock", StageSources = "stage-sources", VerifyEdits = "verify-edits",
        WriteSources = "write-sources", RestoreSources = "restore-sources", RevalidatePins = "revalidate-pins", WriteUpdate = "write-update";
}

/// <summary>Builds the steps of an operation.</summary>
public static partial class Planner
{
    private static readonly string[] NixFlags = ["--log-format", "internal-json", "--print-out-paths"];

    /// <param name="review">For <c>review-apply</c>: the stored review the request names.</param>
    /// <param name="undo">For an undo review: the undo point of the change it reverses.</param>
    public static Plan Plan(Config config, Request r, Reviews.BuildReview? review = null, Operations.UndoPoint? undo = null)
    {
        RequestValidation.Validate(r);
        var host = HostResolution.Resolve(config.Hosts, r.Host is { Length: > 0 } ? r.Host : config.HostKey).Value;
        var steps = new List<Command>();
        var notes = new List<DisplayText>();
        void Add(DisplayText label, IReadOnlyList<string> argv, bool progress = false, IReadOnlyDictionary<string, string>? env = null, string? @internal = null) =>
            steps.Add(new Command { Label = label, Argv = argv, Cwd = config.Flake, Env = env, Progress = progress ? true : null, Internal = @internal });
        void PrivilegedStep(DisplayText label, params string[] args) => Add(label, [.. config.Privileged, .. args]);
        bool activatesHome = false, activatesSystem = false;
        var home = r.IncludesHome;
        var system = r.IncludesSystem;
        // Edited reviews build a copy of the flake's files with the edits applied.
        var flake = r.Action == Actions.ReviewBuild && r.Edits == true
            ? $"path:{Paths.ReviewRoot(config, r.ReviewId ?? "<new>")}/source"
            : config.Flake;
        var homeAttr = $"{flake}#homeConfigurations.{Nix.NixText.Attr(host.HomeConfiguration)}.activationPackage";
        var systemAttr = $"{flake}#nixosConfigurations.{Nix.NixText.Attr(host.NixosConfiguration)}.config.system.build.toplevel";
        void Build(DisplayText label, string installable, string? link, IReadOnlyList<string>? extra = null) =>
            Add(label, ["nix", "build", .. link is null ? ["--no-link"] : new[] { "--out-link", link }, .. NixFlags, .. extra ?? [], installable, .. r.Extra],
                progress: true);
        void ActivateHome(string output)
        {
            // A user's first home generation: nix-env needs the profile's folder.
            if (!config.HomeProfileExists)
                Add(DisplayText.Of("stored_create_the_folder_for_home_generations"), ["mkdir", "-p", Parent(config.HomeProfile)]);
            Add(DisplayText.Of("stored_select_home_manager_generation"), ["nix-env", "--profile", config.HomeProfile, "--set", output]);
            activatesHome = true;
            Add(DisplayText.Of("stored_activate_home_manager"), [$"{output}/activate", "--driver-version", "1"],
                env: new Dictionary<string, string> { ["HOME_MANAGER_BACKUP_EXT"] = "hm-backup" });
        }
        void ActivateSystem(string output, string mode)
        {
            activatesSystem = true;
            PrivilegedStep(DisplayText.Of(mode == "boot" ? "stored_activate_system_on_next_boot" : "stored_activate_system"), "activate-output", mode, output);
        }
        void Cleanup()
        {
            var retention = r.Retention ?? 3;
            if (home && config.HomeProfileExists)
                Add(DisplayText.Of("stored_keep_home_generations", ("count", retention)), ["nix-env", "--profile", config.HomeProfile, "--delete-generations", $"+{retention}"]);
            else if (home)
                notes.Add(DisplayText.Of("stored_no_home_manager_profile_at_generation_deletion", ("path", config.HomeProfile)));
            if (system)
                PrivilegedStep(DisplayText.Of("stored_keep_system_generations_and_collect_garbage", ("count", retention)), "gc", retention.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else Add(DisplayText.Of("stored_collect_unreferenced_store_paths"), ["nix-store", "--gc"]);
            notes.Add(DisplayText.Of("stored_garbage_collection_deletes_unreferenced_store_paths_and"));
        }

        if (r.Action == Actions.ReviewBuild)
        {
            var root = Paths.ReviewRoot(config, r.ReviewId ?? "<new>");
            if (r.Generation is { } generation)
            {
                var profile = home ? config.HomeProfile : Paths.SystemProfile;
                Build(DisplayText.Of(home ? "stored_retain_home_generation" : "stored_retain_system_generation", ("generation", generation)),
                    $"{profile}-{generation}-link", $"{root}/{r.Scope}");
            }
            else if (r.Undo is not null)
            {
                if (undo is null) throw new ManagerException(DisplayText.Of("stored_the_change_to_undo_is_unavailable"));
                foreach (var scope in new[] { Scopes.Home, Scopes.System })
                {
                    if (scope == Scopes.Home ? !home : !system) continue;
                    var number = scope == Scopes.Home ? undo.Generations.Home : undo.Generations.System;
                    if (number is null)
                        throw new ManagerException(DisplayText.Of(scope == Scopes.Home
                            ? "stored_the_change_did_not_record_the_home"
                            : "stored_the_change_did_not_record_the_system"));
                    Build(DisplayText.Of(scope == Scopes.Home ? "stored_retain_home_generation" : "stored_retain_system_generation", ("generation", number.Value)),
                        $"{(scope == Scopes.Home ? config.HomeProfile : Paths.SystemProfile)}-{number}-link", $"{root}/{scope}");
                }
                if (undo.Files.Count > 0)
                    notes.Add(DisplayText.Of("stored_applying_the_undo_also_restores_the_configuration"));
            }
            else
            {
                if (r.Edits == true)
                {
                    Add(DisplayText.Of("stored_stage_the_edited_files"), [], @internal: InternalSteps.StageSources);
                    Add(DisplayText.Of("stored_check_the_edits"), [], @internal: InternalSteps.VerifyEdits);
                }
                var inputs = r.Inputs ?? [];
                var lockFile = r.Update || inputs.Count > 0 ? $"{root}/flake.lock" : null;
                if (lockFile is not null)
                    Add(inputs.Count > 0
                            ? inputs.Count == 1 ? DisplayText.Of("stored_resolve", ("input", inputs[0])) : DisplayText.Of("stored_resolve_inputs", ("count", inputs.Count))
                            : DisplayText.Of("stored_resolve_all_flake_inputs"),
                        ["nix", "flake", "update", .. inputs, "--flake", config.Flake, "--output-lock-file", lockFile, "--log-format", "internal-json"],
                        progress: true);
                // Pins inherit nixpkgs' recipes; an update of the root nixpkgs input can invalidate their hashes.
                if (lockFile is not null && (r.Update || inputs.Contains("nixpkgs")))
                {
                    Add(DisplayText.Of("stored_check_package_pins_against_the_new_nixpkgs"), [], @internal: InternalSteps.RevalidatePins);
                    notes.Add(DisplayText.Of("stored_pins_are_revalidated_against_the_new_nixpkgs"));
                }
                string[] lockFlags = lockFile is not null ? ["--reference-lock-file", lockFile, "--no-write-lock-file"] : ["--no-write-lock-file"];
                if (home) Build(DisplayText.Of("stored_build_home_configuration"), homeAttr, $"{root}/home", lockFlags);
                if (system) Build(DisplayText.Of("stored_build_system_configuration"), systemAttr, $"{root}/system", lockFlags);
                if (lockFile is not null)
                    notes.Add(DisplayText.Of("stored_inputs_are_resolved_into_a_separate_lock"));
                if (r.Edits == true)
                    notes.Add(DisplayText.Of("stored_edits_are_built_from_a_copy_of"));
            }
            Add(DisplayText.Of("stored_compare_with_the_active_configuration"), [], @internal: InternalSteps.Compare);
            notes.Add(DisplayText.Of("stored_nothing_is_activated_the_build_is_kept"));
            return new Plan(steps, notes);
        }
        if (r.Action == Actions.ReviewApply)
        {
            if (review is null || review.Id != r.ReviewId) throw new ManagerException(DisplayText.Of("stored_the_selected_review_is_unavailable"));
            var mode = r.Activation ?? "switch";
            if (review.Edits is { } edits)
                Add(edits.Files.Count == 1 ? DisplayText.Of("stored_write", ("file", edits.Files[0].Path)) : DisplayText.Of("stored_write_edited_files", ("count", edits.Files.Count)), [],
                    @internal: InternalSteps.WriteSources);
            if (review.Inputs is not null)
                Add(DisplayText.Of(review.Inputs.Pins is { Count: > 0 } ? "stored_write_the_reviewed_flake_lock_and_package_pins" : "stored_write_the_reviewed_flake_lock"), [],
                    @internal: InternalSteps.WriteLock);
            if (review.Undo?.Point.Files is { Count: > 0 } files)
                Add(files.Count == 1 ? DisplayText.Of("stored_restore", ("file", files[0].Path)) : DisplayText.Of("stored_restore_files", ("count", files.Count)), [], @internal: InternalSteps.RestoreSources);
            if (home && review.Home is not null && mode == "switch") ActivateHome(review.Home.Path);
            if (system && review.System is not null) ActivateSystem(review.System.Path, mode);
            if (!steps.Any(step => step.Internal is null))
                throw new ManagerException(DisplayText.Of(mode == "boot"
                    ? "stored_only_a_system_configuration_can_be_applied"
                    : "stored_this_review_does_not_contain_the_selected"));
            if (activatesHome && activatesSystem)
                notes.Add(DisplayText.Of("stored_home_activates_before_system_if_system_activation"));
            notes.Add(DisplayText.Of("stored_the_exact_reviewed_outputs_are_activated_the"));
            return new Plan(steps, notes);
        }
        if (r.Update)
        {
            // The new lock is resolved beside the checkout and written, with the pins revalidated against it, before the builds.
            Add(DisplayText.Of("stored_resolve_all_flake_inputs"),
                ["nix", "flake", "update", "--flake", config.Flake, "--output-lock-file", $"{Paths.BuildRoot(config)}/flake.lock", "--log-format", "internal-json"],
                progress: true);
            Add(DisplayText.Of("stored_check_package_pins_against_the_new_nixpkgs"), [], @internal: InternalSteps.RevalidatePins);
            Add(DisplayText.Of("stored_update_flake_inputs"), [], @internal: InternalSteps.WriteUpdate);
            notes.Add(DisplayText.Of("stored_pins_are_revalidated_against_the_new_nixpkgs"));
        }
        if (r.Action == Actions.Check)
            notes.Add(DisplayText.Of("stored_resolve_inputs_in_a_temporary_copy_and"));
        else if (Actions.IsPackage(r.Action))
            notes.Add(DisplayText.Of("stored_compare_the_latest_release_on_the_pin"));
        else
        {
            if (r.Action == Actions.Build)
            {
                if (home) Build(DisplayText.Of("stored_build_home_configuration"), homeAttr, null);
                if (system) Build(DisplayText.Of("stored_build_system_configuration"), systemAttr, null);
            }
            if (r.Action is Actions.Switch or Actions.Boot)
            {
                var root = Paths.BuildRoot(config);
                if (home) Build(DisplayText.Of("stored_build_home_configuration"), homeAttr, $"{root}/home");
                if (system) Build(DisplayText.Of("stored_build_system_configuration"), systemAttr, $"{root}/system");
                if (home) ActivateHome($"{root}/home");
                if (system) ActivateSystem($"{root}/system", r.Action);
                if (home && system)
                    notes.Add(DisplayText.Of("stored_home_activates_before_system_if_system_activation_2"));
                if (system)
                    notes.Add(DisplayText.Of("stored_once_the_privileged_step_starts_cancellation_waits"));
            }
            else if (r.Action == Actions.Rollback)
            {
                if (home) Add(DisplayText.Of("stored_roll_back_home_configuration"), ["home-manager", "switch", "--rollback", .. r.Extra]);
                if (system) PrivilegedStep(DisplayText.Of("stored_roll_back_system_configuration"), "rollback");
            }
            if (r.Cleanup || r.Action == Actions.Gc) Cleanup();
        }
        return new Plan(steps, notes);
    }

    /// <summary>
    /// Commands that make earlier generations active again after a failed apply: the system first, then home, the
    /// reverse of the order they were activated in (<c>goBackSteps</c>).
    /// </summary>
    public static List<Command> GoBackSteps(Config config, int? home, int? system)
    {
        var steps = new List<Command>();
        if (system is { } systemGeneration)
            steps.Add(new Command
            {
                Label = DisplayText.Of("stored_go_back_to_system_generation", ("generation", systemGeneration)),
                Argv = [.. config.Privileged, "activate-generation", Nix.NixText.Invariant(systemGeneration)],
                Cwd = config.Flake,
            });
        if (home is { } homeGeneration)
        {
            steps.Add(new Command
            {
                Label = DisplayText.Of("stored_go_back_to_home_generation", ("generation", homeGeneration)),
                Argv = ["nix-env", "--profile", config.HomeProfile, "--switch-generation", Nix.NixText.Invariant(homeGeneration)],
                Cwd = config.Flake,
            });
            steps.Add(new Command
            {
                Label = DisplayText.Of("stored_activate_the_earlier_home_generation"),
                Argv = [$"{config.HomeProfile}-{homeGeneration}-link/activate", "--driver-version", "1"],
                Cwd = config.Flake,
                Env = new Dictionary<string, string> { ["HOME_MANAGER_BACKUP_EXT"] = "hm-backup" },
            });
        }
        return steps;
    }

    /// <summary>
    /// A planned build of <paramref name="flake"/>'s outputs that builds them from <paramref name="other"/> instead,
    /// such as a copy holding pins revalidated while the operation ran.
    /// </summary>
    public static Command OnFlake(Command build, string flake, string other) =>
        build with { Argv = build.Argv.Select(argument => argument.StartsWith($"{flake}#", StringComparison.Ordinal) ? $"{other}{argument[flake.Length..]}" : argument).ToList() };

    private static string Parent(string path) => Regex.Replace(path, "/[^/]+$", "");

    /// <summary>A command as a shell would read it (<c>formatCommand</c>).</summary>
    public static string Format(Command command) =>
        command.Internal is not null
            ? "(performed by Nix Manager)"
            : string.Join(' ', command.Argv.Select(argument =>
                SafeArgument().IsMatch(argument) ? argument : $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'"));

    [GeneratedRegex("^[A-Za-z0-9_@%+=:,./#-]+$")]
    private static partial Regex SafeArgument();
}
