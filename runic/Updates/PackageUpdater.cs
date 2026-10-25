using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;

namespace NixManager.Updates;

/// <summary>Where a pinned package's releases and nixpkgs recipes come from (an entry of <c>Packages</c>).</summary>
/// <param name="Prefix">The <c>pkgs/by-name</c> shard in nixpkgs.</param>
/// <param name="Recipes">The nixpkgs recipe files the candidate inherits, compared against nixos-unstable.</param>
/// <param name="BlockedVersions">Releases with a known upstream regression, never pinned.</param>
/// <param name="Npm">
/// The npm package whose <c>latest</c> dist-tag names the stable release, for projects that tag releases without
/// publishing them on GitHub. The source still comes from the matching <c>vX.Y.Z</c> tag.
/// </param>
/// <param name="FixedOutputs">
/// The names of the fixed-output derivations whose hashes the pin holds, with <c>{version}</c> for the pinned version,
/// by which a hash mismatch in a configuration build is traced to the pin.
/// </param>
public sealed record PackageSource(string Owner, string Repo, string Prefix, IReadOnlyList<string> Recipes, IReadOnlyList<string> BlockedVersions, string? Npm = null,
    IReadOnlyList<string>? FixedOutputs = null);

/// <summary>
/// Updates a package's pin in the checkout: the newest release on the
/// pin's channel, the inherited nixpkgs recipes compared with nixos-unstable, the source hash fetched, the
/// dependency hashes discovered with a fake hash, a build of the candidate, and then an atomic replacement of the
/// pin, refused when the pin changed meanwhile.
/// </summary>
/// <param name="packages">The packages; tests replace the table, such as to block a version.</param>
public sealed partial class PackageUpdater(INetwork network, IReadOnlyDictionary<string, PackageSource>? packages = null)
{
    /// <summary>The packages with an updater, in the order the update check lists them (<c>packages</c>).</summary>
    public static readonly IReadOnlyDictionary<string, PackageSource> Packages = new Dictionary<string, PackageSource>
    {
        ["t3code"] = new("pingdotgg", "t3code", "t3", ["package.nix", "unwrapped.nix", "resource-monitor.nix"], [],
            FixedOutputs: ["t3code-unwrapped-pnpm-deps.drv", "t3code-resource-monitor-{version}-vendor-staging.drv", "t3code-resource-monitor-{version}-vendor.drv"]),
        // OpenCode 2.x is tagged and published to npm only; GitHub's latest release stays on 1.x.
        ["opencode"] = new("anomalyco", "opencode", "op", ["package.nix"], [], Npm: "@opencode/cli", FixedOutputs: ["opencode-node_modules-{version}.drv"]),
        ["pince"] = new("korcankaraokcu", "PINCE", "", [], []),
    };

    private readonly IReadOnlyDictionary<string, PackageSource> _packages = packages ?? Packages;

    /// <summary>The hash a candidate starts with, so the build fails and names the real one (<c>fakeHash</c>).</summary>
    public const string FakeHash = "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    /// <summary>The candidate T3 Code, OpenCode desktop, or the recipe itself, built from the pin under test (<c>candidateExpression</c>).</summary>
    public const string CandidateExpression = """
        let
          flake = builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE");
          system = builtins.getEnv "NIX_MANAGER_SYSTEM";
          pkgs = import flake.inputs.nixpkgs { inherit system; config.allowUnfree = true; };
          candidate = import (builtins.toPath (builtins.getEnv "NIX_MANAGER_RECIPE")) {
            prev = pkgs;
            pin = builtins.fromJSON (builtins.readFile (builtins.getEnv "NIX_MANAGER_PACKAGE_PIN"));
            forceOverride = true;
          };
          opencodeDesktop = pkgs.opencode-desktop.overrideAttrs (_: {
            inherit (candidate) version src node_modules patches;
          });
          t3code = flake.packages.${system}.t3code.override {
            t3code-unwrapped = candidate.unwrapped;
            t3code-resource-monitor = candidate.resourceMonitor;
          };
        in if builtins.getEnv "NIX_MANAGER_PACKAGE_NAME" == "t3code" then t3code
        else if builtins.getEnv "NIX_MANAGER_PACKAGE_NAME" == "opencode" then opencodeDesktop
        else candidate
        """;

