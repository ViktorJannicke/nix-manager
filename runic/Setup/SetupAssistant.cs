using System.Text.Json;
using System.Text.RegularExpressions;
using NixManager.Configuration;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;

namespace NixManager.Setup;

/// <summary>What the setup assistant found on this computer.</summary>
public sealed record SetupInfo
{
    public required string User { get; init; }
    public required string Home { get; init; }
    public required string HostName { get; init; }
    public required string System { get; init; }
    /// <summary>NixOS release such as "25.05", without the date suffix.</summary>
    public required string Release { get; init; }
    /// <summary>Whether the release is a development version (unstable).</summary>
    public bool Unstable { get; init; }
    /// <summary>nixpkgs revision of the running system, used to lock the new flake.</summary>
    public string? Revision { get; init; }
    public required string StateVersion { get; init; }
    public bool Flakes { get; init; }
    /// <summary>Files copied from /etc/nixos, relative to it.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>Files in /etc/nixos the user cannot read; they stay behind.</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];
    /// <summary>Imports that only work with channels, such as &lt;home-manager/nixos&gt;.</summary>
    public IReadOnlyList<string> ChannelImports { get; init; } = [];
    /// <summary>Why setup cannot continue, if it cannot.</summary>
    public DisplayText? Blocked { get; init; }
    public required string Suggested { get; init; }
}

/// <summary>The nixpkgs and Home Manager branches a new flake follows.</summary>
public sealed record SetupBranch(string Nixpkgs, string HomeManager);

/// <summary>The steps of creating a checkout, as <c>SetupStep</c> names them.</summary>
public static class SetupSteps
{
    public const string Copy = "copy";
    public const string Write = "write";
    public const string Git = "git";
    public const string Lock = "lock";
    public const string Check = "check";
    public const string Remember = "remember";

    /// <summary>Each step with its label, in order (<c>setupSteps</c>).</summary>
    public static readonly IReadOnlyList<(string Id, DisplayText Label)> All =
    [
        (Copy, DisplayText.Of("stored_copy_the_configuration_from_etc_nixos")),
        (Write, DisplayText.Of("stored_add_flake_nix_and_home_nix")),
        (Git, DisplayText.Of("stored_start_a_git_history")),
        (Lock, DisplayText.Of("stored_pin_nixpkgs_to_the_version_this_computer")),
        (Check, DisplayText.Of("stored_check_that_the_configuration_still_describes_this")),
        (Remember, DisplayText.Of("stored_remember_the_folder")),
    ];
}

/// <summary>
/// The setup assistant: turns a classic /etc/nixos configuration into a flake checkout the user
/// owns, with a standalone Home Manager configuration.
/// </summary>
public static partial class SetupAssistant
{
    /// <summary>The classic configuration, <c>NIX_MANAGER_SETUP_SOURCE</c> or <c>/etc/nixos</c> (<c>SOURCE</c>).</summary>
    public static string Source(ManagerEnvironment environment) =>
        environment.Variable("NIX_MANAGER_SETUP_SOURCE") is { Length: > 0 } source ? source : "/etc/nixos";

    private const string FlakesSetting = "extra-experimental-features = nix-command flakes";
    private static readonly Lock Gate = new();
    private static bool _recorded;
    private static string? _userNixConfig;

    /// <summary><c>NIX_CONFIG</c> as the user set it, before <see cref="AllowFlakes"/> added to it.</summary>
    public static string? UserNixConfig
    {
        get { lock (Gate) return _recorded ? _userNixConfig : Environment.GetEnvironmentVariable("NIX_CONFIG"); }
    }

