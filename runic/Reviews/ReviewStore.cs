using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Observation;
using NixManager.Operations;

namespace NixManager.Reviews;

/// <summary>Snapshot of the checkout taken before a review build evaluates it (<c>ReviewBase</c>).</summary>
public sealed record ReviewBase(string Fingerprint, string Lock);

/// <summary>
/// The prepared reviews in <c>reviews/ID/</c> of the state directory: their records, their GC roots and the
/// comparison that writes them. Every manager process reads and writes the same files.
/// </summary>
public static class ReviewStore
{
    /// <summary>Where the system the computer runs is linked; the comparison reads it as the current system.</summary>
    public const string CurrentSystem = "/run/current-system";

    private const int PrivateMode = 0b110_000_000; // 0o600
    /// <summary>The mode of a file the manager creates in the checkout: 0o644.</summary>
    public const int NewFileMode = 0b110_100_100;
    private const int PermissionBits = 0b111_111_111; // 0o777

    private static string ReviewFile(Config config, string id) => Path.Combine(Paths.ReviewRoot(config, id), "review.json");

    /// <summary>A stored review (<c>getReview</c>).</summary>
    /// <exception cref="ManagerException">The ID is invalid, or the review is unavailable.</exception>
    public static BuildReview Get(Config config, string id)
    {
        if (!RequestValidation.ValidId(id)) throw new ManagerException("Invalid review.");
        try
        {
            return ManagerJson.Deserialize<BuildReview>(File.ReadAllText(ReviewFile(config, id))) ?? throw new JsonException("null");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ManagerException("The selected review is unavailable.");
        }
    }

