using System.Text.Json;
using System.Text.Json.Serialization;

namespace NixManager.Edits;

// The draft's shapes (src/draft.ts): pending edits rendered into the files they change.

/// <summary>One line of a diff: <c>" "</c> kept, <c>"+"</c> added, <c>"-"</c> removed.</summary>
public sealed record DiffLine(string Kind, string Text);

/// <summary>Changed lines with context.</summary>
public sealed record Hunk
{
    /// <summary>First line of the hunk in the original file, starting at 1.</summary>
    public int Line { get; init; }
    public IReadOnlyList<DiffLine> Lines { get; init; } = [];
}

/// <summary>A file the edits change (<c>DraftFile</c>).</summary>
public sealed class DraftFile
{
    public required string Path { get; set; }
    /// <summary>Hash of the file the edits start from; empty for a file the edits create.</summary>
    public string BaseHash { get; set; } = "";
    /// <summary>True for a file the edits create; absent otherwise.</summary>
    public bool? Created { get; set; }
    public List<Hunk> Hunks { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>A file with its content before and after the edits, in memory (<c>EditedFile</c>).</summary>
public sealed class EditedFile
{
    public required string Path { get; init; }
    public string BaseHash { get; init; } = "";
    public bool? Created { get; init; }
    public required string Before { get; init; }
    public required string After { get; set; }
    public List<Hunk> Hunks { get; set; } = [];

    public DraftFile ToDraftFile() => new() { Path = Path, BaseHash = BaseHash, Created = Created, Hunks = Hunks };
}

public sealed record DraftProblem
{
    /// <summary>Index of the change in the draft.</summary>
    public int Index { get; init; }
    public required string Message { get; init; }
}

/// <summary>The draft as the dashboard and <c>software pending --json</c> show it (<c>RenderedDraft</c>).</summary>
public sealed record RenderedDraft
{
    public IReadOnlyList<Change> Changes { get; init; } = [];
    public IReadOnlyList<DraftFile> Files { get; init; } = [];
    public IReadOnlyList<DraftProblem> Problems { get; init; } = [];
    /// <summary>Identifies the change list, so a review can tell whether the draft moved on.</summary>
    public required string Hash { get; init; }
}

/// <summary>An edit of one file's text: the new text and the range of the old one it replaced (<c>Edit</c>).</summary>
public sealed record SourceEdit(string Source, int Start, int End, string Text)
{
    /// <summary>Replaces <c>[start, end)</c> of <paramref name="source"/> with <paramref name="text"/> (<c>apply</c>).</summary>
    public static SourceEdit Apply(string source, int start, int end, string text) =>
        new(string.Concat(source.AsSpan(0, start), text, source.AsSpan(end)), start, end, text);
}
