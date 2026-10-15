namespace NixManager.Domain;

/// <summary>One host of the registry (<c>/etc/nix-manager/hosts.json</c>, <c>data/hosts.nix</c>).</summary>
public sealed record Host
{
    public required string System { get; init; }
    public string? UserName { get; init; }
    public required string NixosConfiguration { get; init; }
    public required string NixosHostName { get; init; }
    public required string HomeConfiguration { get; init; }
}

/// <summary>The checkout, host and paths an operation runs with (<c>Config</c> in <c>src/domain.ts</c>).</summary>
public sealed record Config
{
    public required string Flake { get; init; }
    public required IReadOnlyDictionary<string, Host> Hosts { get; init; }
    public required string HostKey { get; init; }
    public required string StateDir { get; init; }
    public required string HomeProfile { get; init; }
    public required bool HomeProfileExists { get; init; }
    /// <summary>
    /// The privileged entry point as an argument vector prefix, <c>pkexec PROGRAM --privileged</c>. Plans show it
    /// as the Bun version did; execution runs it through Polkit (<see cref="Execution.PrivilegedCommandRunner"/>).
    /// </summary>
    public required IReadOnlyList<string> Privileged { get; init; }

    public Host Host => Hosts[HostKey];
}

public static class Paths
{
    public const string SystemProfile = "/nix/var/nix/profiles/system";
    public static string ReviewRoot(Config config, string id) => $"{config.StateDir}/reviews/{id}";
    public static string BuildRoot(Config config) => $"{config.StateDir}/build";
    public static string Operations(Config config) => Path.Combine(config.StateDir, "operations");
}

public static class HostResolution
{
    /// <summary>The host named <paramref name="name"/> by key or NixOS host name (<c>resolveHost</c>).</summary>
    public static KeyValuePair<string, Host> Resolve(IReadOnlyDictionary<string, Host> hosts, string name)
    {
        var matches = hosts.Where(entry => entry.Key == name || entry.Value.NixosHostName == name).ToList();
        if (matches.Count != 1)
            throw new ManagerException($"Unknown or ambiguous host: {name}. Available: {string.Join(", ", hosts.Keys)}");
        return matches[0];
    }
}
