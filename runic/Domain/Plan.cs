using System.Text.RegularExpressions;

namespace NixManager.Domain;

/// <summary>One step of a plan: a process to start, or an internal step (<c>Command</c> in <c>src/domain.ts</c>).</summary>
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

/// <summary>Builds the steps of an operation (<c>plan</c> in <c>src/domain.ts</c>).</summary>
public static partial class Planner
{
    private static readonly string[] NixFlags = ["--log-format", "internal-json", "--print-out-paths"];

    public static Plan Plan(Config config, Request r)
    {
        RequestValidation.Validate(r);
        var host = HostResolution.Resolve(config.Hosts, r.Host is { Length: > 0 } ? r.Host : config.HostKey).Value;
        if (Actions.IsReview(r.Action))
            throw new ManagerException("Reviewed changes are prepared and applied by the Bun version until the review workflow is ported.");
        var steps = new List<Command>();
        var notes = new List<string>();
        void Add(string label, IReadOnlyList<string> argv, bool progress = false, IReadOnlyDictionary<string, string>? env = null) =>
            steps.Add(new Command { Label = label, Argv = argv, Cwd = config.Flake, Env = env, Progress = progress ? true : null });
        void PrivilegedStep(string label, params string[] args) => Add(label, [.. config.Privileged, .. args]);
        var home = r.IncludesHome;
        var system = r.IncludesSystem;
        var homeAttr = $"{config.Flake}#homeConfigurations.{Nix.NixText.Attr(host.HomeConfiguration)}.activationPackage";
        var systemAttr = $"{config.Flake}#nixosConfigurations.{Nix.NixText.Attr(host.NixosConfiguration)}.config.system.build.toplevel";
        void Build(string label, string installable, string? link) =>
            Add(label, ["nix", "build", .. link is null ? ["--no-link"] : new[] { "--out-link", link }, .. NixFlags, installable, .. r.Extra],
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
