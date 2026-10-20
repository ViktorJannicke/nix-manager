using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Operations;
using NixManager.Updates;

namespace NixManager.Software;

/// <summary>A catalog entry with what Flathub says about it, when it is an app.</summary>
public sealed record AppEntry : CatalogEntry
{
    public AppEntry() { }

    [SetsRequiredMembers]
    public AppEntry(CatalogEntry entry, AppInfo? app) : base(entry) => App = app;

    /// <summary>Flathub's name, summary, categories and icon; null for a package that is not an app.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AppInfo? App { get; init; }
}

/// <summary>Flathub's catalogue as it is kept in <c>apps/appstream.json</c> (<c>AppStream</c>).</summary>
public sealed record AppStream(string FetchedAt, IReadOnlyList<AppInfo> Apps);

/// <summary>Flathub's app catalogue: <c>off</c> when the preference turned it off (<c>AppsStatus</c>).</summary>
public sealed record AppsStatus
{
    /// <summary><c>off</c>, <c>missing</c>, <c>fetching</c>, <c>ready</c> or <c>failed</c>.</summary>
    public required string State { get; init; }
    public string? FetchedAt { get; init; }
    /// <summary>The last download failed; a ready catalogue is older.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Apps in the package catalog. nixpkgs knows which packages exist but not which of them are
/// apps people open from a menu, what they are called there or what they look like. Flathub's public AppStream
/// catalogue does; its entries are matched to nixpkgs packages by homepage and name. Packages still come from the
/// locked nixpkgs: Flathub only names, sorts and pictures them.
/// </summary>
public static partial class Apps
{
    /// <summary>Freedesktop main categories, as the app list groups them (<c>appCategories</c>).</summary>
    public static readonly IReadOnlyList<string> Categories =
        ["AudioVideo", "Development", "Education", "Game", "Graphics", "Network", "Office", "Science", "System", "Utility"];

    public const string AppStreamUrl = "https://dl.flathub.org/repo/appstream/x86_64/appstream.xml.gz";
    private const string IconHost = "https://dl.flathub.org/";

    /// <summary>Where the downloaded catalogue is kept (<c>appStreamPath</c>).</summary>
    public static string AppStreamPath(Config config) => Path.Combine(config.StateDir, "apps", "appstream.json");

    /// <summary>Where an app's icon is kept once downloaded (<c>iconPath</c>).</summary>
    public static string IconPath(Config config, string id) => Path.Combine(config.StateDir, "apps", "icons", $"{id}.png");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,200}$")]
    private static partial Regex AppId();

    /// <summary>An AppStream id that can name a file (<c>validAppId</c>).</summary>
    public static bool ValidAppId(string id) => AppId().IsMatch(id);

    /// <summary>Whether app details are on: the <c>appDetails</c> preference, on unless turned off.</summary>
    public static bool Enabled(Config config) => Preferences.Read(config)["appDetails"]?.GetValueKind() != System.Text.Json.JsonValueKind.False;

