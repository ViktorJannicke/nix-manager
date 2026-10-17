using Microsoft.Extensions.DependencyInjection;
using NixManager.Configuration;
using NixManager.Dashboard;
using NixManager.Daemon;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.ViewModels;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

namespace NixManager.Setup;

/// <summary>The setup assistant's Window: its own page, <c>setup.html</c>, beside the dashboard's.</summary>
public sealed partial class SetupWindow : RunicWindow<SetupViewModel>;

/// <summary>The decisions of <c>nix-manager ui</c> about the setup assistant (<c>setUp</c> and <c>main</c> in <c>src/main.ts</c>).</summary>
public static class SetupLaunch
{
    /// <summary>The checkout <c>ui</c> would manage (<c>selectCheckout</c>).</summary>
    public static (string Path, bool Chosen) Checkout(string? flake, ICommandRunner? runner = null, ManagerEnvironment? environment = null) =>
        new ConfigLoader(runner ?? new ProcessCommandRunner(), environment).SelectCheckoutAsync(flake).GetAwaiter().GetResult();

    /// <summary>
    /// Without any checkout the dashboard starts with the setup assistant: none was asked for, by <c>--flake</c> or
    /// <c>NIX_MANAGER_FLAKE</c>, and the folder that would be managed has no <c>flake.nix</c>.
    /// </summary>
    public static bool NeedsSetup((string Path, bool Chosen) checkout) =>
        !checkout.Chosen && !File.Exists(Path.Combine(checkout.Path, "flake.nix"));

    /// <inheritdoc cref="NeedsSetup(ValueTuple{string, bool})"/>
    public static bool NeedsSetup(string? flake, ICommandRunner? runner = null, ManagerEnvironment? environment = null) =>
        NeedsSetup(Checkout(flake, runner, environment));

    /// <summary>
    /// Whether a configuration that does not load is shown in the assistant's Window: the dashboard was started from
    /// the menu, where an error on the terminal would go unseen (standard output is not a terminal), and was asked
    /// to open a Window (not <c>--no-open</c>).
    /// </summary>
    public static bool ShowsProblem(bool noOpen, bool outputRedirected) => !noOpen && outputRedirected;
}

/// <summary>
/// Hosts the setup assistant (<see cref="SetupAssistantService"/>, replacing <c>src/setup-server.ts</c>) in its own
/// Window before there is a checkout, then hands over to the dashboard in the same process and on the same Desktop
/// host. The dashboard's services need a loaded configuration, so they are built only once the assistant created the
/// checkout; the assistant has a service provider of its own.
/// </summary>
public static class SetupApplication
{
    /// <summary>The page and size the assistant opens with.</summary>
    public static RunicWindowOptions WindowOptions => new() { StartPage = "setup.html", Width = 760, Height = 820 };

    /// <summary>
    /// In serve-only mode the assistant's page follows <see cref="SetupState.Next"/> to the dashboard, which it may
    /// need a moment to read, so the assistant is served this long after handing over (<c>handOver</c>).
    /// </summary>
    public static readonly TimeSpan HandOverGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>nix-manager ui</c> without a checkout: the assistant until it created one, then the dashboard as
    /// <c>ui</c> opens it, the running instance first. Closing the assistant ends it with 0, as in the Bun version.
    /// Call it before any <see langword="await"/>, since the Desktop host's event loop may need the main thread.
    /// </summary>
    /// <param name="named">The checkout or host was named, so a running instance is used only for the same configuration.</param>
    /// <param name="opened">Called with the assistant's Window once it opened, such as to open it in a browser.</param>
    public static int Run(DashboardCommand.Invocation invocation, bool named, Action<IRunicWindow>? opened = null) =>
        DesktopEventLoop.Run(DashboardApplication.HostOptions(), desktop => RunAsync(invocation, named, desktop, opened));

    /// <summary>Shows why the dashboard could not start until the Window is closed, then ends with 1.</summary>
    public static int ShowProblem(SetupProblem problem) =>
        DesktopEventLoop.Run(DashboardApplication.HostOptions(), async desktop =>
        {
            await using var session = await SetupSession.OpenAsync(desktop, problem, desktop.ShutdownRequested);
            await session.Closed;
            return 1;
        });

