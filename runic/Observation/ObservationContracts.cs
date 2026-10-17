using System.Text.Json;
using System.Text.Json.Serialization;

namespace NixManager.Observation;

// The observed state of the computer and the checkout (src/contracts.ts), as the dashboard reads it.

/// <summary>One generation (restore point) of a profile (<c>Generation</c>).</summary>
public sealed record Generation
{
    public int Number { get; init; }
    public required string CreatedAt { get; init; }
    public required string Output { get; init; }
    public bool Current { get; init; }
    public bool? Booted { get; init; }
    /// <summary>NixOS version label for system generations.</summary>
    public string? Version { get; init; }
    public string? Kernel { get; init; }
}

/// <summary>The checkout's Git state (<c>Repository</c>).</summary>
public sealed record RepositoryState
{
    /// <summary>Identifies the checkout's content: the lock file, the status and the diff to HEAD.</summary>
    public required string Fingerprint { get; init; }
    public bool Clean { get; init; }
    public IReadOnlyList<string> Changes { get; init; } = [];
    public string? Branch { get; init; }
    public string? Revision { get; init; }
    public string? Subject { get; init; }
    public string? CommittedAt { get; init; }
    /// <summary>The remote branch the checkout's branch pushes to, such as origin/main.</summary>
    public string? Upstream { get; init; }
    /// <summary>flake.lock differs from the committed version.</summary>
    public bool LockModified { get; init; }
    public required string LockHash { get; init; }
}

public sealed record ProfileGenerations
{
    public IReadOnlyList<Generation> Home { get; init; } = [];
    public IReadOnlyList<Generation> System { get; init; } = [];
}

public sealed record CurrentOutputs
{
    public string? Home { get; init; }
    public string? System { get; init; }
    public string? Booted { get; init; }
}

public sealed record KernelVersions
{
    public string? Current { get; init; }
    public string? Booted { get; init; }
}

public sealed record DiskSpace
{
    public required string Path { get; init; }
    public long Free { get; init; }
    public long Total { get; init; }
}

/// <summary>What the dashboard shows about the computer (<c>SystemState</c>).</summary>
public sealed record SystemState
{
    public required string ObservedAt { get; init; }
    public required RepositoryState Repository { get; init; }
    public required ProfileGenerations Generations { get; init; }
    public required CurrentOutputs Current { get; init; }
    public string? NixosVersion { get; init; }
    public required KernelVersions Kernel { get; init; }
    /// <summary>The running kernel, initrd or modules differ from the active system.</summary>
    public bool RebootRequired { get; init; }
    public DiskSpace? Disk { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}