    /// <summary>PINCE built from the checkout's recipe and the pin under test (<c>pinceExpression</c>).</summary>
    public const string PinceExpression = """
        let
          flake = builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE");
          pkgs = import flake.inputs.nixpkgs { system = builtins.getEnv "NIX_MANAGER_SYSTEM"; config.allowUnfree = true; };
        in pkgs.callPackage (flake.outPath + "/pkgs/pince/package.nix") {
          pin = builtins.fromJSON (builtins.readFile (builtins.getEnv "NIX_MANAGER_PACKAGE_PIN"));
        }
        """;

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    internal static partial Regex Color();

    [GeneratedRegex(@"hash mismatch in fixed-output derivation '([^']+)'")]
    internal static partial Regex Mismatch();

    [GeneratedRegex(@"^\s*got:\s*(sha256-[A-Za-z0-9+/]{43}=)\s*$")]
    private static partial Regex Got();

    [GeneratedRegex(@"^\s*version = ""(\d+\.\d+\.\d+)"";", RegexOptions.Multiline)]
    private static partial Regex RecipeVersion();

    [GeneratedRegex("^[a-f0-9]{40}$")]
    private static partial Regex Revision();

    /// <summary>
    /// The hash Nix printed for <paramref name="expected"/> in a failed fake-hash build; other derivations'
    /// mismatches are ignored (<c>extractHash</c>). With content-addressed inputs Nix builds and reports the resolved
    /// derivation, whose store path differs from the evaluated one but keeps its name.
    /// </summary>
    public static string ExtractHash(string log, string expected, int code)
    {
        var matching = false;
        if (code != 0)
            foreach (var line in Color().Replace(log, "").Split('\n'))
            {
                var mismatch = Mismatch().Match(line);
                if (mismatch.Success)
                    matching = mismatch.Groups[1].Value == expected || DerivationName(mismatch.Groups[1].Value) == DerivationName(expected);
                else if (matching && Got().Match(line) is { Success: true } got) return got.Groups[1].Value;
            }
        throw new ManagerException(code == 0
            ? DisplayText.Of("stored_fake_hash_build_succeeded")
            : DisplayText.Of("stored_could_not_extract_the_hash", ("derivation", expected)));
    }

    /// <summary>The name of a store path without its hash, such as <c>t3code-unwrapped-pnpm-deps.drv</c>.</summary>
    public static string DerivationName(string path)
    {
        var file = Path.GetFileName(path);
        var separator = file.IndexOf('-', StringComparison.Ordinal);
        return separator == 32 ? file[(separator + 1)..] : file;
    }

