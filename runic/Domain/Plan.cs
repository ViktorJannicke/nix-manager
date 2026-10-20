using System.Text.RegularExpressions;

namespace NixManager.Domain;

/// <summary>One step of a plan: a process to start, or an internal step.</summary>
public sealed record Command
{
    public required string Label { get; init; }
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

public sealed record Plan(IReadOnlyList<Command> Steps, IReadOnlyList<string> Notes);

/// <summary>Steps the manager performs itself instead of starting a process (<c>InternalStep</c>).</summary>
public static class InternalSteps
{
    public const string Compare = "compare", WriteLock = "write-lock", StageSources = "stage-sources", VerifyEdits = "verify-edits",
        WriteSources = "write-sources", RestoreSources = "restore-sources";
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
        var notes = new List<string>();
        void Add(string label, IReadOnlyList<string> argv, bool progress = false, IReadOnlyDictionary<string, string>? env = null, string? @internal = null) =>
            steps.Add(new Command { Label = label, Argv = argv, Cwd = config.Flake, Env = env, Progress = progress ? true : null, Internal = @internal });
        void PrivilegedStep(string label, params string[] args) => Add(label, [.. config.Privileged, .. args]);
        var home = r.IncludesHome;
        var system = r.IncludesSystem;
        // Edited reviews build a copy of the flake's files with the edits applied.
        var flake = r.Action == Actions.ReviewBuild && r.Edits == true
            ? $"path:{Paths.ReviewRoot(config, r.ReviewId ?? "<new>")}/source"
            : config.Flake;
        var homeAttr = $"{flake}#homeConfigurations.{Nix.NixText.Attr(host.HomeConfiguration)}.activationPackage";
        var systemAttr = $"{flake}#nixosConfigurations.{Nix.NixText.Attr(host.NixosConfiguration)}.config.system.build.toplevel";
        void Build(string label, string installable, string? link, IReadOnlyList<string>? extra = null) =>
            Add(label, ["nix", "build", .. link is null ? ["--no-link"] : new[] { "--out-link", link }, .. NixFlags, .. extra ?? [], installable, .. r.Extra],
                progress: true);
        void ActivateHome(string output)
        {
            // A user's first home generation: nix-env needs the profile's folder.
            if (!config.HomeProfileExists)
                Add("Create the folder for home generations", ["mkdir", "-p", Parent(config.HomeProfile)]);
            Add("Select Home Manager generation", ["nix-env", "--profile", config.HomeProfile, "--set", output]);
            Add("Activate Home Manager", [$"{output}/activate", "--driver-version", "1"],
                env: new Dictionary<string, string> { ["HOME_MANAGER_BACKUP_EXT"] = "hm-backup" });
        }
        void ActivateSystem(string output, string mode) =>
            PrivilegedStep(mode == "boot" ? "Activate system on next boot" : "Activate system", "activate-output", mode, output);
        void Cleanup()
        {
            var retention = r.Retention ?? 3;
            if (home && config.HomeProfileExists)
                Add($"Keep {retention} home generations", ["nix-env", "--profile", config.HomeProfile, "--delete-generations", $"+{retention}"]);
            else if (home)
                notes.Add($"No Home Manager profile at {config.HomeProfile}; generation deletion is skipped.");
            if (system)
                PrivilegedStep($"Keep {retention} system generations and collect garbage", "gc", retention.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else Add("Collect unreferenced store paths", ["nix-store", "--gc"]);
            notes.Add("Garbage collection deletes unreferenced store paths and older generations; these cannot be rolled back afterward.");
        }

        if (r.Action == Actions.ReviewBuild)
        {
            var root = Paths.ReviewRoot(config, r.ReviewId ?? "<new>");
            if (r.Generation is { } generation)
            {
                var profile = home ? config.HomeProfile : Paths.SystemProfile;
                Build(home ? $"Retain home generation {generation}" : $"Retain system generation {generation}",
                    $"{profile}-{generation}-link", $"{root}/{r.Scope}");
            }
            else if (r.Undo is not null)
            {
                if (undo is null) throw new ManagerException("The change to undo is unavailable.");
                foreach (var scope in new[] { Scopes.Home, Scopes.System })
                {
                    if (scope == Scopes.Home ? !home : !system) continue;
                    var number = scope == Scopes.Home ? undo.Generations.Home : undo.Generations.System;
                    if (number is null)
                        throw new ManagerException(scope == Scopes.Home
                            ? "The change did not record the home generation it replaced."
                            : "The change did not record the system generation it replaced.");
                    Build(scope == Scopes.Home ? $"Retain home generation {number}" : $"Retain system generation {number}",
                        $"{(scope == Scopes.Home ? config.HomeProfile : Paths.SystemProfile)}-{number}-link", $"{root}/{scope}");
                }
                if (undo.Files.Count > 0)
                    notes.Add("Applying the undo also restores the configuration files the change wrote.");
            }
            else
            {
                if (r.Edits == true)
                {
                    Add("Stage the edited files", [], @internal: InternalSteps.StageSources);
                    Add("Check the edits", [], @internal: InternalSteps.VerifyEdits);
                }
                var inputs = r.Inputs ?? [];
                var lockFile = r.Update || inputs.Count > 0 ? $"{root}/flake.lock" : null;
                if (lockFile is not null)
                    Add(inputs.Count > 0
                            ? inputs.Count == 1 ? $"Resolve {inputs[0]}" : $"Resolve {inputs.Count} inputs"
                            : "Resolve all flake inputs",
                        ["nix", "flake", "update", .. inputs, "--flake", config.Flake, "--output-lock-file", lockFile, "--log-format", "internal-json"],
                        progress: true);
                string[] lockFlags = lockFile is not null ? ["--reference-lock-file", lockFile, "--no-write-lock-file"] : ["--no-write-lock-file"];
                if (home) Build("Build home configuration", homeAttr, $"{root}/home", lockFlags);
                if (system) Build("Build system configuration", systemAttr, $"{root}/system", lockFlags);
                if (lockFile is not null)
                    notes.Add("Inputs are resolved into a separate lock file. The checkout's flake.lock changes only when you apply this review.");
                if (r.Edits == true)
                    notes.Add("Edits are built from a copy of the checkout. Your files change only when you apply this review.");
            }
            Add("Compare with the active configuration", [], @internal: InternalSteps.Compare);
            notes.Add("Nothing is activated. The build is kept until you apply or discard it.");
            return new Plan(steps, notes);
        }
        if (r.Action == Actions.ReviewApply)
        {
            if (review is null || review.Id != r.ReviewId) throw new ManagerException("The selected review is unavailable.");
            var mode = r.Activation ?? "switch";
            if (review.Edits is { } edits)
                Add(edits.Files.Count == 1 ? $"Write {edits.Files[0].Path}" : $"Write {edits.Files.Count} edited files", [],
                    @internal: InternalSteps.WriteSources);
            if (review.Inputs is not null) Add("Write the reviewed flake.lock", [], @internal: InternalSteps.WriteLock);
            if (review.Undo?.Point.Files is { Count: > 0 } files)
                Add(files.Count == 1 ? $"Restore {files[0].Path}" : $"Restore {files.Count} files", [], @internal: InternalSteps.RestoreSources);
            if (home && review.Home is not null && mode == "switch") ActivateHome(review.Home.Path);
            if (system && review.System is not null) ActivateSystem(review.System.Path, mode);
            if (!steps.Any(step => step.Internal is null))
                throw new ManagerException(mode == "boot"
                    ? "Only a system configuration can be applied on next boot."
                    : "This review does not contain the selected configuration.");
            if (steps.Any(step => step.Label.StartsWith("Activate Home", StringComparison.Ordinal))
                && steps.Any(step => step.Label.StartsWith("Activate system", StringComparison.Ordinal)))
                notes.Add("Home activates before system. If system activation fails, home remains active.");
            notes.Add("The exact reviewed outputs are activated; the checkout is not evaluated again.");
            return new Plan(steps, notes);
        }
        if (r.Update)
            Add("Update flake inputs", ["nix", "flake", "update", "--flake", config.Flake]);
        if (r.Action == Actions.Check)
            notes.Add("Resolve inputs in a temporary copy and compare package releases on each pin's channel. The checkout is unchanged.");
        else if (Actions.IsPackage(r.Action))
            notes.Add("Compare the latest release on the pin's channel and inherited recipes, discover hashes, then build before atomically replacing the pin.");
        else
        {
            if (r.Action == Actions.Build)
            {
                if (home) Build("Build home configuration", homeAttr, null);
                if (system) Build("Build system configuration", systemAttr, null);
            }
            if (r.Action is Actions.Switch or Actions.Boot)
            {
                var root = Paths.BuildRoot(config);
                if (home) Build("Build home configuration", homeAttr, $"{root}/home");
                if (system) Build("Build system configuration", systemAttr, $"{root}/system");
                if (home) ActivateHome($"{root}/home");
                if (system) ActivateSystem($"{root}/system", r.Action);
                if (home && system)
                    notes.Add("Home activates before system. If system activation fails, home may already be active.");
                if (system)
                    notes.Add("Once the privileged step starts, cancellation waits for it to finish before stopping subsequent steps.");
            }
            else if (r.Action == Actions.Rollback)
            {
                if (home) Add("Roll back home configuration", ["home-manager", "switch", "--rollback", .. r.Extra]);
                if (system) PrivilegedStep("Roll back system configuration", "rollback");
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
                Label = $"Go back to system generation {systemGeneration}",
                Argv = [.. config.Privileged, "activate-generation", Nix.NixText.Invariant(systemGeneration)],
                Cwd = config.Flake,
            });
        if (home is { } homeGeneration)
        {
            steps.Add(new Command
            {
                Label = $"Go back to home generation {homeGeneration}",
                Argv = ["nix-env", "--profile", config.HomeProfile, "--switch-generation", Nix.NixText.Invariant(homeGeneration)],
                Cwd = config.Flake,
            });
            steps.Add(new Command
            {
                Label = "Activate the earlier home generation",
                Argv = [$"{config.HomeProfile}-{homeGeneration}-link/activate", "--driver-version", "1"],
                Cwd = config.Flake,
                Env = new Dictionary<string, string> { ["HOME_MANAGER_BACKUP_EXT"] = "hm-backup" },
            });
        }
        return steps;
    }

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
