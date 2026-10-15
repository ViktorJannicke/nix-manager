using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Nix;
using NixManager.Operations;
using Runic.CommandLine.Processes;

namespace NixManager.Software;

/// <summary>A searchable package of the locked nixpkgs (<c>CatalogEntry</c>), with the Bun version's short member names.</summary>
public record CatalogEntry
{
    /// <summary>Attribute path below <c>pkgs</c>.</summary>
    [JsonPropertyName("a")] public required string A { get; init; }
    [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? N { get; init; }
    [JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? V { get; init; }
    [JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? D { get; init; }
    [JsonPropertyName("h"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? H { get; init; }
    /// <summary>Main program.</summary>
    [JsonPropertyName("p"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? P { get; init; }
    [JsonPropertyName("l")] public IReadOnlyList<string> L { get; init; } = [];
    /// <summary>Free license.</summary>
    [JsonPropertyName("f")] public bool F { get; init; }
    /// <summary>Marked as broken.</summary>
    [JsonPropertyName("b")] public bool B { get; init; }
    /// <summary>Built like a graphical app: with GTK or Qt wrapping or desktop entries.</summary>
    [JsonPropertyName("g")] public bool? G { get; init; }
}

/// <summary>What Flathub says about an app (<c>AppInfo</c> in <c>src/apps.ts</c>).</summary>
public sealed record AppInfo
{
    /// <summary>AppStream id, such as <c>org.gimp.GIMP</c>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Summary { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Icon { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Homepage { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
}

/// <summary>Search restrictions: only free licenses, only packages not marked broken (<c>CatalogFilter</c>).</summary>
public sealed record CatalogFilter(bool Free = false, bool Working = false);

/// <summary>The state of the package catalog for the checkout's lock file (<c>CatalogStatus</c>).</summary>
public sealed record CatalogStatus
{
    /// <summary><c>missing</c>, <c>building</c>, <c>ready</c> or <c>failed</c>.</summary>
    public required string State { get; init; }
    public int? Count { get; init; }
    public string? BuiltAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>The package catalog of a configuration's <c>pkgs</c> (<c>src/software.ts</c>).</summary>
public static partial class Catalog
{
    /// <summary>Raised when catalog entries gain fields, so older files are built again (<c>catalogVersion</c>).</summary>
    public const int Version = 2;

    /// <summary>Where the catalog of a lock file is kept (<c>catalogPath</c>).</summary>
    public static string PathFor(Config config, string lockHash) =>
        Path.Combine(config.StateDir, "catalog", $"{lockHash[..Math.Min(32, lockHash.Length)]}.v{Version}.json");

    /// <summary>Walks <c>pkgs</c> (and package sets that ask to be recursed into) for searchable metadata (<c>catalogExpression</c>).</summary>
    public const string Expression = """
        pkgs:
        let
          inherit (builtins) tryEval isAttrs attrNames concatMap;
          str = v: if builtins.isString v then v else null;
          entry = path: v:
            let
              m = v.meta or { };
              license = m.license or null;
              licenses = if builtins.isList license then license else if license == null then [ ] else [ license ];
            in {
              a = builtins.concatStringsSep "." path;
              n = str (v.pname or null);
              v = str (v.version or null);
              d = str (m.description or null);
              h = let x = m.homepage or null; in if builtins.isList x then str (builtins.head x) else str x;
              p = str (m.mainProgram or null);
              l = map (x: if isAttrs x then (x.spdxId or x.shortName or "unknown") else toString x) licenses;
              f = builtins.all (x: !isAttrs x || (x.free or true)) licenses;
              b = m.broken or false;
              g = let
                hooks = [ "wrap-gapps-hook" "wrap-gapps-hook3" "wrap-gapps-hook4" "wrap-qt5-apps-hook" "wrap-qt6-apps-hook" "copy-desktop-items-hook" "desktop-file-utils" ];
                hook = x: isAttrs x && builtins.elem (x.pname or x.name or "") hooks;
              in (v ? desktopItems) || (v ? desktopItem) || builtins.any hook (v.nativeBuildInputs or [ ]);
            };
          walk = depth: path: set:
            concatMap (name:
              let r = tryEval set.${name}; v = r.value; in
              if !r.success then [ ]
              else if isAttrs v && (v.type or null) == "derivation" then
                let e = tryEval (let x = entry (path ++ [ name ]) v; in builtins.deepSeq x x); in if e.success then [ e.value ] else [ ]
              else if depth < 2 && isAttrs v && (v.recurseForDerivations or false) then walk (depth + 1) (path ++ [ name ]) v
              else [ ]
            ) (attrNames set);
        in walk 0 [ ] pkgs
        """;

    /// <summary>
    /// Writes the package catalog of a configuration's <c>pkgs</c>, including overlays (<c>buildCatalog</c>). The
    /// evaluation visits about a hundred thousand packages; its output goes straight to a file instead of the
    /// bounded command capture, and catalogs of earlier lock files or versions are removed.
    /// </summary>
    /// <param name="program">The evaluator; tests replace <c>nix</c> with a script.</param>
    public static async Task BuildAsync(Config config, string host, string target, IProcessRunner? runner = null,
        CancellationToken cancellationToken = default, string program = "nix")
    {
        var directory = Path.GetDirectoryName(target)!;
        var temporary = $"{target}.{Guid.NewGuid()}.tmp";
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ProcessResult result;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var options = new ProcessExecutionOptions(standardOutputLimitBytes: 0, standardErrorLimitBytes: 64 * 1024)
                {
                    StandardInput = ProcessStandardInput.Closed,
                    DetachFromTerminal = true,
                };
                var request = new ProcessRequest(program,
                    ["eval", "--json", $"{config.Flake}#{Evaluation.ConfigurationAttr(config, host, "home")}.pkgs", "--apply", Expression],
                    null, null, options);
                result = await (runner ?? new ProcessRunner(new LocalExecutablePolicy()))
                    .RunAsync(request, new ToFile(file), cancellationToken).ConfigureAwait(false);
            }
            if (result.State == ProcessState.Cancelled) throw new OperationCanceledException(cancellationToken);
            if (result.State != ProcessState.Exited)
                throw new ManagerException($"Build package catalog: Error: {result.Fault?.Message ?? result.State.ToString()}");
            if (result.ExitCode != 0)
            {
                var stderr = result.StandardError.Text;
                throw new ManagerException($"Package catalog evaluation failed. {(stderr.Length > 1200 ? stderr[^1200..] : stderr).Trim()}");
            }
            File.Move(temporary, target, overwrite: true);
            // Catalogs of earlier lock files or versions are not read again.
            foreach (var name in Directory.EnumerateFiles(directory, "*.json"))
                if (name != target) File.Delete(name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ManagerException($"Build package catalog: Error: {error.Message}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Copies standard output to the catalog file as it arrives.</summary>
    private sealed class ToFile(FileStream file) : IProcessOutputHandler
    {
        public async ValueTask OnChunkAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
        {
            if (chunk.Channel == ProcessOutputChannel.StandardOutput) await file.WriteAsync(chunk.Bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a catalog file.</summary>
    public static List<CatalogEntry> Read(string path) =>
        ManagerJson.Deserialize<List<CatalogEntry>>(File.ReadAllText(path)) ?? [];

    [GeneratedRegex("[^a-z0-9+]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    private static List<string> Words(string text) => NonWord().Split(text.ToLowerInvariant()).Where(word => word.Length > 0).ToList();

    /// <summary>
    /// Ranks catalog entries (<c>searchCatalog</c>): exact names first, then prefixes, then descriptions. Apps are
    /// also found by their shown name, summary and keywords, and come before other packages that match as well.
    /// </summary>
    public static List<CatalogEntry> Search(IReadOnlyList<CatalogEntry> entries, string query, int limit = 60, CatalogFilter? filter = null,
        IReadOnlyDictionary<string, AppInfo>? apps = null)
    {
        filter ??= new CatalogFilter();
        var terms = Words(query);
        if (terms.Count == 0) return [];
        // JavaScript's \b: a boundary between [A-Za-z0-9_] and anything else.
        var patterns = terms.Select(term => new Regex($@"\b{term.Replace("+", @"\+", StringComparison.Ordinal)}", RegexOptions.ECMAScript)).ToList();
        var scored = new List<(CatalogEntry Entry, double Score)>();
        foreach (var entry in entries)
        {
            if ((filter.Free && !entry.F) || (filter.Working && entry.B)) continue;
            var name = (entry.N ?? entry.A).ToLowerInvariant();
            var attribute = entry.A.ToLowerInvariant();
            var leaf = attribute[(attribute.LastIndexOf('.') + 1)..];
            var program = entry.P?.ToLowerInvariant();
            var app = apps?.GetValueOrDefault(entry.A);
            var shown = app?.Name.ToLowerInvariant();
            var description = $"{entry.D ?? ""} {(app is null ? "" : $"{app.Summary ?? ""} {string.Join(' ', app.Keywords)}")}".ToLowerInvariant();
            double score = 0;
            for (var index = 0; index < terms.Count; index++)
            {
                var term = terms[index];
                var best = 0;
                if (name == term || leaf == term || program == term || shown == term) best = 100;
                else if (name.StartsWith(term, StringComparison.Ordinal) || leaf.StartsWith(term, StringComparison.Ordinal)
                    || (shown is not null && Space().Split(shown).Any(word => word.StartsWith(term, StringComparison.Ordinal)))) best = 60;
                else if (attribute.Contains(term, StringComparison.Ordinal) || name.Contains(term, StringComparison.Ordinal)
                    || shown?.Contains(term, StringComparison.Ordinal) == true) best = 35;
                else if (program?.Contains(term, StringComparison.Ordinal) == true) best = 30;
                else if (patterns[index].IsMatch(description)) best = 12;
                if (best == 0)
                {
                    score = 0;
                    break;
                }
                score += best;
            }
            if (score == 0) continue;
            // Prefer top-level packages, working builds and shorter, canonical names.
            if (!entry.A.Contains('.')) score += 6;
            if (entry.B) score -= 40;
            if (entry.D is null or "") score -= 5;
            if (app is not null) score += 15;
            else if (entry.G == true) score += 5;
            score -= Math.Min(10, entry.A.Length / 6.0);
            scored.Add((entry, score));
        }
        return scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.A, Comparer<string>.Create(JsText.LocaleCompare))
            .Take(Math.Max(0, limit))
            .Select(item => item.Entry)
            .ToList();
    }
}
