using System.Text.Json;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Operations;

namespace NixManager.Configuration;

/// <summary>The process environment a configuration is read from, replaceable in tests.</summary>
public sealed record ManagerEnvironment(Func<string, string?> Variable, string HostName, string UserName, string HomeDirectory)
{
    public static ManagerEnvironment Current { get; } = new(
        Environment.GetEnvironmentVariable,
        System.Net.Dns.GetHostName(),
        Environment.UserName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string StateHome => Variable("XDG_STATE_HOME") is { Length: > 0 } state ? state : Path.Combine(HomeDirectory, ".local/state");

    /// <summary><c>NIX_MANAGER_STATE_DIR</c>, or <c>$XDG_STATE_HOME/nix-manager</c>.</summary>
    public string StateDir => Variable("NIX_MANAGER_STATE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(StateHome, "nix-manager");
}

/// <summary>
/// Selects the checkout and reads the host registry: an explicit registry, the
/// checkout's <c>data/hosts.nix</c>, or the host read from the flake's own configurations.
/// </summary>
public sealed partial class ConfigLoader(ICommandRunner runner, ManagerEnvironment? environment = null)
{
    private readonly ManagerEnvironment _env = environment ?? ManagerEnvironment.Current;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex WordSeparator();

    /// <summary>Words of a configuration or host name, for telling <c>alice-laptop</c> from <c>alice-desktop</c>.</summary>
    private static HashSet<string> Words(string name) =>
        [.. WordSeparator().Split(name.ToLowerInvariant()).Where(word => word.Length > 0)];

    private static int Shared(string a, string b)
    {
        var other = Words(b);
        return Words(a).Count(other.Contains);
    }

    private static ManagerException Ambiguous(string what, IEnumerable<string> names) =>
        new($"Cannot tell which {what} belongs to this computer: {string.Join(", ", names)}. Choose one with --host, or list the hosts in data/hosts.nix.");

    /// <summary>"evaluate": the names do not decide, and the host names must be read.</summary>
    public const string Evaluate = "evaluate";

    /// <summary>
    /// The NixOS configuration for a host name: the one with that name, as nixos-rebuild picks it, the one setting
    /// it as <c>networking.hostName</c>, or the only one (<c>pickSystem</c>).
    /// </summary>
    public static string PickSystem(string target, IReadOnlyList<string> names, IReadOnlyDictionary<string, string>? hostNames = null)
    {
        if (names.Contains(target)) return target;
        if (names.Count == 1) return names[0];
        if (names.Count == 0) throw new ManagerException("The flake has no nixosConfigurations.");
        if (hostNames is null) return Evaluate;
        var matches = names.Where(name => hostNames.GetValueOrDefault(name) == target).ToList();
        if (matches.Count == 1) return matches[0];
        throw Ambiguous("NixOS configuration", names);
    }

    /// <summary>
    /// The Home Manager configuration for a user on a host: <c>user@host</c> or <c>user</c>, the only one, or the
    /// user's configuration whose name shares the most words with the host (<c>pickHome</c>).
    /// </summary>
    public static string PickHome(string user, IReadOnlyList<string> hosts, IReadOnlyList<string> names, IReadOnlyDictionary<string, string>? userNames = null)
    {
        foreach (var name in hosts.Select(host => $"{user}@{host}").Append(user))
            if (names.Contains(name)) return name;
        if (names.Count == 1) return names[0];
        if (names.Count == 0)
            throw new ManagerException("The flake has no homeConfigurations. Nix Manager needs a standalone Home Manager configuration for your user.");
        if (userNames is null) return Evaluate;
        var own = names.Where(name => userNames.GetValueOrDefault(name) == user).ToList();
        if (own.Count == 1) return own[0];
        int Score(string name) => hosts.Count == 0 ? 0 : hosts.Max(host => Shared(name, host));
        var best = own.Select(Score).Append(0).Max();
        var closest = own.Where(name => Score(name) == best).ToList();
        if (best > 0 && closest.Count == 1) return closest[0];
        throw Ambiguous("Home Manager configuration", own.Count > 0 ? own : names);
    }

    /// <summary>The running machine's platform, without evaluating a configuration.</summary>
    public static string CurrentPlatform() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64-linux",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64-linux",
            var other => $"{other.ToString().ToLowerInvariant()}-linux",
        };

    /// <summary>
    /// The Home Manager profile in the modern (<c>~/.local/state/nix/profiles</c>) or legacy
    /// (<c>/nix/var/nix/profiles/per-user/USER</c>) folder; an existing profile stays where it is
    /// (<c>chooseHomeProfile</c>).
    /// </summary>
    public static string ChooseHomeProfile(string modern, string legacy, Func<string, bool>? exists = null)
    {
        exists ??= path => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
        var modernProfile = Path.Combine(modern, "home-manager");
        var legacyProfile = Path.Combine(legacy, "home-manager");
        if (exists(modernProfile)) return modernProfile;
        if (exists(legacyProfile)) return legacyProfile;
        return exists(modern) || !exists(legacy) ? modernProfile : legacyProfile;
    }

    /// <summary>Where the setup assistant remembers the checkout it created.</summary>
    public string RememberedCheckout => Path.Combine(_env.StateDir, "checkout");

    private async Task<CommandResult> Quiet(string label, string[] argv, bool allowFailure = false, CancellationToken cancellationToken = default) =>
        await runner.RunAsync(new Command { Label = label, Argv = argv, Quiet = true, AllowFailure = allowFailure ? true : null }, _ => { }, cancellationToken)
            .ConfigureAwait(false);

    private async Task<JsonElement?> EvalJson(string label, string installable, string apply, bool allowFailure = false)
    {
        var result = await Quiet(label, ["nix", "eval", "--json", installable, "--apply", apply], allowFailure: true).ConfigureAwait(false);
        if (result.Code == 0)
        {
            using var document = JsonDocument.Parse(result.Stdout);
            return document.RootElement.Clone();
        }
        if (allowFailure) return null;
        throw new ManagerException($"{label}: {result.Stderr.Trim().Split('\n')[^1]}");
    }

    private static List<string> Strings(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray().Select(item => item.GetString() ?? "")] : [];

    private static Dictionary<string, string> StringMap(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } map
            ? map.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())
            : [];

