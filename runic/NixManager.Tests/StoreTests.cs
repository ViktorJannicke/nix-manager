using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Operations;
using NixManager.Software;
using NixManager.Updates;
using NixManager.ViewModels;
using Runic.Application.Views;
using Runic.Navigation;

namespace NixManager.Tests;

/// <summary>Answers Flathub's addresses from a table and counts the requests.</summary>
internal sealed class FlathubNetwork : INetwork
{
    public Dictionary<string, NetworkResponse> Answers { get; } = [];
    public List<string> Requests { get; } = [];
    /// <summary>Holds every answer until it is set, to observe a download in progress.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public Task<string> TextAsync(string url, CancellationToken cancellationToken) => throw new NotSupportedException(url);

    public async Task<NetworkResponse> GetAsync(string url, bool followRedirects, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(url);
        if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        return Answers.TryGetValue(url, out var answer) ? answer : throw new ManagerException("Fetch failed: connection refused");
    }

    public static NetworkResponse Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return new NetworkResponse(200, "OK", "application/x-gzip", buffer.ToArray());
    }
}

/// <summary>A clock the test moves.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The software store's parts beyond the catalog (<c>src/apps.ts</c>, <c>src/hardware.ts</c>, the apps and curated
/// options of <c>src/store.ts</c>), compared with the Bun version's results in <c>Fixtures/bun-store.json</c>, which
/// <c>record-bun-store.ts</c> records, and the Software page's ViewModel over them.
/// </summary>
public sealed class StoreTests : IDisposable
{
    private static readonly JsonNode Recorded = JsonNode.Parse(File.ReadAllText(Path.Combine(BunFixtures.Directory, "bun-store.json")))!;
    private static string Sample => File.ReadAllText(Path.Combine(BunFixtures.Directory, "appstream-sample.xml"));
    private const string Hash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private readonly string _root = Directory.CreateTempSubdirectory("nix-manager-apps-").FullName;
    private readonly FlathubNetwork _network = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Config Config => TestConfig.Create(_root);

    private static JsonNode Json<T>(T value) => JsonNode.Parse(ManagerJson.Serialize(value))!;

    [Test]
    public async Task AppStream_parsing_matches_the_Bun_version()
    {
        var parsed = Json(new AppStream("", Apps.ParseAppStream(Sample)))["apps"]!;
        await Assert.That(JsonNode.DeepEquals(parsed, Recorded["parseAppStream"])).IsTrue()
            .Because($"C#: {parsed.ToJsonString()}");
    }

    [Test]
    public async Task Apps_are_matched_to_packages_as_the_Bun_version_matches_them()
    {
        var recorded = Recorded["matchApps"]!;
        var entries = ManagerJson.Deserialize<List<CatalogEntry>>(recorded["entries"]!.ToJsonString())!;
        var apps = recorded["apps"]!.AsArray().Select(app => ManagerJson.Deserialize<AppStream>(
            new JsonObject { ["fetchedAt"] = "", ["apps"] = new JsonArray(app!.DeepClone()) }.ToJsonString())!.Apps[0]).ToList();
        var matched = Apps.Match(apps, entries).ToDictionary(pair => pair.Key, pair => pair.Value.Id);
        var expected = recorded["result"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>());
        await Assert.That(matched).IsEquivalentTo(expected);
    }

    [Test]
    public async Task App_ids_that_name_files_are_the_Bun_version_s()
    {
        foreach (var item in Recorded["validAppId"]!.AsArray())
            await Assert.That(Apps.ValidAppId(item!["id"]!.GetValue<string>())).IsEqualTo(item["result"]!.GetValue<bool>())
                .Because(item["id"]!.GetValue<string>());
    }