    /// <summary>
    /// Asks Nix for flakes in this process's commands; a computer the setup assistant just set up turns them on with
    /// its first apply, and asking again where they are on is harmless (<c>allowFlakes</c>).
    /// </summary>
    public static void AllowFlakes(ManagerEnvironment environment)
    {
        var current = environment.Variable("NIX_CONFIG");
        lock (Gate)
        {
            if (!_recorded)
            {
                _userNixConfig = current;
                _recorded = true;
            }
        }
        if (current?.Contains(FlakesSetting, StringComparison.Ordinal) == true) return;
        Environment.SetEnvironmentVariable("NIX_CONFIG", string.IsNullOrEmpty(current) ? FlakesSetting : $"{current}\n{FlakesSetting}");
    }

    [GeneratedRegex(@"\bflakes\b")]
    private static partial Regex FlakesFeature();

    [GeneratedRegex(@"\bnix-command\b")]
    private static partial Regex NixCommandFeature();

    /// <summary>Whether the user's own Nix configuration turns flakes on (<c>flakesEnabled</c>).</summary>
    public static async Task<bool> FlakesEnabledAsync(ICommandRunner runner, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(new Command
        {
            Label = DisplayText.Of("stored_check_flakes"),
            Argv = ["nix", "config", "show", "experimental-features"],
            Quiet = true,
            AllowFailure = true,
            Env = new Dictionary<string, string> { ["NIX_CONFIG"] = UserNixConfig ?? "" },
        }, _ => { }, cancellationToken).ConfigureAwait(false);
        return result.Code == 0 && FlakesFeature().IsMatch(result.Stdout) && NixCommandFeature().IsMatch(result.Stdout);
    }

    /// <summary>Release branches to try for nixpkgs and Home Manager, most specific first (<c>branches</c>).</summary>
    public static IReadOnlyList<SetupBranch> Branches(string release, bool unstable) =>
        [.. unstable ? [] : new[] { new SetupBranch($"nixos-{release}", $"release-{release}") }, new SetupBranch("nixos-unstable", "master")];

