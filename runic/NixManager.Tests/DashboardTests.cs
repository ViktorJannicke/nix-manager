using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NixManager.Dashboard;
using NixManager.Domain;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.ViewModels;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace NixManager.Tests;

/// <summary>
/// The dashboard Window through its generated Bridges, as the frontend drives it, over a copy of the Bun
/// version's state: history, live log selection, the dry run behind a confirmation, preferences, the pages' Views,
/// observing the computer, the pending edits, and refreshing when another process writes a record.
/// </summary>
public sealed class DashboardTests : IAsyncDisposable
{
    private readonly RecordedFixtures _fixtures = new();
    private readonly RunicPathWatchTester _watching = new();
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly RunicWindowTestHost<DashboardViewModel> _window;
    private int _observations;

    public DashboardTests()
    {
        var config = TestConfig.Create(_fixtures.Root) with { StateDir = _fixtures.StateDir };
        _services = new ServiceCollection()
            .AddSingleton<IOperationService>(new OperationService(config, new FakeRunner(), new FakeUpdates(new UpdateReport { CheckedAt = "" })))
            .AddSingleton(new Observer(config, _ =>
            {
                Interlocked.Increment(ref _observations);
                return Task.FromResult(Observed);
            }))
            .AddNixManagerBackend(config)
            .AddNixManagerDashboard()
            .AddSingleton(_watching.Options)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        _window = RunicWindowTestHost.Create<DashboardViewModel>(_scope.ServiceProvider);
    }

