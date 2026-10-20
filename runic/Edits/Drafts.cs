using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Nix;
using NixManager.Observation;
using NixManager.Operations;

namespace NixManager.Edits;

/// <summary>The draft rendered in memory: the changed files and the edits that could not be applied.</summary>
public sealed record RenderResult(IReadOnlyList<EditedFile> Files, IReadOnlyList<DraftProblem> Problems);

/// <summary>Pending edits rendered into the files they change, and the stored draft.</summary>
public static partial class Drafts
{
    /// <summary>What a module created for the draft starts as (<c>moduleTemplate</c>).</summary>
    public const string ModuleTemplate = "{ pkgs, ... }:\n{\n}\n";

    /// <summary>Package list bindings each scope adds to; category modules use <c>packages</c> (<c>scopeBindings</c>).</summary>
    public static IReadOnlyList<string> ScopeBindings(string scope) => scope switch
    {
        Targets.Home => PackageLists.Bindings.Where(binding => binding != "environment.systemPackages").ToList(),
        Targets.System => PackageLists.Bindings.Where(binding => binding != "home.packages").ToList(),
        // scopeBindings[scope] is undefined, so addPackage falls back to its default.
        _ => PackageLists.Bindings,
    };

    /// <summary>The list a package goes into when a module has no package list yet (<c>packagesOption</c>).</summary>
    /// <exception cref="ManagerException">The scope is neither <c>home</c> nor <c>system</c>.</exception>
    public static string PackagesOption(string scope) => scope switch
    {
        Targets.Home => "home.packages",
        Targets.System => "environment.systemPackages",
        _ => throw new ManagerException($"Unknown scope {scope}."),
    };

    /// <summary>The path an importer writes for a file, such as <c>./fzf.nix</c> or <c>../programs/fzf.nix</c> (<c>importPath</c>).</summary>
    public static string ImportPath(string importer, string file)
    {
        var path = PosixPath.Relative(PosixPath.Dirname(importer), file);
        return path.StartsWith("../", StringComparison.Ordinal) ? path : $"./{path}";
    }

    /// <summary>Resolves a flake-relative <c>.nix</c> path, refusing anything outside the flake (<c>sourcePath</c>).</summary>
    public static string SourcePath(string flake, string file)
    {
        var clean = PosixPath.Normalize(file);
        if (PosixPath.IsAbsolute(clean) || clean.StartsWith("..", StringComparison.Ordinal) || !clean.EndsWith(".nix", StringComparison.Ordinal) || clean != file)
            throw new ManagerException($"Invalid file: {file}");
        var path = PosixPath.Join(flake, clean);
        // existsSync follows links: a dangling one does not exist.
        if (Native.RealPath(path) is { } target)
        {
            var real = PosixPath.Relative(Native.RealPath(flake) ?? flake, target);
            if (real.StartsWith("..", StringComparison.Ordinal) || PosixPath.IsAbsolute(real))
                throw new ManagerException($"{file} is outside the flake.");
        }
        return path;
    }

