using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Operations;

namespace NixManager.Updates;

/// <summary>Checks for updates without changing the checkout (<c>checkUpdates</c> in <c>src/updates.ts</c>).</summary>
public interface IUpdateChecker
{
    /// <param name="run">Runs a command as a step of the operation, with its output in the operation's log.</param>
    Task<UpdateReport> CheckAsync(Config config, Func<Command, Task<CommandResult>> run, CancellationToken cancellationToken);
}

/// <summary>Reads a URL; the GitHub release lookups go through it so tests can answer them.</summary>
public interface INetwork
{
    Task<string> TextAsync(string url, CancellationToken cancellationToken);
}

public sealed class HttpNetwork(HttpClient? client = null) : INetwork
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string> TextAsync(string url, CancellationToken cancellationToken)
    {
        // Three attempts with exponential backoff, as the Bun version's Schedule.exponential("500 millis").
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
                if (attempt >= 2) throw new ManagerException($"Fetch failed: {e.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

public sealed partial class UpdateChecker(INetwork network, TimeProvider? clock = null) : IUpdateChecker
{
    /// <summary>The package pins of <c>packages</c> in <c>src/updates.ts</c>, checked in this order.</summary>
    public static readonly IReadOnlyList<(string Name, string Owner, string Repo)> Packages =
    [
        ("t3code", "pingdotgg", "t3code"),
        ("opencode", "anomalyco", "opencode"),
        ("pince", "korcankaraokcu", "PINCE"),
    ];

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex StableVersion();

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)-nightly\.(\d{8})\.(\d+)$")]
    private static partial Regex NightlyVersion();

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
                report.Errors.Add(e.Message);
            }
            foreach (var (name, owner, repo) in Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var pin = ReadPin(Path.Combine(config.Flake, "pkgs", name, "source.json"), name);
                    var version = await LatestAsync(name, owner, repo, pin.Channel, cancellationToken).ConfigureAwait(false);
                    if (CompareVersions(version, pin.Version) > 0)
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
                    report.Errors.Add($"{name}: {e.Message}");
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
            Label = "Check flake inputs without changing the checkout",
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
        catch (IOException e) { throw new ManagerException($"Read {path}: {e.Message}"); }
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Object)
                throw new ManagerException($"Invalid lock file {path}: no nodes.");
            return nodes.EnumerateObject()
                .Select(node => new KeyValuePair<string, JsonElement?>(node.Name,
                    node.Value.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.Object ? locked.Clone() : null))
                .ToList();
        }
        catch (JsonException e)
        {
            throw new ManagerException($"Invalid lock file {path}: {e.Message}");
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

    private sealed record Pin(string Version, string? Channel);

    private static Pin ReadPin(string path, string name)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ManagerException($"Read {name} pin: {e.Message}"); }
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            if (!StableVersion().IsMatch(version) && !NightlyVersion().IsMatch(version))
                throw new ManagerException("Invalid source pin: Expected X.Y.Z or X.Y.Z-nightly.YYYYMMDD.N");
            var channel = root.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (channel is not (null or "stable" or "nightly")) throw new ManagerException("Invalid source pin: unknown channel");
            return new Pin(version, channel);
        }
        catch (JsonException e)
        {
            throw new ManagerException($"Invalid source pin: {e.Message}");
        }
    }

    private async Task<string> LatestAsync(string name, string owner, string repo, string? channel, CancellationToken cancellationToken)
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
                return versions.Count > 0 ? versions[^1] : throw new ManagerException($"{name}: No stable or nightly release tags found");
            }
            var latest = document.RootElement.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            var match = StableTag().Match(latest);
            return match.Success ? match.Groups[1].Value : throw new ManagerException($"{name}: Release does not have a stable vX.Y.Z tag");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new ManagerException($"{name}: {e.Message}");
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
                throw new ManagerException($"Expected X.Y.Z or X.Y.Z-nightly.YYYYMMDD.N versions: {a}, {b}");
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