    [Test]
    public async Task Hardware_is_detected_as_the_Bun_version_detects_it()
    {
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(BunFixtures.Directory, "hardware-cases.json")))!.AsArray();
        var results = Recorded["detectHardware"]!.AsArray();
        for (var i = 0; i < cases.Count; i++)
        {
            var root = Path.Combine(_root, $"sysfs-{i}");
            Directory.CreateDirectory(root);
            foreach (var directory in cases[i]!["directories"]?.AsArray() ?? [])
                Directory.CreateDirectory(Path.Combine(root, directory!.GetValue<string>()));
            foreach (var (path, text) in cases[i]!["files"]!.AsObject())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
                File.WriteAllText(Path.Combine(root, path), text!.GetValue<string>());
            }
            var detected = JsonNode.Parse(JsonSerializer.Serialize(Hardware.Detect(root), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await Assert.That(JsonNode.DeepEquals(detected, results[i]!["result"])).IsTrue().Because(cases[i]!["name"]!.GetValue<string>());
        }
    }

    [Test]
    public async Task The_inventory_reads_the_Bun_version_s_curated_options()
    {
        foreach (var scope in new[] { Edits.Targets.Home, Edits.Targets.System })
        {
            var expected = Recorded["curatedOptions"]![scope]!.AsArray().Select(option => option!.GetValue<string>()).ToList();
            await Assert.That(CuratedOptions.For(scope)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task The_catalogue_is_downloaded_kept_and_not_tried_again_for_an_hour_after_a_failure()
    {
        var service = new SoftwareService(Config, new FakeRunner(), network: _network, clock: _clock);
        var changed = new SemaphoreSlim(0);
        service.Changed += () => changed.Release();
        await Assert.That(service.AppsStatus(false).State).IsEqualTo("off");
        await Assert.That(service.AppsStatus(true).State).IsEqualTo("missing");

        // Flathub is not reachable: the failure is kept, and only a forced download tries again within the hour.
        service.FetchApps();
        await Until(changed, () => service.AppsStatus(true).State != "fetching");
        var failed = service.AppsStatus(true);
        await Assert.That(failed.State).IsEqualTo("failed");
        await Assert.That(failed.Error).IsEqualTo("App details could not be downloaded from Flathub. Fetch failed: connection refused");
        service.FetchApps();
        await Assert.That(_network.Requests.Count).IsEqualTo(1);
        _clock.Now += TimeSpan.FromMinutes(61);
        _network.Answers[Apps.AppStreamUrl] = FlathubNetwork.Gzip(Sample);
        _network.Gate = new TaskCompletionSource();
        service.FetchApps();
        await Assert.That(service.AppsStatus(true).State).IsEqualTo("fetching");
        _network.Gate.SetResult();
        await Until(changed, () => service.AppsStatus(true).State != "fetching");

        var ready = service.AppsStatus(true);
        await Assert.That(ready.State).IsEqualTo("ready");
        await Assert.That(ready.Error).IsNull();
        await Assert.That(ready.FetchedAt).IsEqualTo("2026-10-15T13:01:00.000Z");
        // The same file the Bun version writes and reads.
        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(Config.StateDir, "apps", "appstream.json")))!;
        await Assert.That(stored["fetchedAt"]!.GetValue<string>()).IsEqualTo("2026-10-15T13:01:00.000Z");
        await Assert.That(JsonNode.DeepEquals(stored["apps"], Recorded["parseAppStream"])).IsTrue();

        // A week-old catalogue is downloaded again; a newer one is not.
        service.FetchApps();
        await Assert.That(_network.Requests.Count).IsEqualTo(2);
        var fresh = new SoftwareService(Config, new FakeRunner(), network: _network, clock: _clock);
        await Assert.That(fresh.AppsStatus(true).FetchedAt).IsEqualTo("2026-10-15T13:01:00.000Z");
        _clock.Now += TimeSpan.FromDays(7);
        _network.Answers.Remove(Apps.AppStreamUrl);
        fresh.FetchApps();
        await WaitIdle(fresh);
        // A failed download keeps the older catalogue and reports why.
        await Assert.That(fresh.AppsStatus(true).State).IsEqualTo("ready");
        await Assert.That(fresh.AppsStatus(true).Error).IsNotNull();
    }

    [Test]
    public async Task Search_lookup_and_the_app_list_carry_the_app_details()
    {
        var service = await ReadyService();
        var gimp = service.Search(Hash, "image manipulation").First();
        await Assert.That(gimp.A).IsEqualTo("gimp");
        await Assert.That(gimp.App!.Id).IsEqualTo("org.gimp.GIMP");
        // Found by Flathub's name and keywords too.
        await Assert.That(service.Search(Hash, "browser").First().App!.Name).IsEqualTo("Firefox");
        var lookup = service.Lookup(Hash, ["vlc", "ripgrep", "missing"]);
        await Assert.That(lookup.Keys).IsEquivalentTo(["vlc", "ripgrep"]);
        await Assert.That(lookup["vlc"].App!.Id).IsEqualTo("org.videolan.VLC");
        await Assert.That(lookup["ripgrep"].App).IsNull();
        // Catalog order, only apps.
        await Assert.That(service.AppList(Hash).Select(entry => entry.A))
            .IsEquivalentTo(["firefox", "discord", "sioyek", "gimp", "keepassxc", "vlc"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(service.AppList(new string('e', 64))).IsEmpty();
    }

    [Test]
    public async Task Icons_are_downloaded_once_kept_and_only_from_Flathub_as_PNG()
    {
        var service = await ReadyService();
        const string url = "https://dl.flathub.org/media/org/mozilla/firefox/icons/128x128/org.mozilla.firefox.png";
        _network.Answers[url] = new NetworkResponse(200, "OK", "image/png", [1, 2, 3]);
        await Assert.That(await service.IconAsync("org.mozilla.firefox")).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(File.ReadAllBytes(Apps.IconPath(Config, "org.mozilla.firefox"))).IsEquivalentTo(new byte[] { 1, 2, 3 });
        _network.Answers.Remove(url);
        await Assert.That(await service.IconAsync("org.mozilla.firefox")).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(_network.Requests.Count(request => request == url)).IsEqualTo(1);

        // Not a PNG, no icon in the catalogue, an unknown or unsafe id.
        _network.Answers["https://dl.flathub.org/repo/appstream/x86_64/icons/128x128/io.github.ahrm.sioyek.png"] = new NetworkResponse(200, "OK", "text/html", [60]);
        await Assert.That(await service.IconAsync("io.github.ahrm.sioyek")).IsNull();
        await Assert.That(File.Exists(Apps.IconPath(Config, "io.github.ahrm.sioyek"))).IsFalse();
        await Assert.That(await service.IconAsync("art.darkly.Darkly")).IsNull();
        await Assert.That(await service.IconAsync("org.example.Unknown")).IsNull();
        await Assert.That(await service.IconAsync("../org.mozilla.firefox")).IsNull();
    }

    [Test]
    public async Task The_Software_page_reads_hardware_apps_options_and_icons_through_its_ViewModel()
    {
        var service = await ReadyService();
        var firefoxIcon = "https://dl.flathub.org/media/org/mozilla/firefox/icons/128x128/org.mozilla.firefox.png";
        _network.Answers[firefoxIcon] = new NetworkResponse(200, "OK", "image/png", [137, 80, 78, 71]);
        var hardware = new HardwareFeatures(Laptop: true, Nvidia: false, Bluetooth: true, Printer: false, Fingerprint: false);
        await using var context = new RunicModelContext();
        using var viewModel = await context.InvokeAsync(() => new SoftwareViewModel(service, context, hardware, "alice"));
        await Assert.That(viewModel.User).IsEqualTo("alice");
        await Assert.That(viewModel.Hardware).IsEqualTo(hardware);

        await context.InvokeAsync(() => viewModel.LockHash = Hash);
        await Assert.That(await context.InvokeAsync(() => viewModel.Catalog.State)).IsEqualTo("ready");
        await Assert.That(await context.InvokeAsync(() => viewModel.AppsStatus.State)).IsEqualTo("ready");
        await context.InvokeAsync(() => viewModel.Query = "firefox");
        await Assert.That(await context.InvokeAsync(() => viewModel.Results[0].App?.Id)).IsEqualTo("org.mozilla.firefox");
        await Assert.That(await context.InvokeAsync(() => viewModel.Searched)).IsEqualTo("firefox");

        await context.InvokeAsync(() => viewModel.LoadAppsCommand.Execute(false));
        await Assert.That(await context.InvokeAsync(() => viewModel.Apps.Entries.Count)).IsEqualTo(6);
        await context.InvokeAsync(() => viewModel.LookupPackagesCommand.Execute(new[] { "gimp", "nope" }));
        var lookup = await context.InvokeAsync(() => viewModel.Lookup);
        await Assert.That(lookup!.Attrs).IsEquivalentTo(["gimp", "nope"]);
        await Assert.That(lookup.Entries["gimp"].App!.Name).IsEqualTo("GNU Image Manipulation Program");

        await await context.InvokeAsync(() => viewModel.Icons.LoadCommand.ExecuteAsync(new[] { "org.mozilla.firefox", "art.darkly.Darkly", "../x" }));
        var icons = await context.InvokeAsync(() => viewModel.Icons.Icons);
        await Assert.That(icons["org.mozilla.firefox"]).IsEqualTo("data:image/png;base64,iVBORw==");
        await Assert.That(icons["art.darkly.Darkly"]).IsEqualTo("");
        await Assert.That(icons.ContainsKey("../x")).IsFalse();

        // Without a lock hash there is no catalog to build; the page hears why.
        await context.InvokeAsync(() => viewModel.LockHash = null);
        var refused = await context.InvokeAsync(() =>
        {
            try
            {
                viewModel.BuildCatalogCommand.Execute(false);
                return null;
            }
            catch (RunicFailureException failure)
            {
                return failure.Failure;
            }
        });
        await Assert.That(refused).IsEqualTo(new SoftwareFailure("The checkout has not been read yet."));
    }

    [Test]
    public async Task Turning_app_details_off_hides_them_from_the_page()
    {
        var service = await ReadyService();
        Directory.CreateDirectory(Config.StateDir);
        File.WriteAllText(Path.Combine(Config.StateDir, "preferences.json"), """{"detail":"simple","appDetails":false}""");
        await Assert.That(service.AppDetails).IsFalse();
        await using var context = new RunicModelContext();
        using var viewModel = await context.InvokeAsync(() => new SoftwareViewModel(service, context, new HardwareFeatures(false, false, false, false, false), "alice"));
        await context.InvokeAsync(() => viewModel.LockHash = Hash);
        await context.InvokeAsync(() => viewModel.LoadAppsCommand.Execute(false));
        await Assert.That(await context.InvokeAsync(() => viewModel.AppsStatus.State)).IsEqualTo("off");
        await Assert.That(await context.InvokeAsync(() => viewModel.Apps.Entries.Count)).IsEqualTo(0);
        await await context.InvokeAsync(() => viewModel.Icons.LoadCommand.ExecuteAsync(new[] { "org.mozilla.firefox" }));
        await Assert.That(await context.InvokeAsync(() => viewModel.Icons.Icons.Count)).IsEqualTo(0);
        await Assert.That(_network.Requests).IsEmpty();
    }

    /// <summary>A service with the recorded catalog loaded for <see cref="Hash"/> and the sample catalogue saved.</summary>
    private async Task<SoftwareService> ReadyService()
    {
        var config = Config;
        Directory.CreateDirectory(Path.GetDirectoryName(Catalog.PathFor(config, Hash))!);
        File.WriteAllText(Catalog.PathFor(config, Hash), Recorded["matchApps"]!["entries"]!.ToJsonString());
        Directory.CreateDirectory(Path.Combine(config.StateDir, "apps"));
        File.WriteAllText(Apps.AppStreamPath(config),
            new JsonObject { ["fetchedAt"] = "2026-10-15T11:00:00.000Z", ["apps"] = Recorded["parseAppStream"]!.DeepClone() }.ToJsonString());
        var service = new SoftwareService(config, new FakeRunner(), network: _network, clock: _clock);
        var changed = new SemaphoreSlim(0);
        service.Changed += () => changed.Release();
        service.CatalogStatus(Hash);
        await Until(changed, () => service.CatalogStatus(Hash).State == "ready");
        return service;
    }

    private static async Task Until(SemaphoreSlim changed, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await changed.WaitAsync(TimeSpan.FromMilliseconds(100));
        }
    }

    private static async Task WaitIdle(SoftwareService service)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (service.AppsStatus(true).State == "fetching")
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
