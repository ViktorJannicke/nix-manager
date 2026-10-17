using NixManager.Domain;

namespace NixManager.Tests;

/// <summary>A configuration in a fresh temporary directory, as <c>fixture()</c> in <c>test/helpers.ts</c>.</summary>
internal static class TestConfig
{
    public static Config Create(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "nix-manager-test");
        var state = Path.Combine(root, "state");
        return new Config
        {
            Flake = root,
            StateDir = state,
            HomeProfile = Path.Combine(state, "nix/profiles/home-manager"),
            HomeProfileExists = true,
            HostKey = "test",
            Hosts = new Dictionary<string, Host>
            {
                ["test"] = new()
                {
                    System = "x86_64-linux",
                    UserName = "test",
                    NixosHostName = "test-machine",
                    HomeConfiguration = "user-test",
                    NixosConfiguration = "test-machine",
                },
            },
            Privileged = ["pkexec", "/immutable/bin/nix-manager", "--privileged"],
        };
    }

    /// <summary>A new empty directory with a flake.nix, removed by the caller.</summary>
    public static string TemporaryFlake()
    {
        var directory = Directory.CreateTempSubdirectory("nix-manager-test-").FullName;
        File.WriteAllText(Path.Combine(directory, "flake.nix"), "{}");
        return directory;
    }
}
