using System.Text.Json.Serialization;

namespace NixManager.Software;

/// <summary>A module file whose settings can be edited (<c>ModuleFile</c>).</summary>
/// <param name="Active">The host's configuration imports it; null when it sets nothing that could show it.</param>
public sealed record ModuleFile(string File, IReadOnlyList<string> Names,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Active);