    /// <summary>A registry with one host, read from the flake's own configurations (<c>detectHosts</c>).</summary>
    public async Task<Dictionary<string, Host>> DetectHostsAsync(string flake, string target, string user)
    {
        var systemsTask = EvalJson("List NixOS configurations", $"{flake}#nixosConfigurations", "builtins.attrNames");
        var homesTask = EvalJson("List Home Manager configurations", $"{flake}#homeConfigurations", "builtins.attrNames", allowFailure: true);
        var systems = Strings(await systemsTask.ConfigureAwait(false));
        var homes = Strings(await homesTask.ConfigureAwait(false));
        var system = PickSystem(target, systems);
        var hostName = system;
        if (system == Evaluate)
        {
            var hostNames = StringMap(await EvalJson("Read host names", $"{flake}#nixosConfigurations",
                "builtins.mapAttrs (_: c: c.config.networking.hostName)").ConfigureAwait(false));
            system = PickSystem(target, systems, hostNames);
            hostName = hostNames.GetValueOrDefault(system) ?? system;
        }
        var hosts = new[] { target, hostName, system }.Distinct().ToList();
        var home = PickHome(user, hosts, homes);
        if (home == Evaluate)
        {
            var userNames = StringMap(await EvalJson("Read Home Manager users", $"{flake}#homeConfigurations",
                "builtins.mapAttrs (_: c: c.config.home.username)").ConfigureAwait(false));
            home = PickHome(user, hosts, homes, userNames);
        }
        return new Dictionary<string, Host>
        {
            [system] = new Host
            {
                System = CurrentPlatform(),
                UserName = user,
                NixosConfiguration = system,
                NixosHostName = hostName,
                HomeConfiguration = home,
            },
        };
    }

    private static async Task<bool> IsConfiguration(string path) =>
        File.Exists(Path.Combine(path, "data/hosts.nix")) ||
        (File.Exists(Path.Combine(path, "flake.nix")) &&
         (await File.ReadAllTextAsync(Path.Combine(path, "flake.nix")).ConfigureAwait(false)).Contains("nixosConfigurations", StringComparison.Ordinal));