    /// <summary>Every readable review, newest first (<c>listReviews</c>).</summary>
    public static IReadOnlyList<BuildReview> List(Config config)
    {
        var root = Path.Combine(config.StateDir, "reviews");
        IEnumerable<string> ids;
        try
        {
            ids = Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ids = [];
        }
        var found = new List<BuildReview>();
        foreach (var id in ids)
        {
            try
            {
                if (ManagerJson.Deserialize<BuildReview>(File.ReadAllText(Path.Combine(root, id, "review.json"))) is { Id.Length: > 0 } review)
                    found.Add(review);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
            {
                // An unreadable review is not listed.
            }
        }
        return found.OrderByDescending(review => review.CreatedAt, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// After the system part of a review was applied on next boot, keeps the review for its home part, which is
    /// applied once the computer has started into that system (<c>keepForBoot</c>). Edits and the lock are written
    /// already, so the kept review neither writes them again nor counts them as changed since.
    /// </summary>
    public static async Task KeepForBootAsync(Config config, BuildReview review, CancellationToken cancellationToken = default)
    {
        var kept = review.Clone();
        kept.System = null;
        kept.Edits = null;
        kept.Undo = null;
        kept.Scope = Scopes.Home;
        kept.Fingerprint = (await Repository.StateAsync(config.Flake, cancellationToken).ConfigureAwait(false)).Fingerprint;
        if (kept.Inputs is { } inputs) inputs.BaseLockHash = Repository.HashText(Repository.ReadLock(config.Flake));
        kept.AfterBoot = new AfterBoot { System = review.System!.Path };
        AtomicWrite(ReviewFile(config, review.Id), Stringify(kept, indented: false));
    }

    /// <summary>Turns a review kept for the next boot back into an ordinary one, so it is applied by itself only once (<c>forgetBoot</c>).</summary>
    public static void ForgetBoot(Config config, BuildReview review)
    {
        var rest = review.Clone();
        rest.AfterBoot = null;
        AtomicWrite(ReviewFile(config, review.Id), Stringify(rest, indented: false));
    }

    /// <summary>Removes a review and its GC roots; the outputs become collectable (<c>removeReview</c>).</summary>
    public static void Remove(Config config, string id)
    {
        if (!RequestValidation.ValidId(id)) throw new ManagerException("Invalid review.");
        var root = Paths.ReviewRoot(config, id);
        // GC roots are links: they are removed, never followed.
        if (Directory.Exists(root) || File.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>A review as JSON, compact or with two-space indentation.</summary>
    public static string Stringify(BuildReview review, bool indented)
    {
        var node = JsonSerializer.SerializeToNode(review, ManagerJson.Default.BuildReview);
        return indented ? JsJson.Indented(node) : JsJson.Stringify(node);
    }

    /// <summary>
    /// Writes a file by renaming a temporary file with <paramref name="mode"/> over it (<c>atomicWrite</c>); the
    /// mode is set explicitly, so the umask does not narrow it.
    /// </summary>
    public static void AtomicWrite(string path, ReadOnlySpan<byte> data, int mode = PrivateMode)
    {
        var temporary = $"{path}.{Guid.NewGuid():D}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = (UnixFileMode)mode;
            using (var stream = new FileStream(temporary, options)) stream.Write(data);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, (UnixFileMode)mode);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void AtomicWrite(string path, string data, int mode = PrivateMode) => AtomicWrite(path, Encoding.UTF8.GetBytes(data), mode);

    /// <summary>The permission bits of an existing file, or 0o644 for a new one.</summary>
    public static int ModeOf(string path) =>
        File.Exists(path) && !OperatingSystem.IsWindows() ? (int)File.GetUnixFileMode(path) & PermissionBits : NewFileMode;

    private static string? RealPath(string path) => Native.RealPath(path);

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadLink(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static long? ClosureSize(JsonNode? entry) =>
        entry?["closureSize"] is JsonValue value && value.TryGetValue<double>(out var size) ? (long)size : null;

    /// <summary>
    /// Records what a finished review build would change: package versions, closure sizes, NixOS version and
    /// kernel transitions, and input changes. Writes <c>review.json</c> as indented JSON, with two spaces
    /// and a final line feed.
    /// </summary>
    /// <param name="run">Runs quiet commands.</param>
    /// <param name="currentSystem">The link to the running system, <see cref="CurrentSystem"/> outside tests.</param>
    public static async Task<BuildReview> CompareAsync(Config config, Request r, ReviewBase @base, string operationId,
        Func<Command, Task<CommandResult>> run, ReviewEdits? edits = null, ReviewUndo? undo = null, long? downloaded = null,
        TimeProvider? clock = null, string currentSystem = CurrentSystem)
    {
        var id = r.ReviewId!;
        var root = Paths.ReviewRoot(config, id);

        async Task<ReviewOutput?> Inspect(string scope, string? current)
        {
            var path = RealPath(Path.Combine(root, scope));
            if (path is null) return null;
            var output = new ReviewOutput { Path = path, Changes = [], Compared = false };
            var sizes = await run(new Command
            {
                Label = "Measure closure sizes",
                Argv = ["nix", "path-info", "--json", "--closure-size", path, .. current is null ? Array.Empty<string>() : [current]],
                Quiet = true,
                AllowFailure = true,
            }).ConfigureAwait(false);
            try
            {
                var parsed = JsonNode.Parse(sizes.Stdout);
                var byPath = parsed is JsonArray entries
                    ? entries.Where(entry => entry?["path"] is JsonValue).GroupBy(entry => entry!["path"]!.GetValue<string>())
                        .ToDictionary(group => group.Key, group => group.Last())
                    : parsed is JsonObject members ? members.ToDictionary(pair => pair.Key, pair => pair.Value) : throw new JsonException("no sizes");
                output.ClosureSize = ClosureSize(byPath.GetValueOrDefault(path));
                if (current is not null) output.CurrentClosureSize = ClosureSize(byPath.GetValueOrDefault(current));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                // Sizes are informative only.
            }
            if (current is not null && current != path)
            {
                var diff = await run(new Command
                {
                    Label = "Compare package versions",
                    Argv = ["nix", "store", "diff-closures", current, path],
                    Quiet = true,
                    AllowFailure = true,
                }).ConfigureAwait(false);
                if (diff.Code == 0)
                {
                    output.Changes = NixOutputs.ParseClosureDiff(diff.Stdout);
                    output.Compared = true;
                }
            }
            else if (current is not null) output.Compared = true;
            if (scope == Scopes.System)
            {
                static (string? Version, string? Kernel) Meta(string? link) => link is null
                    ? (null, null)
                    : (ReadText(Path.Combine(link, "nixos-version"))?.Trim(), NixOutputs.KernelVersion(ReadLink(Path.Combine(link, "kernel"))));
                var before = Meta(current);
                var after = Meta(path);
                output.Version = new VersionPair { Before = before.Version, After = after.Version };
                output.Kernel = new VersionPair { Before = before.Kernel, After = after.Kernel };
            }
            return output;
        }

        var currentHome = RealPath(config.HomeProfile);
        var currentSystemPath = RealPath(currentSystem);
        var home = r.Scope != Scopes.System ? await Inspect(Scopes.Home, currentHome).ConfigureAwait(false) : null;
        var system = r.Scope != Scopes.Home ? await Inspect(Scopes.System, currentSystemPath).ConfigureAwait(false) : null;
        if (home is null && system is null) throw new ManagerException("The build did not produce any output to review.");
        var proposed = Path.Combine(root, "flake.lock");
        var proposal = File.Exists(proposed) ? File.ReadAllText(proposed) : null;
        var review = new BuildReview
        {
            Id = id,
            Host = r.Host is { Length: > 0 } ? r.Host : config.HostKey,
            Scope = r.Scope,
            CreatedAt = Timestamps.Now(clock),
            Fingerprint = @base.Fingerprint,
            // An empty proposal counts as none, as it does in JavaScript.
            Source = r.Generation is not null || undo is not null ? ReviewSources.Generation : proposal is { Length: > 0 } ? ReviewSources.Inputs : ReviewSources.Checkout,
            Generation = r.Generation,
            Undo = undo,
            Edits = edits,
            Inputs = proposal is { Length: > 0 }
                ? new ReviewInputs { Names = [.. r.Inputs ?? []], Changes = NixOutputs.LockChanges(@base.Lock, proposal), BaseLockHash = Repository.HashText(@base.Lock) }
                : null,
            Home = home,
            System = system,
            OperationId = operationId,
            Downloaded = downloaded is > 0 ? downloaded : null,
        };
        try
        {
            AtomicWrite(ReviewFile(config, id), Stringify(review, indented: true) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ManagerException($"Save review: {e.Message}");
        }
        return review;
    }

    /// <summary>Copies the reviewed lock into the checkout unless flake.lock changed since (<c>writeReviewedLock</c>).</summary>
    public static void WriteReviewedLock(Config config, BuildReview review)
    {
        if (review.Inputs is null) return;
        try
        {
            var target = Path.Combine(config.Flake, "flake.lock");
            if (Repository.HashText(Repository.ReadLock(config.Flake)) != review.Inputs.BaseLockHash)
                throw new ManagerException("flake.lock changed after this review was prepared. Prepare the update again.");
            var proposal = File.ReadAllBytes(Path.Combine(Paths.ReviewRoot(config, review.Id), "flake.lock"));
            AtomicWrite(target, proposal, ModeOf(target));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ManagerException($"Write flake.lock: {e.Message}");
        }
    }
}