    [Test]
    public async Task It_shows_the_checkout_and_the_operations_the_Bun_version_recorded()
    {
        var configuration = _window.Root.Snapshot().Get(vm => vm.Configuration);
        await Assert.That(configuration.GetProperty("hostKey").GetString()).IsEqualTo("test");
        await Assert.That(configuration.GetProperty("hosts").EnumerateArray().Select(host => host.GetProperty("name").GetString())).IsEquivalentTo(["test"]);

        var operations = _window.Root.View<OperationsViewModel>(vm => vm.Operations).Snapshot();
        var history = operations.Get(vm => vm.Operations).EnumerateArray().ToList();
        await Assert.That(history.Count).IsGreaterThan(10);
        await Assert.That(history.All(operation => operation.GetProperty("flake").GetString() == _fixtures.Root)).IsTrue();
        await Assert.That(operations.Get(vm => vm.Updates).GetProperty("items").GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task Selecting_an_operation_shows_its_stored_log()
    {
        var operations = _window.Root.View<OperationsViewModel>(vm => vm.Operations);
        const string id = "1790107522576-f1f493f8-ff91-4054-bbfb-c506dc96ec61";

        operations.Set(vm => vm.SelectedOperationId, id).EnsureOk();

        var log = operations.Snapshot().Read(vm => vm.Log);
        await Assert.That(log.Length).IsGreaterThan(0);
        await Assert.That(File.ReadAllText(Path.Combine(_fixtures.Operations, $"{id}.log"))).EndsWith(log);
    }

    [Test]
    public async Task A_dry_run_plans_the_confirmed_request_without_running_it()
    {
        var request = Request.Create() with { Action = Actions.Gc, Scope = Scopes.Home, Retention = 4 };
        var before = Directory.GetFiles(_fixtures.Operations, "*.json").Length;

        (await _window.Root.ExecuteAsync(vm => vm.PreviewPlanCommand, Wire(request))).EnsureOk();

        var preview = _window.Root.Snapshot().Get(vm => vm.Preview);
        await Assert.That(preview.GetProperty("request").GetProperty("retention").GetInt32()).IsEqualTo(4);
        await Assert.That(preview.GetProperty("error").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(preview.GetProperty("plan").GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("label").GetString()))
            .IsEquivalentTo(["Keep 4 home generations", "Collect unreferenced store paths"]);
        await Assert.That(Directory.GetFiles(_fixtures.Operations, "*.json").Length).IsEqualTo(before);
    }

    [Test]
    public async Task A_dry_run_reports_why_a_request_cannot_run()
    {
        (await _window.Root.ExecuteAsync(vm => vm.PreviewPlanCommand, Wire(Request.Create() with { Action = Actions.Boot, Scope = Scopes.Home }))).EnsureOk();

        var preview = _window.Root.Snapshot().Get(vm => vm.Preview);
        await Assert.That(preview.GetProperty("plan").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(preview.GetProperty("error").GetString()).IsEqualTo("boot requires system scope.");
    }

    [Test]
    public async Task A_dry_run_previews_a_review_build()
    {
        (await _window.Root.ExecuteAsync(vm => vm.PreviewPlanCommand, Wire(Request.Create() with { Action = Actions.ReviewBuild }))).EnsureOk();

        var preview = _window.Root.Snapshot().Get(vm => vm.Preview);
        await Assert.That(preview.GetProperty("plan").ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    public async Task Preferences_are_saved_with_the_ones_the_Bun_version_keeps()
    {
        File.WriteAllText(PreferencesPath, """{"detail":"simple","cleanUp":true,"later":1}""");

        (await _window.Root.ExecuteAsync(vm => vm.SavePreferencesCommand, Wire(new PreferenceChange("dark", "de")))).EnsureOk();

        var dashboard = _window.Root.Snapshot();
        await Assert.That(dashboard.Read(vm => vm.Theme)).IsEqualTo("dark");
        await Assert.That(dashboard.Read(vm => vm.Language)).IsEqualTo("de");
        await Assert.That(dashboard.Read(vm => vm.Locale)).IsEqualTo("de");
        await Assert.That(dashboard.Read(vm => vm.CleanUp)).IsTrue();
        var stored = File.ReadAllText(PreferencesPath);
        await Assert.That(stored).Contains("\"cleanUp\": true").And.Contains("\"theme\": \"dark\"").And.Contains("\"later\": 1");
    }

    [Test]
    public async Task Every_preference_of_the_Bun_version_is_saved_and_read_back()
    {
        (await _window.Root.ExecuteAsync(vm => vm.SavePreferencesCommand, Wire(new PreferenceChange(Detail: "simple", PrepareUpdates: true,
            CleanUp: true, History: true, Backup: true, AppDetails: false)))).EnsureOk();

        var dashboard = _window.Root.Snapshot();
        await Assert.That(dashboard.Read(vm => vm.Detail)).IsEqualTo("simple");
        await Assert.That(dashboard.Read(vm => vm.PrepareUpdates)).IsTrue();
        await Assert.That(dashboard.Read(vm => vm.CleanUp)).IsTrue();
        await Assert.That(dashboard.Read(vm => vm.History)).IsTrue();
        await Assert.That(dashboard.Read(vm => vm.Backup)).IsTrue();
        await Assert.That(dashboard.Read(vm => vm.AppDetails)).IsFalse();
        // The background tasks and the review workflow read the same members (BackgroundTasks, recordChange).
        var stored = JsonDocument.Parse(File.ReadAllText(PreferencesPath)).RootElement;
        foreach (var name in new[] { "prepareUpdates", "cleanUp", "history", "backup" })
            await Assert.That(stored.GetProperty(name).GetBoolean()).IsTrue();
        await Assert.That(stored.GetProperty("appDetails").GetBoolean()).IsFalse();
        await Assert.That(stored.GetProperty("detail").GetString()).IsEqualTo("simple");

        // The page sends only the preference it changes; the others stay as they are.
        (await _window.Root.ExecuteAsync(vm => vm.SavePreferencesCommand, """{"prepareUpdates":false}""")).EnsureOk();
        await Assert.That(_window.Root.Snapshot().Read(vm => vm.PrepareUpdates)).IsFalse();
        await Assert.That(_window.Root.Snapshot().Read(vm => vm.History)).IsTrue();
    }

    [Test]
    public async Task An_unknown_detail_level_is_refused()
    {
        var result = await _window.Root.ExecuteAsync(vm => vm.SavePreferencesCommand, Wire(new PreferenceChange(Detail: "everything")));

        await Assert.That(result.Ok).IsFalse();
        await Assert.That(File.ReadAllText(PreferencesPath)).DoesNotContain("everything");
    }

    [Test]
    public async Task Someone_who_ran_operations_before_starts_detailed_with_app_details_on()
    {
        // The fixtures hold earlier operation records, so the first choice written on opening is the detailed view.
        var dashboard = _window.Root.Snapshot();
        await Assert.That(dashboard.Read(vm => vm.Detail)).IsEqualTo("detailed");
        await Assert.That(dashboard.Read(vm => vm.History)).IsFalse();
        await Assert.That(dashboard.Read(vm => vm.AppDetails)).IsTrue();
        await Assert.That(dashboard.Read(vm => vm.PrepareUpdates)).IsFalse();
        await Assert.That(File.ReadAllText(PreferencesPath)).Contains("\"detail\": \"detailed\"");
    }

    [Test]
    public async Task Someone_new_starts_simple_with_a_kept_history()
    {
        using var fixtures = new RecordedFixtures();
        var config = TestConfig.Create(fixtures.Root) with { StateDir = Path.Combine(fixtures.Root, "fresh-state") };

        var preferences = DashboardPreferences.Read(config);

        await Assert.That(preferences.Detail).IsEqualTo("simple");
        await Assert.That(preferences.History).IsTrue();
        await Assert.That(preferences.Backup).IsFalse();
        await Assert.That(preferences.AppDetails).IsTrue();
        // Written, so it does not change once operations exist.
        await Assert.That(File.ReadAllText(DashboardPreferences.PathOf(config))).Contains("\"detail\": \"simple\"");
    }

    [Test]
    public async Task The_pages_connect_to_the_Windows_reviews_computer_and_software()
    {
        var reviews = _window.Root.View<ReviewsViewModel>(vm => vm.Reviews).Snapshot();
        await Assert.That(reviews.Get(vm => vm.Reviews).GetArrayLength()).IsGreaterThan(0);

        await WaitForAsync(() => _window.Root.View<SystemViewModel>(vm => vm.System).Snapshot().Get(vm => vm.System).ValueKind == JsonValueKind.Object);
        var system = _window.Root.View<SystemViewModel>(vm => vm.System).Snapshot();
        await Assert.That(system.Get(vm => vm.HomeGenerations).GetArrayLength()).IsEqualTo(2);

        var software = _window.Root.View<SoftwareViewModel>(vm => vm.Software).Snapshot();
        await Assert.That(software.Get(vm => vm.Catalog).GetProperty("state").GetString()).IsNotNull();
    }

    [Test]
    public async Task Opening_the_Window_observes_the_computer_and_reads_the_pending_edits()
    {
        await WaitForAsync(() => _observations > 0);
        await WaitForAsync(() => _window.Root.Snapshot().Get(vm => vm.Draft).ValueKind == JsonValueKind.Object);

        await Assert.That(_window.Root.Snapshot().Get(vm => vm.Draft).GetProperty("changes").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Pending_edits_another_process_saves_appear_without_a_refresh()
    {
        await WaitForAsync(() => _window.Root.Snapshot().Get(vm => vm.Draft).ValueKind == JsonValueKind.Object);
        var draft = new System.Text.Json.Nodes.JsonObject
        {
            ["flake"] = _fixtures.Root,
            ["changes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "add-package", ["scope"] = "home", ["file"] = "home.nix", ["attr"] = "pkgs.hello",
            }),
        };

        File.WriteAllText(Path.Combine(_fixtures.StateDir, "draft.json"), draft.ToJsonString());
        await _watching.NextChangeAsync();

        await WaitForAsync(() => _window.Root.Snapshot().Get(vm => vm.Draft).GetProperty("changes").GetArrayLength() == 1);
    }

    [Test]
    public async Task Pending_edits_cross_the_Bridge_as_a_union_and_are_stored_by_kind()
    {
        await WaitForAsync(() => _window.Root.Snapshot().Get(vm => vm.Draft).ValueKind == JsonValueKind.Object);
        var software = _window.Root.View<SoftwareViewModel>(vm => vm.Software);

        (await software.ExecuteAsync(vm => vm.SetDraftCommand, """
            [{"$case":"set-option","scope":"system","file":"home.nix","option":"o.p","value":{"a":[1.5,null]},"label":"L"},
             {"$case":"add-package","scope":"home","file":"home.nix","attr":"pkgs.hello"}]
            """)).EnsureOk();

        var changes = software.Snapshot().Get(vm => vm.Draft).GetProperty("changes");
        await Assert.That(changes.GetRawText()).IsEqualTo("""
            [{"$case":"set-option","scope":"system","file":"home.nix","label":"L","option":"o.p","value":{"a":[1.5,null]}},{"$case":"add-package","scope":"home","file":"home.nix","label":null,"attr":"pkgs.hello"}]
            """.Trim());
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(_fixtures.StateDir, "draft.json")))!;
        await Assert.That(saved["changes"]!.ToJsonString()).IsEqualTo("""
            [{"kind":"set-option","scope":"system","file":"home.nix","option":"o.p","value":{"a":[1.5,null]},"label":"L"},{"kind":"add-package","scope":"home","file":"home.nix","attr":"pkgs.hello"}]
            """.Trim());

        var invalid = await software.ExecuteAsync(vm => vm.SetDraftCommand, """
            [{"$case":"add-package","scope":"elsewhere","file":"home.nix","attr":"pkgs.hello","label":null}]
            """);
        await Assert.That(invalid.ErrorKind).IsEqualTo("domain-failed");
        await Assert.That(invalid.Failure!.Value.GetProperty("message").GetString()).IsEqualTo("Invalid edit: unknown scope elsewhere.");
        var unknown = await software.ExecuteAsync(vm => vm.SetDraftCommand, """[{"$case":"rename-package","scope":"home","file":"home.nix"}]""");
        await Assert.That(unknown.Ok).IsFalse();
    }

    [Test]
    public async Task An_operation_that_ends_observes_the_computer_again()
    {
        await WaitForAsync(() => _observations > 0 && _window.Root.View<SystemViewModel>(vm => vm.System).Snapshot().Get(vm => vm.System).ValueKind == JsonValueKind.Object);
        var before = _observations;
        var operations = _window.Root.View<OperationsViewModel>(vm => vm.Operations);

        (await operations.ExecuteAsync(vm => vm.StartCommand, Wire(Request.Create() with { Action = Actions.Gc, Scope = Scopes.Home, Host = "test" }))).EnsureOk();

        await WaitForAsync(() => _observations > before);
    }

    [Test]
    public async Task An_announcement_without_an_embedded_window_is_left_to_the_pages_live_region()
    {
        // The tester's Window has no native window to announce through, as in a browser; the command still succeeds.
        (await _window.Root.ExecuteAsync(vm => vm.AnnounceCommand, """{"message":"Check for updates failed"}""")).EnsureOk();
        (await _window.Root.ExecuteAsync(vm => vm.AnnounceCommand, Wire(new Announcement("Saved")))).EnsureOk();
    }

    [Test]
    public async Task A_record_another_process_writes_appears_without_a_refresh()
    {
        var operations = _window.Root.View<OperationsViewModel>(vm => vm.Operations);
        var count = operations.Snapshot().Get(vm => vm.Operations).GetArrayLength();
        var source = Directory.GetFiles(_fixtures.Operations, "*.json").Order(StringComparer.Ordinal).Last();
        var copy = File.ReadAllText(source).Replace(Path.GetFileNameWithoutExtension(source), "1799999999999-11111111-2222-4333-8444-555555555555");

        File.WriteAllText(Path.Combine(_fixtures.Operations, "1799999999999-11111111-2222-4333-8444-555555555555.json"), copy);
        await _watching.NextChangeAsync();

        await Assert.That(operations.Snapshot().Get(vm => vm.Operations).GetArrayLength()).IsEqualTo(count + 1);
    }

    [Test]
    public async Task A_record_another_process_replaces_by_renaming_is_read_again()
    {
        var operations = _window.Root.View<OperationsViewModel>(vm => vm.Operations);
        var source = Directory.GetFiles(_fixtures.Operations, "*.json").Order(StringComparer.Ordinal).Last();
        var id = Path.GetFileNameWithoutExtension(source);
        var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(source))!;
        record["step"] = "Replaced by another process";
        var temporary = $"{source}.{Guid.NewGuid():D}.tmp";

        // As OperationStore.AtomicJson writes it: the name stays, so only a watch of the record itself sees it.
        File.WriteAllText(temporary, record.ToJsonString());
        File.Move(temporary, source, overwrite: true);
        await _watching.NextChangeAsync();

        var summaries = operations.Snapshot().Get(vm => vm.Operations).EnumerateArray()
            .Where(operation => operation.GetProperty("id").GetString() == id)
            .Select(operation => operation.GetProperty("step").GetString());
        await Assert.That(summaries.Single()).IsEqualTo("Replaced by another process");
    }

    [Test]
    [Arguments("system", "de_DE.UTF-8", "de")]
    [Arguments("system", "C", "en")]
    [Arguments("system", "fr_FR.UTF-8", "en")]
    [Arguments("en", "de_DE.UTF-8", "en")]
    [Arguments("de", "en_US.UTF-8", "de")]
    public async Task The_language_is_the_chosen_one_or_the_desktops(string language, string lang, string locale) =>
        await Assert.That(DashboardPreferences.ResolveLocale(language, name => name == "LANG" ? lang : null)).IsEqualTo(locale);

    /// <summary>Runs the command line as <c>Main</c> does, with the dashboard and the daemon recorded instead of started.</summary>
    private static (int ExitCode, object? Started, string Stderr, string Stdout) Launch(params string[] args)
    {
        object? started = null;
        var console = new Runic.CommandLine.Testing.TestCommandConsole { IsOutputRedirected = true, IsErrorRedirected = true };
        var session = new Cli.CliSession
        {
            Dashboard = invocation => { started = invocation; return 7; },
            Daemon = invocation => { started = invocation; return 8; },
        };
        var code = Cli.NixManagerCli.Run(args, session, console, handleSignals: false);
        return (code, started, console.StandardError, console.StandardOutput);
    }

    [Test]
    [Arguments(new[] { "ui" }, null, null, false, false)]
    [Arguments(new[] { "--flake", "/x", "ui", "--host=laptop" }, "/x", "laptop", false, false)]
    [Arguments(new[] { "ui", "--no-open" }, null, null, true, false)]
    [Arguments(new[] { "ui", "--browser", "--plain" }, null, null, false, true)]
    public async Task A_dashboard_invocation_starts_the_Window_once_the_command_line_returned(string[] args, string? flake, string? host,
        bool noOpen, bool browser)
    {
        var (code, started, _, _) = Launch(args);
        await Assert.That(code).IsEqualTo(7);
        await Assert.That(started).IsEqualTo(new DashboardCommand.Invocation(flake, host, noOpen, browser));
    }

    [Test]
    public async Task A_tray_invocation_starts_the_daemon_once_the_command_line_returned()
    {
        var (code, started, _, _) = Launch("tray", "--interval", "30", "--flake", "/x");
        await Assert.That(code).IsEqualTo(8);
        await Assert.That(started).IsEqualTo(new Daemon.DaemonCommand.Invocation("/x", null, 30));
    }

    [Test]
    [Arguments(new[] { "ui", "--interval", "30" }, "The dashboard takes only --flake, --host, --no-open and --browser.")]
    [Arguments(new[] { "tray", "--browser" }, "The tray takes only --flake, --host and --interval.")]
    [Arguments(new[] { "ui", "home" }, "Action modifiers require a build, activation, maintenance, or package action.")]
    public async Task Other_options_with_ui_or_tray_start_nothing(string[] args, string error)
    {
        var (code, started, stderr, stdout) = Launch(args);
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(started).IsNull();
        await Assert.That(stdout).IsEqualTo(error + "\n");
        await Assert.That(stderr).IsEqualTo("NM0002: The command line is not valid.\n");
    }

    [Test]
    public async Task The_dashboard_has_no_JSON_output()
    {
        var (code, started, _, _) = Launch("ui", "--output", "json");
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(started).IsNull();
    }

    [Test]
    public async Task Help_for_ui_starts_nothing()
    {
        var (code, started, _, _) = Launch("ui", "--help");
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(started).IsNull();
    }

    [Test]
    public async Task A_browser_is_served_the_page_as_with_no_open()
    {
        await Assert.That(new DashboardCommand.Invocation(null, null, NoOpen: false, Browser: true).Served).IsTrue();
        await Assert.That(new DashboardCommand.Invocation(null, null, NoOpen: true).Served).IsTrue();
        await Assert.That(new DashboardCommand.Invocation(null, null, NoOpen: false).Served).IsFalse();
    }

    private string PreferencesPath => DashboardPreferences.PathOf(TestConfig.Create(_fixtures.Root) with { StateDir = _fixtures.StateDir });

    private static readonly SystemState Observed = new()
    {
        ObservedAt = "2026-10-15T08:00:00.000Z",
        Repository = new RepositoryState { Fingerprint = "observed", LockHash = "lock" },
        Generations = new ProfileGenerations
        {
            Home =
            [
                new Generation { Number = 2, CreatedAt = "2026-10-14T08:00:00.000Z", Output = "/h2", Current = true },
                new Generation { Number = 1, CreatedAt = "2026-10-13T08:00:00.000Z", Output = "/h1" },
            ],
        },
        Current = new CurrentOutputs { Home = "/h2" },
        Kernel = new KernelVersions(),
    };

    /// <summary>Waits for work the Window posts to its model context, such as an observation or the rendered draft.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    /// <summary>A command argument as the generated client sends it: camelCase, every member present.</summary>
    private static string Wire<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public async ValueTask DisposeAsync()
    {
        _window.Dispose();
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
        _fixtures.Dispose();
    }
}
