using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Operations;

namespace NixManager.Updates;

/// <summary>Checks for updates without changing the checkout.</summary>
public interface IUpdateChecker
{
    /// <param name="run">Runs a command as a step of the operation, with its output in the operation's log.</param>
    Task<UpdateReport> CheckAsync(Config config, Func<Command, Task<CommandResult>> run, CancellationToken cancellationToken);
}

/// <summary>A response read whole: its status and body, for downloads that are not text.</summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Reason">The status text, such as <c>Not Found</c>.</param>
/// <param name="MediaType">The <c>Content-Type</c> media type, such as <c>image/png</c>.</param>
public sealed record NetworkResponse(int Status, string Reason, string? MediaType, byte[] Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Reads a URL; the GitHub release lookups and Flathub's app details go through it so tests can answer them.</summary>
public interface INetwork
{
    Task<string> TextAsync(string url, CancellationToken cancellationToken);

    /// <summary>Reads a URL once, as <c>fetch</c> does: any status is an answer, only a failed connection throws.</summary>
    /// <param name="followRedirects">Follow redirects; otherwise a redirect is the answer (<c>redirect: "error"</c> refuses it).</param>
    /// <exception cref="ManagerException">The request failed or was cancelled.</exception>
    Task<NetworkResponse> GetAsync(string url, bool followRedirects, CancellationToken cancellationToken);
}

public sealed class HttpNetwork(HttpClient? client = null) : INetwork
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    // Downloads end by their caller's token: Flathub's catalogue may take longer than the release lookups' timeout.
    private readonly Lazy<HttpClient> _downloads = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
    private readonly Lazy<HttpClient> _direct = new(() => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan });

    public async Task<string> TextAsync(string url, CancellationToken cancellationToken)
    {
        // Three attempts with exponential backoff from 500 milliseconds.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("nix-manager");
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {url}");
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= 2) throw new ManagerException(DisplayText.Of("stored_fetch_failed", ("reason", e.Message)));
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<NetworkResponse> GetAsync(string url, bool followRedirects, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("nix-manager");
            using var response = await (followRedirects ? _downloads : _direct).Value.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new NetworkResponse((int)response.StatusCode, response.ReasonPhrase ?? "", response.Content.Headers.ContentType?.MediaType, body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ManagerException(cancellationToken.IsCancellationRequested
                ? DisplayText.Of("stored_the_operation_timed_out")
                : DisplayText.Of("stored_fetch_failed", ("reason", e.Message)));
        }
    }
}

public sealed partial class UpdateChecker(INetwork network, TimeProvider? clock = null) : IUpdateChecker
{
    /// <summary>The package pins of <c>PackageUpdater.Packages</c>, checked in this order.</summary>
    public static readonly IReadOnlyList<(string Name, string Owner, string Repo)> Packages =
        PackageUpdater.Packages.Select(pair => (pair.Key, pair.Value.Owner, pair.Value.Repo)).ToList();

    /// <summary>Where release lookups go; package pin updates share it.</summary>
    public INetwork Network => network;

    private static Regex StableVersion() => PackagePins.StableVersion();