    private static async Task<int> RunAsync(DashboardCommand.Invocation invocation, bool named, DesktopHost desktop, Action<IRunicWindow>? opened)
    {
        await using var session = await SetupSession.OpenAsync(desktop, null, desktop.ShutdownRequested);
        using var served = new CancellationTokenSource();
        if (session.Window is { } window)
        {
            // With --browser, closing the assistant's last tab closes it, as closing its Window does, unless it is
            // creating the checkout.
            if (invocation.Browser) _ = DashboardBrowser.WatchAsync(window, () => session.Running, TimeProvider.System, served.Token);
            opened?.Invoke(window);
        }
        var created = await session.CheckoutAsync();
        await served.CancelAsync();
        if (created is null) return 0;
        // The assistant remembered the new checkout, where the configuration is now read from.
        var (config, _) = await DaemonApplication.LoadAsync(invocation.Flake, invocation.Host);
        if (config is null) return 1;
        if (invocation.Served) return await DashboardApplication.RunAsync(config, desktop, session.HandOver, invocation.Browser);
        var claim = await DaemonProtocol.ClaimAsync(new DaemonRequest(DaemonRequestKinds.Ui, named ? DaemonIdentity.Of(config) : null));
        switch (claim)
        {
            case ApplicationInstanceClaim.Forwarded { Reply: { } reply }:
                // A daemon started meanwhile, such as the tray service once the checkout existed, opens the dashboard.
                if (await DaemonApplication.FollowAsync(reply) is { } code) return code;
                break;
            case ApplicationInstanceClaim.Primary primary:
                return await DaemonApplication.RunAsync(config, primary.Instance, new DaemonSettings { Mode = DaemonMode.Ui }, desktop,
                    session.HandOver);
        }
        return await DashboardApplication.RunAsync(config, desktop, session.HandOver);
    }
}

/// <summary>The assistant's Window with its own services, from opening until it is closed or has handed over.</summary>
internal sealed class SetupSession : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SetupAssistantService _service;
    private readonly TaskCompletionSource<string> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SetupWindow? _window;
    private Task? _closed;
    private int _handedOver;

    private SetupSession(ServiceProvider provider)
    {
        _provider = provider;
        _service = provider.GetRequiredService<SetupAssistantService>();
        _service.Created += path => _created.TrySetResult(path);
    }

    /// <summary>Opens the assistant, or with <paramref name="problem"/> only why the dashboard could not start.</summary>
    public static async Task<SetupSession> OpenAsync(DesktopHost desktop, SetupProblem? problem, CancellationToken cancellationToken)
    {
        var provider = new ServiceCollection()
            .AddNixManagerSetup(problem)
            // Generated by the build: the Window and View Bridges.
            .AddRunicViews()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var session = new SetupSession(provider);
        try
        {
            session._window = await new RunicDesktopHost(provider, desktop).OpenWindowAsync<SetupWindow>(SetupApplication.WindowOptions,
                cancellationToken);
            session._closed = session._window.WaitForCloseAsync().AsTask();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>The assistant's Window while it is open.</summary>
    public IRunicWindow? Window => _window;

    /// <summary>Whether the assistant is creating the checkout.</summary>
    public bool Running => _service.State.Running;

    /// <summary>Completes when the Window closed, by the user, a signal, or after handing over.</summary>
    public Task Closed => _closed ?? Task.CompletedTask;

    /// <summary>The created checkout's path, or null when the Window was closed before.</summary>
    public async Task<string?> CheckoutAsync() =>
        await Task.WhenAny(_created.Task, Closed) == _created.Task ? await _created.Task : null;

    /// <summary>
    /// The dashboard opened (<c>handOver</c>): a served page moves on to its address by itself, and is served a little
    /// longer for that; a shown Window closes.
    /// </summary>
    public void HandOver(IRunicWindow dashboard)
    {
        if (Interlocked.Exchange(ref _handedOver, 1) == 1) return;
        if (Environment.GetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY") == "1"
            && RunicDesktopHost.GetSurface(dashboard) is { } served)
        {
            _service.HandOver(served.Url.ToString());
            _ = Task.Delay(SetupApplication.HandOverGrace).ContinueWith(_ => CloseAsync(), TaskScheduler.Default);
        }
        else _ = CloseAsync();
    }

    private async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _window, null) is { } window) await window.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        await _provider.DisposeAsync();
    }
}
