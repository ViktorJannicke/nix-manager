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
/// <param name="Revision">The commit the checkout was at, if any.</param>
public sealed record ReviewBase(string Fingerprint, string Lock, string? Revision = null);

/// <summary>A review that can no longer be applied as it was prepared, and why.</summary>
/// <param name="SupersededBy">The newer review of the same kind that replaces it, when that is the reason.</param>
public sealed record ObsoleteReview(BuildReview Review, DisplayText Reason, string? SupersededBy = null);

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
        if (!RequestValidation.ValidId(id)) throw new ManagerException(DisplayText.Of("stored_invalid_review"));
        try
        {
            return ManagerJson.Deserialize<BuildReview>(File.ReadAllText(ReviewFile(config, id))) ?? throw new JsonException("null");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ManagerException(DisplayText.Of("stored_the_selected_review_is_unavailable"));
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
        var checkout = await Repository.StateAsync(config.Flake, cancellationToken).ConfigureAwait(false);
        kept.Fingerprint = checkout.Fingerprint;
        kept.Revision = checkout.Revision;
        if (kept.Inputs is { } inputs)
        {
            inputs.BaseLockHash = Repository.HashText(Repository.ReadLock(config.Flake));
            inputs.Pins = null;
        }
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
        if (!RequestValidation.ValidId(id)) throw new ManagerException(DisplayText.Of("stored_invalid_review"));
        var root = Paths.ReviewRoot(config, id);
        // GC roots are links: they are removed, never followed.
        if (Directory.Exists(root) || File.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// Whether the checkout changed since a review was built from it: its fingerprint, or its commit when the review
    /// recorded one. A review of a retained generation (going back, or undoing) was not built from the checkout.
    /// </summary>
    public static bool CheckoutChanged(BuildReview review, RepositoryState checkout) =>
        review.Source != ReviewSources.Generation
        && (review.Fingerprint != checkout.Fingerprint || review.Revision is { } revision && revision != checkout.Revision);

    /// <summary>Whether flake.lock changed since an input update was resolved from it.</summary>
    public static bool LockChanged(BuildReview review, RepositoryState checkout) =>
        review.Inputs is { } inputs && inputs.BaseLockHash != checkout.LockHash;

    /// <summary>Refuses to apply a review whose builds no longer match the checkout, as they would undo newer changes.</summary>
    /// <exception cref="ManagerException">flake.lock or the checkout changed since the review was prepared.</exception>
    public static void EnsureCurrent(BuildReview review, RepositoryState checkout)
    {
        if (LockChanged(review, checkout)) throw new ManagerException(DisplayText.Of("stored_flake_lock_changed_after_review"));
        if (CheckoutChanged(review, checkout)) throw new ManagerException(DisplayText.Of("stored_checkout_changed_after_review"));
    }

    private static bool Activates(string action) => action is Actions.Switch or Actions.Boot or Actions.Rollback or Actions.ReviewApply;

    /// <summary>
    /// The reviews that can no longer be applied as they were prepared, and why:
    /// <list type="bullet">
    /// <item>built from the checkout, and flake.lock or the checkout changed since, so applying is refused;</item>
    /// <item>going back to a generation or undoing a change, and another change was applied since, so it would go back
    /// further than it shows;</item>
    /// <item>replaced by a newer review of the same kind (checkout, input update, generation or undo), host and scope
    /// that is not obsolete itself.</item>
    /// </list>
    /// A review a running operation uses is kept. So is a review kept for the next boot, unless a newer one of the
    /// same host waits instead: only the system applied last starts by default.
    /// </summary>
    /// <param name="history">The operations, as <see cref="OperationStore.History"/> lists them.</param>
    public static IReadOnlyList<ObsoleteReview> Obsolete(IReadOnlyList<BuildReview> reviews, RepositoryState checkout, IReadOnlyList<Operation> history)
    {
        var running = history.Where(op => op.Status == OperationStatuses.Running).Select(op => op.Request.ReviewId).OfType<string>().ToHashSet();
        var lastApplied = history.Where(op => op.Status == OperationStatuses.Succeeded && !op.Request.DryRun && Activates(op.Request.Action))
            .Select(op => op.FinishedAt ?? op.StartedAt).Max(StringComparer.Ordinal);
        DisplayText? Stale(BuildReview review) =>
            review.AfterBoot is not null ? null
            : LockChanged(review, checkout) ? DisplayText.Of("stored_obsolete_review_lock_changed")
            : CheckoutChanged(review, checkout) ? DisplayText.Of("stored_obsolete_review_checkout_changed")
            : review.Source == ReviewSources.Generation && string.CompareOrdinal(lastApplied, review.CreatedAt) > 0
                ? DisplayText.Of("stored_obsolete_review_applied_since")
                : null;
        static string Kind(BuildReview review) => review.AfterBoot is not null
            ? $"after-boot\0{review.Host}"
            : $"{(review.Undo is not null ? "undo" : review.Source)}\0{review.Host}\0{review.Scope}";
        var reasons = reviews.Select(Stale).ToList();
        var obsolete = new List<ObsoleteReview>();
        for (var index = 0; index < reviews.Count; index++)
        {
            var review = reviews[index];
            if (running.Contains(review.Id)) continue;
            if (reasons[index] is { } reason)
            {
                obsolete.Add(new ObsoleteReview(review, reason));
                continue;
            }
            var newer = reviews.Where((other, at) => reasons[at] is null && other.Id != review.Id && Kind(other) == Kind(review)
                    && string.CompareOrdinal(other.CreatedAt, review.CreatedAt) > 0)
                .MaxBy(other => other.CreatedAt, StringComparer.Ordinal);
            if (newer is not null) obsolete.Add(new ObsoleteReview(review, DisplayText.Of("stored_obsolete_review_superseded"), newer.Id));
        }
        return obsolete;
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
        TimeProvider? clock = null, string currentSystem = CurrentSystem, IReadOnlyList<ReviewPin>? pins = null)
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
                Label = DisplayText.Of("stored_measure_closure_sizes"),
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
                    Label = DisplayText.Of("stored_compare_package_versions"),
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
        if (home is null && system is null) throw new ManagerException(DisplayText.Of("stored_build_produced_no_output_to_review"));
        var proposed = Path.Combine(root, "flake.lock");
        var proposal = File.Exists(proposed) ? File.ReadAllText(proposed) : null;
        var review = new BuildReview
        {
            Id = id,
            Host = r.Host is { Length: > 0 } ? r.Host : config.HostKey,
            Scope = r.Scope,
            CreatedAt = Timestamps.Now(clock),
            Fingerprint = @base.Fingerprint,
            Revision = @base.Revision,
            // An empty proposal counts as none, as it does in JavaScript.
            Source = r.Generation is not null || undo is not null ? ReviewSources.Generation : proposal is { Length: > 0 } ? ReviewSources.Inputs : ReviewSources.Checkout,
            Generation = r.Generation,
            Undo = undo,
            Edits = edits,
            Inputs = proposal is { Length: > 0 }
                ? new ReviewInputs
                {
                    Names = [.. r.Inputs ?? []], Changes = NixOutputs.LockChanges(@base.Lock, proposal), BaseLockHash = Repository.HashText(@base.Lock),
                    Pins = pins is { Count: > 0 } ? [.. pins] : null,
                }
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
            throw new ManagerException(DisplayText.Of("stored_step_failed_because", ("step", DisplayText.Of("stored_save_review")), ("reason", FileErrors.Describe(e))));
        }
        return review;
    }

    /// <summary>
    /// Copies the reviewed lock, and the pins revalidated against it, into the checkout unless flake.lock or a pin
    /// changed since (<c>writeReviewedLock</c>).
    /// </summary>
    public static void WriteReviewedLock(Config config, BuildReview review)
    {
        if (review.Inputs is null) return;
        WriteLock(config, Path.Combine(Paths.ReviewRoot(config, review.Id), "flake.lock"), review.Inputs.Pins ?? [], review.Inputs.BaseLockHash);
    }

    /// <summary>
    /// Writes the lock <paramref name="proposed"/> and the <paramref name="pins"/> revalidated against it into the
    /// checkout. Every file is checked first: when flake.lock no longer has <paramref name="baseLockHash"/> or a pin
    /// changed since it was revalidated, nothing is written.
    /// </summary>
    /// <param name="baseLockHash">The checkout lock's hash the proposal was resolved from.</param>
    public static void WriteLock(Config config, string proposed, IReadOnlyList<ReviewPin> pins, string baseLockHash)
    {
        try
        {
            var target = Path.Combine(config.Flake, "flake.lock");
            if (Repository.HashText(Repository.ReadLock(config.Flake)) != baseLockHash)
                throw new ManagerException(DisplayText.Of("stored_flake_lock_changed_after_review"));
            foreach (var pin in pins)
            {
                var path = Path.Combine(config.Flake, pin.Path);
                if (!Updates.PinRevalidation.IsPinPath(pin.Path) || !File.Exists(path) || Repository.HashText(File.ReadAllText(path)) != pin.BaseHash)
                    throw new ManagerException(DisplayText.Of("stored_changed_after_its_pin_was_revalidated", ("path", pin.Path)));
            }
            var proposal = File.ReadAllBytes(proposed);
            foreach (var pin in pins)
            {
                var path = Path.Combine(config.Flake, pin.Path);
                AtomicWrite(path, pin.Text, ModeOf(path));
            }
            AtomicWrite(target, proposal, ModeOf(target));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ManagerException(DisplayText.Of("stored_step_failed_because", ("step", DisplayText.Of("stored_write_flake_lock")), ("reason", FileErrors.Describe(e))));
        }
    }
}