    private static Regex NightlyVersion() => PackagePins.NightlyVersion();

    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)$")]
    private static partial Regex StableTag();

    public async Task<UpdateReport> CheckAsync(Config config, Func<Command, Task<CommandResult>> run, CancellationToken cancellationToken)
    {
        var report = new UpdateReport { CheckedAt = Timestamps.Now(clock) };
        var work = Directory.CreateTempSubdirectory("nix-manager-");
        try
        {
            try
            {
                await CheckInputsAsync(config, run, work.FullName, report).ConfigureAwait(false);
            }
            catch (ManagerException e)
            {
                report.Errors.Add(e.Text);
            }
            foreach (var (name, owner, repo) in Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var pin = ReadPin(Path.Combine(config.Flake, "pkgs", name, "source.json"), name);
                    var version = await LatestAsync(network, name, owner, repo, pin.Channel, cancellationToken).ConfigureAwait(false);
                    if (CompareVersions(version, pin.Version) > 0 && !PackageUpdater.Packages[name].BlockedVersions.Contains(version))
                        report.Items.Add(new UpdateItem
                        {
                            Name = name,
                            Current = pin.Version,
                            Latest = version,
                            Kind = "package",
                            Source = $"{owner}/{repo}",
                            Url = $"https://github.com/{owner}/{repo}/releases/tag/v{version}",
                        });
                }
                catch (ManagerException e)
                {
                    report.Errors.Add(DisplayText.Of("stored_package_check_failed", ("name", name), ("reason", e.Text)));
                }
            }
            return report;
        }
        finally
        {
            try { work.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task CheckInputsAsync(Config config, Func<Command, Task<CommandResult>> run, string work, UpdateReport report)
    {
        var before = ReadLock(Path.Combine(config.Flake, "flake.lock")).ToDictionary(pair => pair.Key, pair => pair.Value);
        var proposed = Path.Combine(work, "flake.lock");
        await run(new Command
        {
            Label = DisplayText.Of("stored_check_flake_inputs_without_changing_the_checkout"),
            Argv = ["nix", "flake", "update", "--flake", config.Flake, "--output-lock-file", proposed],
        }).ConfigureAwait(false);
        var after = ReadLock(proposed);
        foreach (var (name, node) in after)
        {
            if (node is not { } next) continue;
            var old = before.GetValueOrDefault(name);
            if (Fingerprint(old) == Fingerprint(next)) continue;
            var from = NixText.LockRef(old);
            var to = NixText.LockRef(next);
            report.Items.Add(new UpdateItem
            {
                Name = name,
                Current = Revision(old, "new"),
                Latest = Revision(next, "changed"),
                Kind = "input",
                CurrentDate = from?.LastModified,
                LatestDate = to?.LastModified,
                Source = to?.Source,
                Url = to?.Url,
            });
        }
    }

    /// <summary>The <c>locked</c> attributes of every node, in the file's order.</summary>
    private static List<KeyValuePair<string, JsonElement?>> ReadLock(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException e)
        {
            throw new ManagerException(DisplayText.Of("stored_step_failed_because",
                ("step", DisplayText.Of("stored_read_file", ("path", path))), ("reason", DisplayText.Plain(e.Message))));
        }
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Object)
                throw new ManagerException(DisplayText.Of("stored_invalid_lock_file_no_nodes", ("path", path)));
            return nodes.EnumerateObject()
                .Select(node => new KeyValuePair<string, JsonElement?>(node.Name,
                    node.Value.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.Object ? locked.Clone() : null))
                .ToList();
        }
        catch (JsonException e)
        {
            throw new ManagerException(DisplayText.Of("stored_invalid_lock_file", ("path", path), ("reason", e.Message)));
        }
    }

    /// <summary>The locked attributes, sorted by name, so attribute order does not count as a change.</summary>
    private static string Fingerprint(JsonElement? locked) =>
        locked is { } node
            ? string.Join('\u0000', node.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + p.Value.GetRawText()))
            : "";

    private static string Revision(JsonElement? locked, string fallback)
    {
        if (locked is not { } node) return fallback;
        if (node.TryGetProperty("rev", out var rev) && rev.ValueKind == JsonValueKind.String) return Truncate(rev.GetString()!, 12);
        if (node.TryGetProperty("narHash", out var hash) && hash.ValueKind == JsonValueKind.String) return Truncate(hash.GetString()!, 19);
        return fallback;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private static PackagePin ReadPin(string path, string name)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ManagerException(DisplayText.Of("stored_step_failed_because",
                ("step", DisplayText.Of("stored_read_package_pin", ("name", name))), ("reason", FileErrors.Describe(e, path))));
        }
        return PackagePins.Parse(text, name);
    }

    /// <summary>The newest release on a pin's channel (<c>latest</c>).</summary>
    internal static async Task<string> LatestAsync(INetwork network, string name, string owner, string repo, string? channel, CancellationToken cancellationToken)
    {
        var nightly = channel == "nightly";
        var raw = await network.TextAsync(nightly
            ? $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100"
            : $"https://api.github.com/repos/{owner}/{repo}/releases/latest", cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (nightly)
            {
                var versions = document.RootElement.EnumerateArray()
                    .Where(release => !(release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True))
                    .Select(release => release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String ? StripV(tag.GetString()!) : "")
                    .Where(version => StableVersion().IsMatch(version) || NightlyVersion().IsMatch(version))
                    .ToList();
                versions.Sort(CompareVersions);
                return versions.Count > 0 ? versions[^1] : throw new ManagerException(DisplayText.Of("stored_no_stable_or_nightly_release_tags", ("name", name)));
            }
            var latest = document.RootElement.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            var match = StableTag().Match(latest);
            return match.Success ? match.Groups[1].Value : throw new ManagerException(DisplayText.Of("stored_latest_release_has_no_stable_tag", ("name", name)));
        }
        catch (JsonException e)
        {
            throw new ManagerException(DisplayText.Of(nightly ? "stored_release_list_not_valid_json" : "stored_latest_release_not_valid_json",
                ("name", name), ("reason", e.Message)));
        }
        catch (InvalidOperationException)
        {
            // The answer is JSON of another shape, such as an object where the list of releases belongs.
            throw new ManagerException(DisplayText.Of(nightly ? "stored_release_list_not_a_list" : "stored_latest_release_not_an_object", ("name", name)));
        }
    }

    private static string StripV(string tag) => tag.StartsWith('v') ? tag[1..] : tag;

    /// <summary>Orders X.Y.Z and X.Y.Z-nightly.YYYYMMDD.N; nightlies precede their X.Y.Z (<c>compareVersions</c>).</summary>
    public static int CompareVersions(string a, string b)
    {
        BigInteger[] Parse(string version)
        {
            var stable = StableVersion().Match(version);
            var match = stable.Success ? stable : NightlyVersion().Match(version);
            if (!match.Success)
                throw new ManagerException(DisplayText.Of("stored_versions_not_comparable", ("first", a), ("second", b)));
            var parts = match.Groups.Cast<Group>().Skip(1).Select(group => BigInteger.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            return stable.Success ? [.. parts, new BigInteger(9007199254740991), BigInteger.Zero] : [.. parts];
        }
        var left = Parse(a);
        var right = Parse(b);
        for (var i = 0; i < 5; i++)
            if (left[i] != right[i]) return left[i] > right[i] ? 1 : -1;
        return 0;
    }
}
