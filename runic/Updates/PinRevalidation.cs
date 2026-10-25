using System.Text.Json;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Software;

namespace NixManager.Updates;

/// <summary>What an input update means for a package pin (<see cref="PinRevalidation.Status"/>).</summary>
public enum PinStatus
{
    /// <summary>The pin was validated against the new nixpkgs.</summary>
    Current,
    /// <summary>The new nixpkgs has the pinned version or a newer one, so the package falls back to nixpkgs' own.</summary>
    Superseded,
    /// <summary>The package still uses the pin, whose hashes were found under another nixpkgs recipe.</summary>
    Stale,
}

/// <summary>
/// Keeps the package pins of <see cref="PackageUpdater.Packages"/> valid when an input update moves the root nixpkgs
/// input: a pin inherits nixpkgs' recipe, so its fixed-output dependency hashes hold only for the nixpkgs it was
/// validated against (its <c>nixpkgsRev</c>). Stale pins are revalidated against the proposed lock with
/// <see cref="PackageUpdater.RevalidateAsync"/> and written together with that lock. A build that still fails on a
/// pinned dependency's hash is explained by <see cref="ExplainMismatch"/>.
/// </summary>
public static class PinRevalidation
{
    /// <summary>The flake-relative path of a package's pin.</summary>
    public static string PinPath(string name) => $"pkgs/{name}/source.json";

    /// <summary>The path is the pin of a package with an updater.</summary>
    public static bool IsPinPath(string path) => PackageUpdater.Packages.Keys.Any(name => PinPath(name) == path);

    /// <summary>The packages whose pins inherit a nixpkgs recipe, in the update check's order.</summary>
    public static IEnumerable<string> Inherited() => PackageUpdater.Packages.Where(pair => pair.Value.Recipes.Count > 0).Select(pair => pair.Key);