    /// <summary>
    /// The readable files below <paramref name="root"/>, relative to it, and the files and folders the user cannot
    /// read; symbolic links and <c>.git</c> are left out (<c>walk</c>).
    /// </summary>
    public static (List<string> Files, List<string> Unreadable) Walk(string root, string directory = "")
    {
        var files = new List<string>();
        var unreadable = new List<string>();
        var folder = directory.Length > 0 ? Path.Combine(root, directory) : root;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            var path = directory.Length > 0 ? $"{directory}/{entry.Name}" : entry.Name;
            // Build results and other symlinks point into the store or elsewhere.
            if (entry.Name == ".git" || entry.LinkTarget is not null) continue;
            if (entry is DirectoryInfo)
            {
                try
                {
                    var (innerFiles, innerUnreadable) = Walk(root, path);
                    files.AddRange(innerFiles);
                    unreadable.AddRange(innerUnreadable);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add($"{path}/");
                }
            }
            else
            {
                try
                {
                    using (File.OpenRead(Path.Combine(root, path))) { }
                    files.Add(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add(path);
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        unreadable.Sort(StringComparer.Ordinal);
        return (files, unreadable);
    }

    [GeneratedRegex(@"/\*[\s\S]*?\*/")]
    private static partial Regex BlockComment();

    [GeneratedRegex("#.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"(?<=[\s(\[=:])<[A-Za-z][A-Za-z0-9_.+-]*(?:/[A-Za-z0-9_.+/-]*)?>")]
    private static partial Regex SearchPath();

    /// <summary>Search paths such as &lt;nixpkgs&gt; or &lt;home-manager/nixos&gt;, outside comments (<c>channelPaths</c>).</summary>
    public static List<string> ChannelPaths(string text) =>
        SearchPath().Matches(LineComment().Replace(BlockComment().Replace(text, ""), "")).Select(match => match.Value).ToList();

    [GeneratedRegex(@"^\d+\.\d+")]
    private static partial Regex ReleasePattern();

    [GeneratedRegex("pre")]
    private static partial Regex Prerelease();

    [GeneratedRegex(@"system\.stateVersion\s*=\s*""([^""]+)""")]
    private static partial Regex StateVersion();

    /// <summary>What the assistant finds on this computer (<c>readSetup</c>).</summary>
    public static async Task<SetupInfo> ReadAsync(ICommandRunner runner, ManagerEnvironment environment, string? source = null,
        CancellationToken cancellationToken = default)
    {
        source ??= Source(environment);
        var version = await runner.RunAsync(new Command
        {
            Label = DisplayText.Of("stored_read_nixos_version"),
            Argv = ["nixos-version", "--json"],
            Quiet = true,
            AllowFailure = true,
        }, _ => { }, cancellationToken).ConfigureAwait(false);
        string? full = null, revision = null;
        if (version.Code == 0)
        {
            try
            {
                using var document = JsonDocument.Parse(version.Stdout);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("nixosVersion", out var v) && v.ValueKind == JsonValueKind.String) full = v.GetString();
                    if (document.RootElement.TryGetProperty("nixpkgsRevision", out var r) && r.ValueKind == JsonValueKind.String) revision = r.GetString();
                }
            }
            catch (JsonException e)
            {
                throw new ManagerException(DisplayText.Of("stored_nixos_version_not_valid_json", ("reason", e.Message)));
            }
        }
        full ??= "";
        var release = ReleasePattern().Match(full) is { Success: true } match ? match.Value : "";
        var (files, unreadable) = Directory.Exists(source)
            ? FileErrors.Attempt(DisplayText.Of("stored_read_file", ("path", "/etc/nixos")), () => Walk(source), source)
            : ([], []);
        var nix = FileErrors.Attempt(DisplayText.Of("stored_read_the_configuration"), () => files
            .Where(file => file.EndsWith(".nix", StringComparison.Ordinal))
            .Select(file => (File: file, Text: File.ReadAllText(Path.Combine(source, file))))
            .ToList());
        var configuration = nix.FirstOrDefault(entry => entry.File == "configuration.nix").Text;
        var stateVersion = configuration is not null && StateVersion().Match(configuration) is { Success: true } state ? state.Groups[1].Value : release;
        var channelImports = nix.SelectMany(entry => ChannelPaths(entry.Text)).Distinct().Order(StringComparer.Ordinal).ToList();
        var blocked = release.Length == 0
            ? DisplayText.Of("stored_this_does_not_look_like_nixos_nixos")
            : !files.Contains("configuration.nix")
                ? DisplayText.Of("stored_there_is_no_to_start_from", ("file", $"{source}/configuration.nix"))
                : files.Contains("flake.nix")
                    ? DisplayText.Of("stored_is_a_flake_already_copy_it_to", ("folder", source))
                    : null;
        return new SetupInfo
        {
            User = environment.UserName,
            Home = environment.HomeDirectory,
            HostName = environment.HostName,
            System = ConfigLoader.CurrentPlatform(),
            Release = release,
            Unstable = Prerelease().IsMatch(full),
            Revision = revision,
            StateVersion = stateVersion,
            Flakes = await FlakesEnabledAsync(runner, cancellationToken).ConfigureAwait(false),
            Files = files,
            Unreadable = unreadable,
            ChannelImports = channelImports,
            Blocked = blocked,
            Suggested = Path.Combine(environment.HomeDirectory, "nixos-config"),
        };
    }

    private static string NixString(string text) => NixText.JavaScriptString(text);

    /// <summary>The new flake: this computer's NixOS configuration and the user's Home Manager configuration (<c>flakeTemplate</c>).</summary>
    public static string FlakeTemplate(SetupInfo info, SetupBranch branch) => $$"""
        {
          description = "{{info.HostName}}: system and home configuration";

          inputs = {
            nixpkgs.url = "github:NixOS/nixpkgs/{{branch.Nixpkgs}}";
            home-manager = {
              url = "github:nix-community/home-manager/{{branch.HomeManager}}";
              inputs.nixpkgs.follows = "nixpkgs";
            };
          };

          outputs =
            { nixpkgs, home-manager, ... }:
            {
              nixosConfigurations.{{NixText.Attr(info.HostName)}} = nixpkgs.lib.nixosSystem {
                modules = [
                  ./configuration.nix
                  {
                    nixpkgs.hostPlatform = nixpkgs.lib.mkDefault {{NixString(info.System)}};
                    # Nix Manager builds with flakes.
                    nix.settings.experimental-features = [
                      "nix-command"
                      "flakes"
                    ];
                  }
                ];
              };

              homeConfigurations.{{NixText.Attr($"{info.User}@{info.HostName}")}} = home-manager.lib.homeManagerConfiguration {
                pkgs = nixpkgs.legacyPackages.{{NixText.Attr(info.System)}};
                modules = [ ./home.nix ];
              };
            };
        }

        """;

    /// <summary>The user's first Home Manager configuration (<c>homeTemplate</c>).</summary>
    public static string HomeTemplate(SetupInfo info) => $$"""
        {
          home.username = {{NixString(info.User)}};
          home.homeDirectory = {{NixString(info.Home)}};
          # The release this home configuration started on; leave it as it is.
          home.stateVersion = {{NixString(info.StateVersion)}};

          programs.home-manager.enable = true;
        }

        """;

    /// <summary>
    /// Why a folder cannot hold the new checkout, if it cannot. An existing file counts as a
    /// folder that is not empty.
    /// </summary>
    public static DisplayText? TargetProblem(string target, string home)
    {
        if (!target.StartsWith('/')) return DisplayText.Of("stored_use_a_full_path_such_as_nixos");
        var path = Resolve(target);
        if (path != home && !path.StartsWith($"{home}/", StringComparison.Ordinal)) return DisplayText.Of("stored_choose_a_folder_inside_your_home_folder");
        if (path == home) return DisplayText.Of("stored_choose_a_new_folder_inside_your_home");
        if (File.Exists(path)) return DisplayText.Of("stored_this_folder_is_not_empty");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) return DisplayText.Of("stored_this_folder_is_not_empty");
        return null;
    }

    /// <summary><c>path.resolve</c>: an absolute path without <c>.</c>, <c>..</c> or a trailing slash.</summary>
    private static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }

    private static string LastLine(string text) => text.Trim().Split('\n').Where(line => line.Length > 0).LastOrDefault() ?? "";

    /// <summary>
    /// Creates the checkout in <paramref name="target"/>; <paramref name="progress"/> hears each step's id as it starts.
    /// A failed attempt removes the folder again, which was missing or empty before (<c>createCheckout</c>).
    /// </summary>
    /// <returns>The checkout's full path.</returns>
    public static async Task<string> CreateCheckoutAsync(SetupInfo info, string target, Action<string> progress, ICommandRunner runner,
        ManagerEnvironment environment, string? source = null, CancellationToken cancellationToken = default)
    {
        source ??= Source(environment);
        if (info.Blocked is { } blocked) throw new ManagerException(blocked);
        if (TargetProblem(target, info.Home) is { } problem) throw new ManagerException(problem);
        var path = Resolve(target);
        try
        {
            await CreateAsync(info, path, progress, runner, environment, source, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task CreateAsync(SetupInfo info, string path, Action<string> progress, ICommandRunner runner,
        ManagerEnvironment environment, string source, CancellationToken cancellationToken)
    {
        Task<CommandResult> Command(DisplayText label, IReadOnlyList<string> argv) => runner.RunAsync(new Command
        {
            Label = label,
            Argv = argv,
            Cwd = path,
            Quiet = true,
            AllowFailure = true,
        }, _ => { }, cancellationToken);
        async Task Run(DisplayText label, IReadOnlyList<string> argv)
        {
            var result = await Command(label, argv).ConfigureAwait(false);
            if (result.Code != 0)
                throw new ManagerException(LastLine(result.Stderr) is { Length: > 0 } line
                    ? DisplayText.Of("stored_step_failed_because", ("step", label), ("reason", DisplayText.Plain(line)))
                    : DisplayText.Of("stored_failed", ("label", label)));
        }

        progress(SetupSteps.Copy);
        FileErrors.Attempt(DisplayText.Of("stored_copy_the_configuration"), () =>
        {
            Directory.CreateDirectory(path);
            foreach (var file in info.Files)
            {
                var destination = Path.Combine(path, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(source, file), destination, overwrite: false);
            }
        });

        progress(SetupSteps.Write);
        var tried = Branches(info.Release, info.Unstable);
        FileErrors.Attempt(DisplayText.Of("stored_write_the_new_files"), () =>
        {
            WriteNew(Path.Combine(path, "flake.nix"), FlakeTemplate(info, tried[0]));
            WriteNew(Path.Combine(path, "home.nix"), HomeTemplate(info));
        });

        progress(SetupSteps.Git);
        await Run(DisplayText.Of("stored_start_git"), ["git", "init", "--quiet"]).ConfigureAwait(false);
        await Run(DisplayText.Of("stored_add_the_files_to_git"), ["git", "add", "--", .. info.Files, "flake.nix", "home.nix"]).ConfigureAwait(false);

        progress(SetupSteps.Lock);
        // The running revision keeps the first build close to the running system; updates later follow the branch
        // in flake.nix.
        string[] pin = info.Revision is { Length: > 0 } revision ? ["--override-input", "nixpkgs", $"github:NixOS/nixpkgs/{revision}"] : [];
        var locked = false;
        var failure = "";
        for (var index = 0; index < tried.Count; index++)
        {
            if (index > 0)
            {
                var branch = tried[index];
                FileErrors.Attempt(DisplayText.Of("stored_write", ("file", "flake.nix")), () => File.WriteAllText(Path.Combine(path, "flake.nix"), FlakeTemplate(info, branch)));
            }
            var result = await Command(DisplayText.Of("stored_pin_the_inputs"), ["nix", "flake", "lock", .. pin]).ConfigureAwait(false);
            if (result.Code == 0)
            {
                locked = true;
                break;
            }
            failure = LastLine(result.Stderr);
        }
        if (!locked) throw new ManagerException(DisplayText.Of("stored_could_not_pin_the_inputs", ("reason", DisplayText.Plain(failure))));
        await Run(DisplayText.Of("stored_add_the_lock_file_to_git"), ["git", "add", "--", "flake.nix", "flake.lock"]).ConfigureAwait(false);

        progress(SetupSteps.Check);
        var hosts = await new ConfigLoader(runner, environment).DetectHostsAsync(path, info.HostName, info.User).ConfigureAwait(false);
        var host = hosts.Values.First();
        await Run(DisplayText.Of("stored_evaluate_the_system"),
            ["nix", "eval", "--raw", $"{path}#nixosConfigurations.{NixText.Attr(host.NixosConfiguration)}.config.system.build.toplevel.drvPath"]).ConfigureAwait(false);
        await Run(DisplayText.Of("stored_evaluate_the_home_configuration"),
            ["nix", "eval", "--raw", $"{path}#homeConfigurations.{NixText.Attr(host.HomeConfiguration)}.activationPackage.drvPath"]).ConfigureAwait(false);
        // A first commit when Git knows who the user is; staged files are enough for flakes.
        await Command(DisplayText.Of("stored_commit"), ["git", "commit", "--quiet", "-m", $"Start from {source}"]).ConfigureAwait(false);

        progress(SetupSteps.Remember);
        FileErrors.Attempt(DisplayText.Of("stored_remember_the_folder"), () =>
        {
            var file = new ConfigLoader(runner, environment).RememberedCheckout;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"{path}\n");
        });
    }

    /// <summary>Writes a file that must not exist yet (<c>writeFile</c> with flag <c>wx</c>).</summary>
    private static void WriteNew(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }
}