    /// <summary>Evaluates the fixed-output derivation, builds it with the fake hash and reads the real hash (<c>discoverHash</c>).</summary>
    public static async Task<string> DiscoverHashAsync(DisplayText label, string expr, IReadOnlyDictionary<string, string> env, Func<Command, Task<CommandResult>> run)
    {
        var expected = (await run(new Command
        {
            Label = DisplayText.Of("stored_evaluate_the_derivation", ("label", label)),
            Argv = ["nix", "eval", "--impure", "--raw", "--expr", $"({expr}).drvPath"],
            Env = env,
            Quiet = true,
        }).ConfigureAwait(false)).Stdout.Trim();
        var result = await run(new Command
        {
            Label = DisplayText.Of("stored_compute_the_hash", ("label", label)),
            Argv = ["nix", "build", "--no-link", "--impure", "--print-build-logs", "--expr", expr],
            Env = env,
            AllowFailure = true,
        }).ConfigureAwait(false);
        return ExtractHash(result.Stderr + "\n" + result.Stdout, expected, result.Code);
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="value"/> through a new file with the same mode, unless
    /// the pin no longer reads <paramref name="before"/> (<c>atomicPin</c>).
    /// </summary>
    public static void AtomicPin(string path, string before, JsonNode value)
    {
        var candidate = $"{path}.{Guid.NewGuid()}.tmp";
        FileErrors.Attempt(DisplayText.Of("stored_save_validated_pin"), () =>
        {
            try
            {
                using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                    writer.Write(JsJson.Indented(value) + "\n");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(candidate, File.GetUnixFileMode(path) & (UnixFileMode)0b111_111_111);
                if (File.ReadAllText(path) != before)
                    throw new ManagerException(DisplayText.Of("stored_pin_changed_during_the_build"));
                File.Move(candidate, path, overwrite: true);
            }
            finally
            {
                File.Delete(candidate);
            }
        }, path);
    }

    private static void SaveJson(string path, JsonNode data) =>
        FileErrors.Attempt(DisplayText.Of("stored_write", ("file", path)), () => File.WriteAllText(path, JsJson.Indented(data) + "\n"), path);

    /// <summary>
    /// Updates the pin of <c>r.action</c>'s package. Commands go through <paramref name="run"/>, which runs each as a
    /// step of the operation; <paramref name="log"/> receives the decisions.
    /// </summary>
    public async Task UpdateAsync(Config config, Request r, Func<Command, Task<CommandResult>> run, Action<string> log, CancellationToken cancellationToken)
    {
        var name = r.Action.Replace("-update", "", StringComparison.Ordinal);
        if (!_packages.TryGetValue(name, out var p)) throw new ManagerException(DisplayText.Of("stored_unknown_action", ("action", r.Action)));
        var pinFile = Path.Combine(config.Flake, "pkgs", name, "source.json");
        var before = FileErrors.Attempt(DisplayText.Of("stored_read_current_pin"), () => File.ReadAllText(pinFile), pinFile);
        var pin = PackagePins.Parse(before, name);
        var channel = pin.Channel ?? "stable";
        var version = await UpdateChecker.LatestAsync(network, name, p.Owner, p.Repo, channel, cancellationToken, p.Npm).ConfigureAwait(false);
        if (p.BlockedVersions.Contains(version))
            throw new ManagerException(DisplayText.Of("stored_version_blocked_by_upstream_regression", ("name", name), ("version", version)));
        var order = UpdateChecker.CompareVersions(pin.Version, version);
        log($"{name}: local pin {pin.Version} | {channel} release {version}");
        if (order > 0) throw new ManagerException(DisplayText.Of("stored_refusing_a_downgrade", ("name", name)));

        var work = FileErrors.Attempt(DisplayText.Of("stored_create_temporary_workspace"), () => Directory.CreateTempSubdirectory("nix-manager-").FullName);
        try
        {
            var env = CandidateEnvironment(config.Flake, name, HostSystem(config, r), work);
            async Task<string> Evaluate(string expr) => (await run(new Command
            {
                Label = DisplayText.Of("stored_inspect_locked_nixpkgs"),
                Argv = ["nix", "eval", "--impure", "--raw", "--expr", expr],
                Env = env,
                Quiet = true,
            }).ConfigureAwait(false)).Stdout.Trim();

            var rev = "";
            if (name != "pince")
            {
                var path = await Evaluate("""(builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE")).inputs.nixpkgs.outPath""").ConfigureAwait(false);
                var locked = await Evaluate(
                    """let flake = builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE"); pkgs = import flake.inputs.nixpkgs { system = builtins.getEnv "NIX_MANAGER_SYSTEM"; }; in pkgs.${builtins.getEnv "NIX_MANAGER_PACKAGE_NAME"}.version""")
                    .ConfigureAwait(false);
                rev = await Evaluate("""(builtins.getFlake (builtins.getEnv "NIX_MANAGER_HOME_FLAKE")).inputs.nixpkgs.rev""").ConfigureAwait(false);
                var upstream = await network.TextAsync("https://api.github.com/repos/NixOS/nixpkgs/commits/nixos-unstable", cancellationToken).ConfigureAwait(false);
                var upstreamRev = UpstreamRevision(upstream);
                foreach (var recipe in p.Recipes)
                {
                    var text = await network.TextAsync(
                        $"https://raw.githubusercontent.com/NixOS/nixpkgs/{upstreamRev}/pkgs/by-name/{p.Prefix}/{name}/{recipe}", cancellationToken).ConfigureAwait(false);
                    var inherited = Path.Combine(path, "pkgs/by-name", p.Prefix, name, recipe);
                    var saved = Path.Combine(work, recipe);
                    FileErrors.Attempt(DisplayText.Of("stored_save_upstream_recipe"), () => File.WriteAllText(saved, text), saved);
                    var compared = await run(new Command
                    {
                        Label = DisplayText.Of("stored_compare_recipe_against_nixos_unstable", ("recipe", recipe), ("revision", upstreamRev)),
                        Argv = ["diff", "-u", inherited, saved],
                        AllowFailure = true,
                    }).ConfigureAwait(false);
                    if (compared.Code > 1) throw new ManagerException(DisplayText.Of("stored_could_not_compare_inherited_recipe"));
                    if (recipe == (name == "t3code" ? "unwrapped.nix" : "package.nix"))
                    {
                        var upstreamVersion = RecipeVersion().Match(text);
                        if (!upstreamVersion.Success) throw new ManagerException(DisplayText.Of("stored_upstream_recipe_layout_changed"));
                        log($"Locked nixpkgs {locked} | nixos-unstable {upstreamVersion.Groups[1].Value}. Candidate uses the locked recipe.");
                    }
                }
                var lockedOrder = UpdateChecker.CompareVersions(locked, version);
                if (lockedOrder > 0 || (!r.Force && (locked == version || (order == 0 && pin.NixpkgsRev == rev))))
                {
                    log($"{name} is current. Use --force to recalculate hashes.");
                    return;
                }
            }
            else if (order == 0 && !r.Force)
            {
                log("PINCE is current.");
                return;
            }
            if (r.DryRun)
            {
                log($"Would fetch, hash and build {name} {version}, then atomically save {pinFile}.");
                return;
            }

            var url = name == "pince"
                ? $"https://github.com/{p.Owner}/{p.Repo}/releases/download/v{version}/PINCE-x86_64.AppImage"
                : $"https://github.com/{p.Owner}/{p.Repo}/archive/refs/tags/v{version}.tar.gz";
            var rawHash = (await run(new Command
            {
                Label = DisplayText.Of("stored_fetch_release"),
                Argv = ["nix-prefetch-url", .. name == "pince" ? Array.Empty<string>() : ["--unpack"], "--type", "sha256", url],
            }).ConfigureAwait(false)).Stdout.Trim();
            var hash = (await run(new Command
            {
                Label = DisplayText.Of("stored_convert_source_hash"),
                Argv = ["nix", "hash", "convert", "--hash-algo", "sha256", "--to", "sri", rawHash],
                Quiet = true,
            }).ConfigureAwait(false)).Stdout.Trim();
            var candidate = new JsonObject { ["version"] = version };
            if (name == "pince") candidate["hash"] = hash;
            else
            {
                if (pin.Channel is { } pinned) candidate["channel"] = pinned;
                candidate["srcHash"] = hash;
                candidate["nixpkgsRev"] = rev;
                if (name == "t3code")
                {
                    candidate["pnpmHash"] = FakeHash;
                    candidate["cargoHash"] = FakeHash;
                }
                else candidate["nodeHash"] = FakeHash;
            }
            await ValidateAsync(name, candidate, env, run).ConfigureAwait(false);
            AtomicPin(pinFile, before, candidate);
            log($"Updated {pinFile}. Apply with nix-manager home switch.");
        }
        finally
        {
            RemoveWorkspace(work);
        }
    }

    /// <summary>
    /// Revalidates the pin <paramref name="pinText"/> of <paramref name="name"/> against the nixpkgs that
    /// <paramref name="flake"/> locks, such as a copy of the checkout with a proposed flake.lock: the same release and
    /// source, the dependency hashes discovered again under that nixpkgs' recipe, the full candidate built, and
    /// <paramref name="revision"/> recorded as the pin's nixpkgs. No release is looked up and no source prefetched; the
    /// checkout's pin is not written, the caller stages the returned one.
    /// </summary>
    /// <param name="system">The system the candidate is built for, such as <c>x86_64-linux</c>.</param>
    public static async Task<JsonObject> RevalidateAsync(string flake, string name, string pinText, string system, string revision, Func<Command, Task<CommandResult>> run)
    {
        PackagePins.Parse(pinText, name);
        var candidate = JsonNode.Parse(pinText)!.AsObject();
        candidate["nixpkgsRev"] = revision;
        foreach (var field in name == "t3code" ? new[] { "pnpmHash", "cargoHash" } : ["nodeHash"]) candidate[field] = FakeHash;
        var work = FileErrors.Attempt(DisplayText.Of("stored_create_temporary_workspace"), () => Directory.CreateTempSubdirectory("nix-manager-").FullName);
        try
        {
            await ValidateAsync(name, candidate, CandidateEnvironment(flake, name, system, work), run).ConfigureAwait(false);
            return candidate;
        }
        finally
        {
            RemoveWorkspace(work);
        }
    }

    /// <summary>The system of the request's host, which candidates are built for.</summary>
    public static string HostSystem(Config config, Request r) =>
        HostResolution.Resolve(config.Hosts, r.Host is { Length: > 0 } ? r.Host : config.HostKey).Value.System;

    /// <summary>What the candidate expressions read: the flake they evaluate, its recipe and the pin under test in <paramref name="work"/>.</summary>
    private static Dictionary<string, string> CandidateEnvironment(string flake, string name, string system, string work) => new()
    {
        ["NIX_MANAGER_SYSTEM"] = system,
        ["NIX_MANAGER_PACKAGE_NAME"] = name,
        ["NIX_MANAGER_HOME_FLAKE"] = $"path:{flake}",
        ["NIX_MANAGER_RECIPE"] = Path.Combine(flake, "pkgs", name, "package.nix"),
        ["NIX_MANAGER_PACKAGE_PIN"] = Path.Combine(work, "pin.json"),
    };

    /// <summary>
    /// Replaces the fake dependency hashes of <paramref name="candidate"/> with the ones Nix reports, then builds the
    /// whole candidate; a pin is only saved after this succeeds.
    /// </summary>
    private static async Task ValidateAsync(string name, JsonObject candidate, IReadOnlyDictionary<string, string> env, Func<Command, Task<CommandResult>> run)
    {
        var pinPath = env["NIX_MANAGER_PACKAGE_PIN"];
        SaveJson(pinPath, candidate);
        var expr = name == "pince" ? PinceExpression : CandidateExpression;
        if (name == "t3code")
        {
            candidate["pnpmHash"] = await DiscoverHashAsync(DisplayText.Of("stored_hash_t3_code_pnpm_dependencies"), $"({expr}).unwrapped.pnpmDeps", env, run).ConfigureAwait(false);
            SaveJson(pinPath, candidate);
            candidate["cargoHash"] = await DiscoverHashAsync(DisplayText.Of("stored_hash_t3_code_cargo_dependencies"), $"({expr}).resourceMonitor.cargoDeps.vendorStaging", env, run)
                .ConfigureAwait(false);
        }
        else if (name == "opencode")
            candidate["nodeHash"] = await DiscoverHashAsync(DisplayText.Of("stored_hash_opencode_node_modules"), $"({expr}).node_modules", env, run).ConfigureAwait(false);
        SaveJson(pinPath, candidate);
        await run(new Command
        {
            Label = DisplayText.Of("stored_build_before_saving_pin", ("name", name), ("version", candidate["version"]!.GetValue<string>())),
            Argv = ["nix", "build", "--no-link", "--impure", "--print-build-logs", "--expr", expr],
            Env = env,
        }).ConfigureAwait(false);
    }

    private static void RemoveWorkspace(string work)
    {
        try { Directory.Delete(work, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The commit of nixos-unstable from GitHub's answer.</summary>
    private static string UpstreamRevision(string text)
    {
        string? sha;
        try
        {
            using var document = JsonDocument.Parse(text);
            sha = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("sha", out var value) &&
                  value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException e)
        {
            throw new ManagerException(DisplayText.Of("stored_upstream_revision_not_valid_json", ("reason", e.Message)));
        }
        return sha is not null && Revision().IsMatch(sha) ? sha : throw new ManagerException(DisplayText.Of("stored_upstream_revision_invalid"));
    }
}