    /// <summary>
    /// The checkout to manage: the one asked for, the Git checkout this runs in when it is a system
    /// configuration, the one the setup assistant created, or <c>~/my-flakes</c> (<c>selectCheckout</c>).
    /// </summary>
    public async Task<(string Path, bool Chosen)> SelectCheckoutAsync(string? flake)
    {
        var asked = flake is { Length: > 0 } ? flake : _env.Variable("NIX_MANAGER_FLAKE");
        if (asked is { Length: > 0 }) return (asked, true);
        CommandResult repo;
        try
        {
            repo = await Quiet("Find checkout", ["git", "rev-parse", "--show-toplevel"], allowFailure: true).ConfigureAwait(false);
        }
        catch (ManagerException)
        {
            // Without Git, as on a NixOS installation the setup assistant has not set up yet, this is no checkout.
            repo = new CommandResult(127, "", "");
        }
        var path = repo.Stdout.Trim();
        if (repo.Code == 0 && await IsConfiguration(path).ConfigureAwait(false)) return (path, false);
        var remembered = File.Exists(RememberedCheckout) ? (await File.ReadAllTextAsync(RememberedCheckout).ConfigureAwait(false)).Trim() : "";
        return (remembered.Length > 0 ? remembered : Path.Combine(_env.HomeDirectory, "my-flakes"), false);
    }

    /// <summary>The configuration an operation runs with (<c>loadConfig</c>).</summary>
    public async Task<Config> LoadAsync(string? flake, string? host, IReadOnlyList<string> privileged)
    {
        var selected = (await SelectCheckoutAsync(flake).ConfigureAwait(false)).Path;
        string path;
        try
        {
            path = Path.GetFullPath(selected);
            if (Directory.Exists(path)) path = Native.RealPath(path) ?? path;
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            throw new ManagerException($"Resolve flake directory: {e.Message}");
        }
        if (!Directory.Exists(path))
            throw new ManagerException($"Resolve flake directory: ENOENT: no such file or directory, realpath '{selected}'");
        if (!File.Exists(Path.Combine(path, "flake.nix")))
            throw new ManagerException($"Required file not found: {path}/flake.nix");
        var target = host is { Length: > 0 } ? host : _env.Variable("NIX_MANAGER_HOST") is { Length: > 0 } fromEnv ? fromEnv : _env.HostName;
        // Without a registry the host is read from the flake's configurations.
        string? registry = null;
        if (_env.Variable("NIX_MANAGER_HOSTS") is { Length: > 0 } registryPath)
        {
            try { registry = await File.ReadAllTextAsync(registryPath).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ManagerException($"Read host registry: {e.Message}"); }
        }
        else if (File.Exists(Path.Combine(path, "data/hosts.nix")))
            registry = (await runner.RunAsync(new Command
            {
                Label = "Read host registry",
                Argv = ["nix", "eval", "--json", "--file", Path.Combine(path, "data/hosts.nix")],
                Quiet = true,
            }, _ => { }, CancellationToken.None).ConfigureAwait(false)).Stdout;
        Dictionary<string, Host> hosts;
        string hostKey;
        if (registry is null)
        {
            hosts = await DetectHostsAsync(path, target, _env.UserName).ConfigureAwait(false);
            hostKey = hosts.Keys.First();
        }
        else
        {
            hosts = ParseRegistry(registry);
            hostKey = HostResolution.Resolve(hosts, target).Key;
        }
        var homeProfile = ChooseHomeProfile(
            Path.Combine(_env.StateHome, "nix/profiles"),
            Path.Combine(_env.Variable("NIX_STATE_DIR") is { Length: > 0 } nixState ? nixState : "/nix/var/nix", "profiles/per-user", _env.UserName));
        return new Config
        {
            Flake = path,
            Hosts = hosts,
            HostKey = hostKey,
            HomeProfile = homeProfile,
            HomeProfileExists = File.Exists(homeProfile) || Directory.Exists(homeProfile),
            Privileged = privileged,
            StateDir = _env.StateDir,
        };
    }

    public static Dictionary<string, Host> ParseRegistry(string json)
    {
        try
        {
            var hosts = ManagerJson.Deserialize<Dictionary<string, Host>>(json)
                ?? throw new ManagerException("Invalid host registry: expected an object of hosts.");
            return hosts;
        }
        catch (JsonException e)
        {
            throw new ManagerException($"Invalid host registry: {e.Message}");
        }
    }
}