    /// <summary>
    /// The locked revision of the flake's own <c>nixpkgs</c> input in <paramref name="lockText"/>, or null when the lock
    /// has none. The input is found through the root node's inputs, following <c>follows</c> paths: the node named
    /// <c>nixpkgs</c> may belong to another input.
    /// </summary>
    public static string? RootNixpkgsRevision(string lockText)
    {
        try
        {
            using var document = JsonDocument.Parse(lockText);
            var lockFile = document.RootElement;
            if (lockFile.ValueKind != JsonValueKind.Object || !lockFile.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Object) return null;
            var root = lockFile.TryGetProperty("root", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "root";
            return Resolve(nodes, root, ["nixpkgs"], 0) is { } node && nodes.TryGetProperty(node, out var found) && found.ValueKind == JsonValueKind.Object
                   && found.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.Object
                   && locked.TryGetProperty("rev", out var rev) && rev.ValueKind == JsonValueKind.String
                ? rev.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The node an input path from the root leads to; a <c>follows</c> list is itself a path from the root.</summary>
    private static string? Resolve(JsonElement nodes, string root, IEnumerable<string> path, int depth)
    {
        // A lock that follows itself in a circle has no answer.
        if (depth > 32) return null;
        var node = root;
        foreach (var input in path)
        {
            if (!nodes.TryGetProperty(node, out var current) || current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object
                || !inputs.TryGetProperty(input, out var target))
                return null;
            var next = target.ValueKind switch
            {
                JsonValueKind.String => target.GetString(),
                JsonValueKind.Array when target.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) =>
                    Resolve(nodes, root, target.EnumerateArray().Select(item => item.GetString()!).ToList(), depth + 1),
                _ => null,
            };
            if (next is null) return null;
            node = next;
        }
        return node;
    }

    /// <summary>
    /// Whether <paramref name="pin"/> needs revalidating for nixpkgs <paramref name="revision"/>, whose own version of
    /// the package is <paramref name="nixpkgsVersion"/>. A version that cannot be compared counts as older, so the pin
    /// is revalidated rather than dropped.
    /// </summary>
    public static PinStatus Status(PackagePin pin, string revision, string nixpkgsVersion)
    {
        if (pin.NixpkgsRev == revision) return PinStatus.Current;
        try
        {
            return UpdateChecker.CompareVersions(nixpkgsVersion, pin.Version) >= 0 ? PinStatus.Superseded : PinStatus.Stale;
        }
        catch (ManagerException)
        {
            return PinStatus.Stale;
        }
    }

    /// <summary>
    /// Revalidates the checkout's stale pins against the proposed lock (<c>revalidatePins</c>). When the root nixpkgs
    /// input moves, each pin validated against another nixpkgs and still in use is rebuilt with
    /// <see cref="PackageUpdater.RevalidateAsync"/> in <paramref name="copy"/>, a copy of the flake with the proposed
    /// lock; the new pins are written into the copy and returned, for the caller to write with the lock. The checkout
    /// is not changed. A pin that cannot be revalidated fails the update, naming both nixpkgs revisions.
    /// </summary>
    /// <param name="copy">The copy of the flake's files: an edited review's copy, or one this makes.</param>
    /// <param name="staged">The copy exists already, with edits staged into it.</param>
    /// <param name="baseLock">The checkout's flake.lock the proposal was resolved from.</param>
    /// <param name="run">Runs a command as a step of the operation.</param>
    /// <param name="quiet">Runs a command without a step of its own.</param>
    public static async Task<List<ReviewPin>> RevalidateAsync(Config config, Request r, string copy, bool staged, string baseLock, string proposedLock,
        Func<Command, Task<CommandResult>> run, Func<Command, Task<CommandResult>> quiet, Action<string> log)
    {
        var proposal = FileErrors.Attempt(DisplayText.Of("stored_read_file", ("path", proposedLock)), () => File.ReadAllText(proposedLock), proposedLock);
        var from = RootNixpkgsRevision(baseLock);
        if (RootNixpkgsRevision(proposal) is not { } to)
        {
            log("The proposed flake.lock locks no nixpkgs revision for the flake; package pins are not checked.");
            return [];
        }
        if (from == to)
        {
            log($"nixpkgs stays at {Short(to)}; the package pins need no revalidation.");
            return [];
        }
        log($"nixpkgs moves from {(from is null ? "an unlocked revision" : Short(from))} to {Short(to)}.");
        var pins = new List<(string Name, string Text, PackagePin Pin)>();
        foreach (var name in Inherited())
        {
            var file = Path.Combine(config.Flake, PinPath(name));
            if (!File.Exists(file)) continue;
            var text = FileErrors.Attempt(DisplayText.Of("stored_read_package_pin", ("name", name)), () => File.ReadAllText(file), file);
            var pin = PackagePins.Parse(text, name);
            if (pin.NixpkgsRev == to) log($"{name}: the {pin.Version} pin was validated against nixpkgs {Short(to)} already.");
            else pins.Add((name, text, pin));
        }
        if (pins.Count == 0) return [];

        if (!staged)
        {
            var original = await Evaluation.FlakeSourceAsync(config.Flake, quiet).ConfigureAwait(false);
            FileErrors.Attempt(DisplayText.Of("stored_copy_flake_source"), () => { if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true); }, copy);
            await quiet(new Command { Label = DisplayText.Of("stored_copy_flake_source"), Argv = ["cp", "-rT", "--no-preserve=mode", original, copy] }).ConfigureAwait(false);
        }
        var copiedLock = Path.Combine(copy, "flake.lock");
        FileErrors.Attempt(DisplayText.Of("stored_write", ("file", "flake.lock")), () =>
        {
            Directory.CreateDirectory(copy);
            File.WriteAllText(copiedLock, proposal);
        }, copiedLock);

        var system = PackageUpdater.HostSystem(config, r);
        var env = new Dictionary<string, string> { ["NIX_MANAGER_HOME_FLAKE"] = $"path:{copy}", ["NIX_MANAGER_SYSTEM"] = system };
        var revalidated = new List<ReviewPin>();
        foreach (var (name, text, pin) in pins)
        {
            var validated = pin.NixpkgsRev ?? from;
            var version = (await run(new Command
            {
                Label = DisplayText.Of("stored_inspect_in_the_new_nixpkgs", ("name", name)),
                Argv = ["nix", "eval", "--impure", "--raw", "--expr",
                    $"let flake = builtins.getFlake (builtins.getEnv \"NIX_MANAGER_HOME_FLAKE\"); pkgs = import flake.inputs.nixpkgs {{ system = builtins.getEnv \"NIX_MANAGER_SYSTEM\"; }}; in pkgs.{NixText.Attr(name)}.version"],
                Env = env,
                Quiet = true,
            }).ConfigureAwait(false)).Stdout.Trim();
            if (Status(pin, to, version) == PinStatus.Superseded)
            {
                log($"{name}: nixpkgs {Short(to)} has {version}, not older than the {pin.Version} pin, so the package uses nixpkgs' own; the pin is superseded.");
                continue;
            }
            log($"{name}: the {pin.Version} pin was validated against nixpkgs {(validated is null ? "of unknown revision" : Short(validated))}; revalidating it against {Short(to)}.");
            System.Text.Json.Nodes.JsonObject candidate;
            try
            {
                candidate = await PackageUpdater.RevalidateAsync(copy, name, text, system, to, run).ConfigureAwait(false);
            }
            catch (ManagerException e)
            {
                var source = PackageUpdater.Packages[name];
                throw new ManagerException(DisplayText.Of("stored_pin_revalidation_failed", ("name", name), ("version", pin.Version),
                    ("from", validated is null ? "?" : Short(validated)), ("to", Short(to)), ("reason", e.Text),
                    ("url", $"https://github.com/NixOS/nixpkgs/compare/{validated ?? from ?? "nixos-unstable"}...{to}"),
                    ("recipes", string.Join(", ", source.Recipes.Select(recipe => $"pkgs/by-name/{source.Prefix}/{name}/{recipe}")))), e.Explanation);
            }
            var updated = JsJson.Indented(candidate) + "\n";
            var target = Path.Combine(copy, PinPath(name));
            FileErrors.Attempt(DisplayText.Of("stored_write", ("file", PinPath(name))), () => File.WriteAllText(target, updated), target);
            revalidated.Add(new ReviewPin { Name = name, Path = PinPath(name), BaseHash = Repository.HashText(text), NixpkgsRev = to, Text = updated });
            log($"{name}: revalidated the {pin.Version} pin against nixpkgs {Short(to)}; it is written with the new flake.lock.");
        }
        return revalidated;
    }

    /// <summary>
    /// The pinned package whose fixed-output dependency failed with a hash mismatch in <paramref name="log"/>, by the
    /// derivation's name, so evaluated and resolved (content-addressed) store paths both count; null for another one.
    /// </summary>
    public static string? MismatchedPin(string log, IReadOnlyDictionary<string, PackagePin> pins)
    {
        foreach (System.Text.RegularExpressions.Match match in PackageUpdater.Mismatch().Matches(PackageUpdater.Color().Replace(log, "")))
        {
            var derivation = PackageUpdater.DerivationName(match.Groups[1].Value);
            foreach (var (name, pin) in pins)
                if (PackageUpdater.Packages.TryGetValue(name, out var source)
                    && (source.FixedOutputs ?? []).Any(output => output.Replace("{version}", pin.Version, StringComparison.Ordinal) == derivation))
                    return name;
        }
        return null;
    }

    /// <summary>
    /// The safety net for a build that failed anyway: when <paramref name="log"/> has a hash mismatch on a dependency
    /// of a pin in <paramref name="flake"/> that was validated against another nixpkgs than <paramref name="lockText"/>
    /// locks, the explanation names both revisions and how to revalidate the pin. Copying the reported hash into the
    /// pin is never the fix: only a candidate build may change its hashes.
    /// </summary>
    public static Explanation? ExplainMismatch(string log, string flake, string lockText)
    {
        var pins = new Dictionary<string, PackagePin>(StringComparer.Ordinal);
        foreach (var name in Inherited())
        {
            try { pins[name] = PackagePins.Parse(File.ReadAllText(Path.Combine(flake, PinPath(name))), name); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ManagerException) { }
        }
        if (MismatchedPin(log, pins) is not { } mismatched || pins[mismatched].NixpkgsRev is not { } validated
            || RootNixpkgsRevision(lockText) is not { } used || used == validated)
            return null;
        return new Explanation
        {
            Title = DisplayText.Of("stored_the_pin_was_validated_against_another_nixpkgs", ("name", mismatched), ("validated", Short(validated)), ("used", Short(used))),
            Body = DisplayText.Of("stored_revalidate_the_pin_against_this_nixpkgs", ("name", mismatched)),
        };
    }

    /// <summary>A nixpkgs commit as people read it: its first seven characters.</summary>
    public static string Short(string revision) => revision.Length > 7 ? revision[..7] : revision;
}