    // The edit of one change other than create-module (`edit`).
    private static SourceEdit Edit(string source, Change change)
    {
        switch (change)
        {
            case AddPackage add:
                try
                {
                    return PackageLists.AddPackage(source, add.Attr, ScopeBindings(add.Scope));
                }
                catch (ManagerException error) when (error.Message.StartsWith("No editable package list", StringComparison.Ordinal))
                {
                    return ModuleOptions.StartList(source, PackagesOption(add.Scope), add.Attr);
                }
            case RemovePackage remove:
                return PackageLists.RemovePackage(source, remove.Attr);
            case SetOption set:
                return ModuleOptions.SetOption(source, set.Option, set.Value?.DeepClone());
            case UnsetOption unset:
                return ModuleOptions.UnsetOption(source, unset.Option);
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    /// <summary>Line diff by longest common subsequence, grouped into hunks with context (<c>diffLines</c>).</summary>
    public static List<Hunk> DiffLines(string before, string after, int context = 2)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(1 + tail)] == b[^(1 + tail)]) tail++;
        var x = a[head..(a.Length - tail)];
        var y = b[head..(b.Length - tail)];
        var table = new uint[x.Length + 1, y.Length + 1];
        for (var i = x.Length - 1; i >= 0; i--)
            for (var j = y.Length - 1; j >= 0; j--)
                table[i, j] = x[i] == y[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        var lines = new List<(string Kind, string Text, int Old)>();
        for (var k = 0; k < head; k++) lines.Add((" ", a[k], k));
        {
            var i = 0;
            var j = 0;
            while (i < x.Length || j < y.Length)
            {
                if (i < x.Length && j < y.Length && x[i] == y[j])
                {
                    lines.Add((" ", x[i], head + i));
                    i++;
                    j++;
                }
                else if (i < x.Length && (j == y.Length || table[i + 1, j] >= table[i, j + 1]))
                {
                    lines.Add(("-", x[i], head + i));
                    i++;
                }
                else
                {
                    lines.Add(("+", y[j], head + i));
                    j++;
                }
            }
        }
        for (var k = a.Length - tail; k < a.Length; k++) lines.Add((" ", a[k], k));
        var ranges = new List<(int Start, int End)>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Kind == " ") continue;
            var start = Math.Max(0, index - context);
            var end = Math.Min(lines.Count, index + context + 1);
            if (ranges.Count > 0 && start <= ranges[^1].End) ranges[^1] = (ranges[^1].Start, end);
            else ranges.Add((start, end));
        }
        return ranges.Select(range =>
        {
            var slice = lines[range.Start..range.End];
            return new Hunk { Line = slice[0].Old + 1, Lines = slice.Select(line => new DiffLine(line.Kind, line.Text)).ToList() };
        }).ToList();
    }

    [GeneratedRegex(@"^[A-Za-z0-9._+/-]+\z")]
    private static partial Regex WritablePath();

    /// <summary>
    /// Applies the changes to the checkout's files in memory (<c>renderDraft</c>). A change that cannot be applied
    /// is reported and skipped; the others still apply.
    /// </summary>
    public static RenderResult Render(string flake, IReadOnlyList<Change> changes)
    {
        var files = new OrderedDictionary<string, EditedFile>(StringComparer.Ordinal);
        var problems = new List<DraftProblem>();
        EditedFile Open(string name)
        {
            if (files.TryGetValue(name, out var known)) return known;
            var path = SourcePath(flake, name);
            string before;
            try
            {
                // readFile(path, "utf8") keeps a byte order mark and replaces invalid bytes.
                before = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ManagerException($"{name} does not exist.");
            }
            return new EditedFile { Path = name, BaseHash = Repository.HashText(before), Before = before, After = before };
        }
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            try
            {
                if (change is CreateModule create)
                {
                    if (!WritablePath().IsMatch(change.File)) throw new ManagerException($"{change.File} cannot be written as a Nix path.");
                    if (files.ContainsKey(change.File) || Native.RealPath(SourcePath(flake, change.File)) is not null)
                        throw new ManagerException($"{change.File} already exists.");
                    if (create.Importer == change.File) throw new ManagerException("A file cannot import itself.");
                    var importer = Open(create.Importer);
                    importer.After = ModuleOptions.AddImport(importer.After, ImportPath(create.Importer, change.File)).Source;
                    files[create.Importer] = importer;
                    files[change.File] = new EditedFile { Path = change.File, BaseHash = "", Created = true, Before = "", After = ModuleTemplate };
                    continue;
                }
                var file = Open(change.File);
                file.After = Edit(file.After, change).Source;
                files[change.File] = file;
            }
            catch (Exception error)
            {
                problems.Add(new DraftProblem { Index = index, Message = error.Message });
            }
        }
        foreach (var file in files.Values) file.Hunks = DiffLines(file.Before, file.After);
        return new RenderResult(files.Values.Where(file => file.Created == true || file.Before != file.After).ToList(), problems);
    }

    /// <summary>The draft as the dashboard and <c>software pending --json</c> show it (<c>draftPublic</c>).</summary>
    public static RenderedDraft Public(string flake, IReadOnlyList<Change> changes)
    {
        var (files, problems) = Render(flake, changes);
        return new RenderedDraft
        {
            Changes = [.. changes],
            Files = files.Select(file => file.ToDraftFile()).ToList(),
            Problems = problems,
            Hash = Change.Hash(changes),
        };
    }

    private static string DraftFile(Config config) => Path.Combine(config.StateDir, "draft.json");

    /// <summary>The pending edits of the configured checkout; none when the draft is missing, unreadable or another checkout's (<c>readDraft</c>).</summary>
    public static List<Change> Read(Config config)
    {
        try
        {
            // A byte order mark makes the draft unreadable; ReadAllText would drop it.
            var saved = JsonNode.Parse(Encoding.UTF8.GetString(File.ReadAllBytes(DraftFile(config))));
            if (saved is not JsonObject members || members["flake"] is not JsonValue flake ||
                !flake.TryGetValue<string>(out var path) || path != config.Flake)
                return [];
            return Change.DecodeList(members["changes"]);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Saves the pending edits atomically, readable only by the user (<c>writeDraft</c>).</summary>
    public static void Write(Config config, IReadOnlyList<Change> changes)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(config.StateDir);
        else Directory.CreateDirectory(config.StateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = DraftFile(config);
        var temporary = $"{path}.{Guid.NewGuid()}.tmp";
        try
        {
            var draft = new JsonObject
            {
                ["flake"] = config.Flake,
                ["changes"] = Change.ToJson(changes),
            };
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                var bytes = new UTF8Encoding(false).GetBytes(JsJson.Indented(draft) + "\n");
                stream.Write(bytes);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

/// <summary>Node's <c>path.posix</c> functions the draft uses, with their exact results.</summary>
internal static class PosixPath
{
    public static bool IsAbsolute(string path) => path.StartsWith('/');

    // `normalizeString`: resolves `.` and `..` segments.
    private static string Segments(string path, bool allowAboveRoot)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
                else if (allowAboveRoot) parts.Add("..");
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    /// <summary><c>path.posix.normalize</c>.</summary>
    public static string Normalize(string path)
    {
        if (path.Length == 0) return ".";
        var absolute = IsAbsolute(path);
        var trailing = path.EndsWith('/');
        var result = Segments(path, !absolute);
        if (result.Length == 0)
        {
            if (absolute) return "/";
            return trailing ? "./" : ".";
        }
        if (trailing) result += "/";
        return absolute ? $"/{result}" : result;
    }

    /// <summary><c>path.posix.join</c> of two non-empty paths.</summary>
    public static string Join(string first, string second) =>
        first.Length == 0 ? Normalize(second) : second.Length == 0 ? Normalize(first) : Normalize($"{first}/{second}");

    /// <summary><c>path.posix.resolve</c> of one path, against the working directory.</summary>
    public static string Resolve(string path)
    {
        var full = IsAbsolute(path) ? path : $"{Directory.GetCurrentDirectory()}/{path}";
        return $"/{Segments(full, false)}";
    }

    /// <summary><c>path.posix.relative</c>.</summary>
    public static string Relative(string from, string to)
    {
        var a = Resolve(from);
        var b = Resolve(to);
        if (a == b) return "";
        var x = a == "/" ? [] : a[1..].Split('/');
        var y = b == "/" ? [] : b[1..].Split('/');
        var common = 0;
        while (common < x.Length && common < y.Length && x[common] == y[common]) common++;
        return string.Join('/', Enumerable.Repeat("..", x.Length - common).Concat(y[common..]));
    }

    /// <summary><c>path.posix.dirname</c>.</summary>
    public static string Dirname(string path)
    {
        if (path.Length == 0) return ".";
        var end = path.Length;
        while (end > 1 && path[end - 1] == '/') end--;
        var slash = path.LastIndexOf('/', end - 1);
        if (slash < 0) return ".";
        if (slash == 0) return "/";
        return path[..slash];
    }
}