    // --- Parsing ---------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Entities = new()
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
    };

    [GeneratedRegex("&(#x[0-9a-f]+|#[0-9]+|[a-z]+);", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Entity();

    /// <summary>Element text with its character references resolved (<c>text</c>).</summary>
    private static string Text(string value) => Entity().Replace(value, match =>
    {
        var name = match.Groups[1].Value;
        if (name[0] != '#') return Entities.GetValueOrDefault(name) ?? match.Value;
        var hex = name.Length > 1 && name[1] is 'x' or 'X';
        return int.TryParse(name.AsSpan(hex ? 2 : 1), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            && code is >= 0 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF)
            ? char.ConvertFromUtf32(code)
            : match.Value;
    }).Trim();

    private static readonly Dictionary<string, Regex> Elements = [];

    /// <summary>Untranslated values of a simple element (<c>values</c>).</summary>
    private static List<string> Values(string block, string tag)
    {
        Regex pattern;
        lock (Elements)
            if (!Elements.TryGetValue(tag, out pattern!))
                Elements[tag] = pattern = new Regex($@"<{tag}(\s[^>]*)?>([^<]*)</{tag}>", RegexOptions.CultureInvariant);
        return pattern.Matches(block)
            .Where(match => !match.Groups[1].Value.Contains("xml:lang", StringComparison.Ordinal))
            .Select(match => Text(match.Groups[2].Value))
            .ToList();
    }

    [GeneratedRegex(@"<component type=""desktop(?:-application)?"">([\s\S]*?)</component>")]
    private static partial Regex Component();

    [GeneratedRegex(@"<(developer|description|releases|screenshots|content_rating|branding)\b[\s\S]*?</\1>")]
    private static partial Regex Nested();

    [GeneratedRegex(@"<icon ([^>]*type=""(remote|cached)""[^>]*)>([^<]+)</icon>")]
    private static partial Regex Icon();

    [GeneratedRegex(@"width=""(\d+)""")]
    private static partial Regex Width();

    [GeneratedRegex(@"/\.\.|[?#]")]
    private static partial Regex UnsafeUrl();

    [GeneratedRegex(@"<url type=""homepage"">([^<]+)</url>")]
    private static partial Regex Homepage();

    /// <summary>The desktop apps in an AppStream catalogue (<c>parseAppStream</c>).</summary>
    public static List<AppInfo> ParseAppStream(string xml)
    {
        var apps = new List<AppInfo>();
        foreach (Match component in Component().Matches(xml))
        {
            // Nested elements also hold names; only the app's own are wanted.
            var own = Nested().Replace(component.Groups[1].Value, "");
            var id = Values(own, "id").FirstOrDefault();
            var name = Values(own, "name").FirstOrDefault();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
            // Remote icons have full addresses; cached ones sit in the repository's icon folders.
            var icon = Icon().Matches(own)
                .Where(match => !match.Groups[1].Value.Contains("scale=", StringComparison.Ordinal))
                .Select(match =>
                {
                    var width = Width().Match(match.Groups[1].Value);
                    var size = width.Success && int.TryParse(width.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
                    var remote = match.Groups[2].Value == "remote";
                    var url = remote ? Text(match.Groups[3].Value) : $"{IconHost}repo/appstream/x86_64/icons/{size}x{size}/{Text(match.Groups[3].Value)}";
                    return (Size: size, Url: url, Remote: remote);
                })
                .Where(icon => icon.Size != 0 && icon.Url.StartsWith(IconHost, StringComparison.Ordinal) && !UnsafeUrl().IsMatch(icon.Url))
                .OrderBy(icon => Math.Abs(icon.Size - 128))
                .ThenBy(icon => icon.Remote ? 0 : 1)
                .Select(icon => icon.Url)
                .FirstOrDefault();
            apps.Add(new AppInfo
            {
                Id = id,
                Name = name,
                Summary = Values(own, "summary").FirstOrDefault(),
                Categories = Values(own, "category").Where(Categories.Contains).Distinct().ToList(),
                Icon = icon,
                Homepage = Homepage().Match(own) is { Success: true } homepage ? homepage.Groups[1].Value.Trim() : null,
                Keywords = Values(own, "keyword").Select(keyword => keyword.ToLowerInvariant()).Distinct().Take(12).ToList(),
            });
        }
        return apps;
    }

    // --- Matching --------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^https?://(www\.)?")]
    private static partial Regex Scheme();

    [GeneratedRegex("[?#].*$")]
    private static partial Regex QueryPart();

    [GeneratedRegex(@"(/|\.git|/index\.html?)$")]
    private static partial Regex Trailing();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NotKey();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"-(bin|unwrapped|wrapped|fhs|beta|nightly|git|full|minimal)$|_\d+$")]
    private static partial Regex Variant();

    /// <summary>A homepage without its scheme, <c>www.</c>, query and trailing slash (<c>site</c>).</summary>
    private static string? Site(string? url)
    {
        if (url is null) return null;
        var site = Trailing().Replace(QueryPart().Replace(Scheme().Replace(url.ToLowerInvariant(), "", 1), "", 1), "", 1);
        return site.Length > 0 ? site : null;
    }

    private static string? HostOf(string? url) => Site(url)?.Split('/')[0];

    private static string? Key(string? value) => value is null ? null : NotKey().Replace(value.ToLowerInvariant(), "") is { Length: > 0 } key ? key : null;

    private static readonly HashSet<string> Common =
    [
        "with", "your", "from", "that", "this", "the", "and", "for", "app", "application", "tool", "simple", "free", "open", "source",
        "linux", "based", "client", "program", "software", "desktop", "easy", "fast", "modern",
    ];

    private static List<string> Words(string value) =>
        NotWord().Split(value.ToLowerInvariant()).Where(word => word.Length > 3 && !Common.Contains(word)).ToList();

    /// <summary>The developer in a reverse domain id: discordapp in com.discordapp.Discord, ahrm in io.github.ahrm.sioyek (<c>developerLabel</c>).</summary>
    private static string? DeveloperLabel(string id)
    {
        var parts = id.ToLowerInvariant().Split('.');
        var label = parts.Length > 1 && parts[1] is "github" or "gitlab" or "codeberg" or "sourceforge" or "sr"
            ? parts.ElementAtOrDefault(2)
            : parts.ElementAtOrDefault(1);
        return label is { Length: > 3 } && parts.Length > 2 ? label : null;
    }

    /// <summary>
    /// Pairs apps with top-level packages: the same homepage, or the same name on the same site (<c>matchApps</c>).
    /// Each app names one package, preferring the plain attribute over binary, unwrapped and versioned variants.
    /// </summary>
    public static Dictionary<string, AppInfo> Match(IReadOnlyList<AppInfo> apps, IReadOnlyList<CatalogEntry> entries)
    {
        var bySite = new Dictionary<string, List<CatalogEntry>>();
        var byName = new Dictionary<string, List<CatalogEntry>>();
        static void Add(Dictionary<string, List<CatalogEntry>> map, string? name, CatalogEntry entry)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!map.TryGetValue(name, out var list)) map[name] = [entry];
            else if (!list.Any(other => ReferenceEquals(other, entry))) list.Add(entry);
        }
        foreach (var entry in entries)
        {
            if (entry.A.Contains('.')) continue;
            Add(bySite, Site(entry.H), entry);
            foreach (var name in new[] { entry.N, entry.A, entry.P }) Add(byName, Key(name), entry);
        }
        var compare = Comparer<string>.Create(Nix.JsText.LocaleCompare);
        var matched = new Dictionary<string, AppInfo>();
        foreach (var app in apps)
        {
            string?[] names = [Key(app.Name), Key(app.Id.EndsWith(".desktop", StringComparison.Ordinal) ? app.Id[..^8].Split('.')[^1] : app.Id.Split('.')[^1])];
            var named = names.SelectMany(name => byName.GetValueOrDefault(name ?? "") ?? []).ToList();
            bool SameHost(CatalogEntry entry) => entry.H is null or "" || app.Homepage is null or "" || HostOf(entry.H) == HostOf(app.Homepage);
            // The plain package next to a matching variant, such as firefox for firefox-bin.
            CatalogEntry? Plain(CatalogEntry entry) => named.FirstOrDefault(other => other.A == Variant().Replace(entry.A, "", 1));
            List<CatalogEntry> bySiteOrHost = [.. bySite.GetValueOrDefault(Site(app.Homepage) ?? "") ?? [], .. named.Where(SameHost)];
            var candidates = Unique(bySiteOrHost.Concat(bySiteOrHost.Select(Plain).OfType<CatalogEntry>()));
            // When the homepages moved apart, such as discord.com and discordapp.com, the shown name must also end the
            // id, and the package's homepage name the developer or the descriptions share a word.
            if (candidates.Count == 0 && names[0] == names[1])
            {
                var about = Words($"{app.Summary ?? ""} {string.Join(' ', app.Keywords)}");
                // A developer called like the app, as in art.darkly.Darkly, says nothing.
                var developer = DeveloperLabel(app.Id);
                var owner = developer != names[0] ? developer : null;
                candidates = named.Where(entry => Key(entry.A) == names[0]
                    && ((owner is not null && Site(entry.H)?.Contains(owner, StringComparison.Ordinal) == true)
                        || Words(entry.D ?? "").Any(about.Contains))).ToList();
            }
            var best = candidates
                .Where(entry => !matched.ContainsKey(entry.A))
                .OrderBy(entry => Variant().IsMatch(entry.A) ? 1 : 0)
                .ThenBy(entry => names.Contains(Key(entry.A)) ? 0 : 1)
                .ThenBy(entry => entry.B ? 1 : 0)
                .ThenBy(entry => entry.A.Length)
                .ThenBy(entry => entry.A, compare)
                .FirstOrDefault();
            if (best is not null) matched[best.A] = app;
        }
        return matched;
    }

    /// <summary>Entries in order without repeats of the same entry (<c>new Set</c> of objects).</summary>
    private static List<CatalogEntry> Unique(IEnumerable<CatalogEntry> entries)
    {
        var seen = new HashSet<CatalogEntry>(ReferenceEqualityComparer.Instance);
        return entries.Where(seen.Add).ToList();
    }

    // --- Files and downloads ---------------------------------------------------------------------------------

    /// <summary>The saved catalogue, if it was downloaded before (<c>readAppStream</c>).</summary>
    public static AppStream? Read(Config config)
    {
        try
        {
            return ManagerJson.Deserialize<AppStream>(File.ReadAllText(AppStreamPath(config)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Downloads and saves Flathub's catalogue; it is about ten megabytes (<c>fetchAppStream</c>).</summary>
    /// <exception cref="ManagerException">Flathub did not answer with a catalogue that lists apps.</exception>
    public static async Task<AppStream> FetchAsync(Config config, INetwork network, TimeProvider clock, CancellationToken cancellationToken)
    {
        var response = await network.GetAsync(AppStreamUrl, followRedirects: true, cancellationToken).ConfigureAwait(false);
        if (!response.Ok) throw new ManagerException($"Flathub answered {response.Status} {response.Reason}.");
        string xml;
        try
        {
            await using var gzip = new GZipStream(new MemoryStream(response.Body), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, new UTF8Encoding(false));
            xml = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            throw new ManagerException(error.Message);
        }
        var result = new AppStream(Timestamps.Now(clock), ParseAppStream(xml));
        if (result.Apps.Count == 0) throw new ManagerException("Flathub's catalogue lists no apps.");
        OperationStore.CreatePrivateDirectory(Path.Combine(config.StateDir, "apps"));
        OperationStore.AtomicJson(AppStreamPath(config), ManagerJson.Serialize(result));
        return result;
    }

    /// <summary>An app's icon, downloaded once from Flathub's media server and kept (<c>appIcon</c>).</summary>
    /// <returns>The PNG, or null when the app has no icon there or the download is not a PNG.</returns>
    public static async Task<byte[]?> IconAsync(Config config, AppInfo app, INetwork network, CancellationToken cancellationToken)
    {
        var path = IconPath(config, app.Id);
        if (!ValidAppId(app.Id)) return null;
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (app.Icon?.StartsWith(IconHost, StringComparison.Ordinal) != true) return null;
        var response = await network.GetAsync(app.Icon, followRedirects: false, cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.MediaType?.StartsWith("image/png", StringComparison.Ordinal) != true) return null;
        var directory = Path.Combine(config.StateDir, "apps", "icons");
        OperationStore.CreatePrivateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():D}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, response.Body, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return response.Body;
    }
}
