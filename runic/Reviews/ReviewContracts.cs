using System.Text.Json;
using System.Text.Json.Serialization;
using NixManager.Edits;
using NixManager.Nix;
using NixManager.Operations;

namespace NixManager.Reviews;

// The stored shapes of reviews (reviews/ID/review.json) and the edits built into them, as src/contracts.ts and
// src/draft.ts define them. Records written by the Bun version read here and the other way round: camelCase,
// absent members omitted, unknown members kept.

public static class ReviewSources
{
    public const string Checkout = "checkout", Inputs = "inputs", Generation = "generation";
}

/// <summary>A package a closure comparison found changed (<c>PackageChange</c>).</summary>
public sealed record PackageChange
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Before { get; init; } = [];
    public IReadOnlyList<string> After { get; init; } = [];
    /// <summary>Closure size change in bytes.</summary>
    public long SizeDelta { get; init; }
    /// <summary><c>added</c>, <c>removed</c>, <c>changed</c> or <c>rebuilt</c>.</summary>
    public required string Kind { get; init; }
}

/// <summary>A flake input whose locked revision changed (<c>LockChange</c>).</summary>
public sealed record LockChange
{
    public required string Name { get; init; }
    public LockRef? Before { get; init; }
    public LockRef? After { get; init; }
}

public sealed record VersionPair
{
    public string? Before { get; init; }
    public string? After { get; init; }
}

/// <summary>One built configuration of a review, compared with the active one (<c>ReviewOutput</c>).</summary>
public sealed class ReviewOutput
{
    // Members in the order compareReview writes them.
    public required string Path { get; set; }
    public List<PackageChange> Changes { get; set; } = [];
    /// <summary>The active configuration could be compared (false when, for example, none exists).</summary>
    public bool Compared { get; set; }
    public long? ClosureSize { get; set; }
    public long? CurrentClosureSize { get; set; }
    public VersionPair? Version { get; set; }
    public VersionPair? Kernel { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>A package an edit added to or removed from a configuration (<c>EditedPackage</c>).</summary>
public sealed record EditedPackage
{
    public required string Out { get; init; }
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Version { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Description { get; init; }
    public required string File { get; init; }
}

/// <summary>An option an edit changed, with its values before and after; an absent value is undefined.</summary>
public sealed record OptionEffect
{
    public required string Option { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Before { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement After { get; init; }
}

/// <summary>What the edits themselves did to one configuration, by evaluation (<c>EditEffect</c>).</summary>
public sealed record EditEffect
{
    /// <summary><c>home</c> or <c>system</c>.</summary>
    public required string Scope { get; init; }
    public IReadOnlyList<EditedPackage> Added { get; init; } = [];
    public IReadOnlyList<EditedPackage> Removed { get; init; } = [];
    public IReadOnlyList<OptionEffect> Options { get; init; } = [];
}

/// <summary>Source edits a review was built with, written to the checkout on apply (<c>ReviewEdits</c>).</summary>
public sealed class ReviewEdits
{
    public List<Change> Changes { get; set; } = [];
    /// <summary>Hash of the change list, compared with the current draft.</summary>
    public required string Hash { get; set; }
    public List<DraftFile> Files { get; set; } = [];
    public List<EditEffect> Effects { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

public sealed class ReviewInputs
{
    public List<string> Names { get; set; } = [];
    public List<LockChange> Changes { get; set; } = [];
    /// <summary>Hash of the checkout's flake.lock the proposal was resolved from.</summary>
    public required string BaseLockHash { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>For an undo: the applied change it reverses, from that change's operation.</summary>
public sealed record ReviewUndo
{
    public required string OperationId { get; init; }
    public required string AppliedAt { get; init; }
    public required UndoPoint Point { get; init; }
}

public sealed record AfterBoot
{
    /// <summary>The system output applied on next boot.</summary>
    public required string System { get; init; }
}

/// <summary>A prepared review: its built outputs and what applying it changes (<c>BuildReview</c>).</summary>
public sealed class BuildReview
{
    public required string Id { get; set; }
    public string Host { get; set; } = "";
    public required string Scope { get; set; }
    public required string CreatedAt { get; set; }
    /// <summary>Checkout state the outputs were evaluated from.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary><c>checkout</c>, <c>inputs</c> or <c>generation</c>.</summary>
    public required string Source { get; set; }
    // The remaining members in the order compareReview writes them; keepForBoot appends afterBoot.
    public int? Generation { get; set; }
    public ReviewUndo? Undo { get; set; }
    public ReviewEdits? Edits { get; set; }
    public ReviewInputs? Inputs { get; set; }
    public ReviewOutput? Home { get; set; }
    public ReviewOutput? System { get; set; }
    public string? OperationId { get; set; }
    /// <summary>Bytes Nix downloaded while preparing the review.</summary>
    public long? Downloaded { get; set; }
    /// <summary>The system part was applied on next boot; the home part follows once the computer runs that system.</summary>
    public AfterBoot? AfterBoot { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    public BuildReview Clone() => ManagerJson.Deserialize<BuildReview>(ManagerJson.Serialize(this))!;
}
