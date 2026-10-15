using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NixManager.Dashboard;
using NixManager.Domain;
using NixManager.Operations;
using NixManager.ViewModels;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace NixManager.Tests;

/// <summary>
/// The dashboard Window through its generated Bridges, as the frontend drives it, over a copy of the Bun
/// version's state: history, live log selection, the dry run behind a confirmation, preferences, and refreshing
/// when another process writes a record.
/// </summary>
public sealed class DashboardTests : IAsyncDisposable
{
    private readonly BunFixtures _fixtures = new();
    private readonly RunicPathWatchTester _watching = new();
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly RunicWindowTestHost<DashboardViewModel> _window;

    public DashboardTests()
    {
        var config = TestConfig.Create(_fixtures.Root) with { StateDir = _fixtures.StateDir };
        _services = new ServiceCollection()
            .AddSingleton<IOperationService>(new OperationService(config, new FakeRunner(), new FakeUpdates(new UpdateReport { CheckedAt = "" })))
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
    public async Task A_dry_run_reports_what_this_version_cannot_run()
    {
        (await _window.Root.ExecuteAsync(vm => vm.PreviewPlanCommand, Wire(Request.Create() with { Action = Actions.ReviewBuild }))).EnsureOk();

        var preview = _window.Root.Snapshot().Get(vm => vm.Preview);
        await Assert.That(preview.GetProperty("plan").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(preview.GetProperty("error").GetString()).IsNotNull();
    }

    [Test]
    public async Task Preferences_are_saved_with_the_ones_the_Bun_version_keeps()
    {
        File.WriteAllText(DashboardPreferences.PathOf(TestConfig.Create(_fixtures.Root) with { StateDir = _fixtures.StateDir }),
            """{"detail":"simple","cleanUp":true}""");

        (await _window.Root.ExecuteAsync(vm => vm.SavePreferencesCommand, Wire(new PreferenceChange("dark", "de")))).EnsureOk();

        var dashboard = _window.Root.Snapshot();
        await Assert.That(dashboard.Read(vm => vm.Theme)).IsEqualTo("dark");
        await Assert.That(dashboard.Read(vm => vm.Language)).IsEqualTo("de");
        await Assert.That(dashboard.Read(vm => vm.Locale)).IsEqualTo("de");
        var stored = File.ReadAllText(Path.Combine(_fixtures.StateDir, "preferences.json"));
        await Assert.That(stored).Contains("\"cleanUp\": true").And.Contains("\"theme\": \"dark\"");
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
    [Arguments("system", "de_DE.UTF-8", "de")]
    [Arguments("system", "C", "en")]
    [Arguments("system", "fr_FR.UTF-8", "en")]
    [Arguments("en", "de_DE.UTF-8", "en")]
    [Arguments("de", "en_US.UTF-8", "de")]
    public async Task The_language_is_the_chosen_one_or_the_desktops(string language, string lang, string locale) =>
        await Assert.That(DashboardPreferences.ResolveLocale(language, name => name == "LANG" ? lang : null)).IsEqualTo(locale);

    [Test]
    [Arguments(new[] { "ui" }, true)]
    [Arguments(new[] { "--flake", "/x", "ui", "--host=laptop" }, true)]
    [Arguments(new[] { "ui", "--no-open" }, true)]
    [Arguments(new[] { "ui", "--help" }, false)]
    [Arguments(new[] { "home", "switch" }, false)]
    [Arguments(new[] { "ui", "--json" }, false)]
    public async Task Only_a_dashboard_invocation_starts_the_Window(string[] args, bool dashboard) =>
        await Assert.That(DashboardCommand.TryParse(args, out _)).IsEqualTo(dashboard);

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
