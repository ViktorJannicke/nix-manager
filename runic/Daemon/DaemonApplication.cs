using Microsoft.Extensions.DependencyInjection;
using NixManager.Cli;
using NixManager.Configuration;
using NixManager.Dashboard;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Observation;
using NixManager.Operations;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace NixManager.Daemon;

/// <summary>Hosts <see cref="DaemonCore"/> on Runic Desktop: the status icon, notifications and the dashboard Window.</summary>
public static class DaemonApplication
{
    /// <summary>
    /// Runs the running instance until it ends, then releases <paramref name="instance"/>. Call it before any
    /// <see langword="await"/>, since the Desktop host's event loop may need the main thread.
    /// </summary>
    public static int Run(Config config, IApplicationInstance? instance, DaemonSettings settings) =>
        DesktopEventLoop.Run(DashboardApplication.HostOptions(), desktop => RunAsync(config, instance, settings, desktop));

    /// <summary>
    /// Runs the running instance on a Desktop host that runs already, as after the setup assistant created the checkout
    /// (<see cref="Setup.SetupApplication"/>), until it ends; then releases <paramref name="instance"/>.
    /// </summary>
    /// <param name="opened">Called with the dashboard Window each time it opens.</param>
    public static async Task<int> RunAsync(Config config, IApplicationInstance? instance, DaemonSettings settings, DesktopHost desktop,
        Action<IRunicWindow>? opened = null)
    {
        var provider = new ServiceCollection()
            .AddNixManagerBackend(config)
            .AddNixManagerDashboard()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        // On Linux the icon is a StatusNotifierItem and notifications go through the desktop portal, without GTK. The
        // dashboard alone (DaemonMode.Ui) shows no icon, as the Bun version's did not.
        var icon = settings.Mode == DaemonMode.Tray
            ? PlatformServices.CreateStatusIcon()
            : DesktopStatusIcons.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        var notifications = PlatformServices.CreateNotifications();
        try
        {
            var core = new DaemonCore(provider.GetRequiredService<IOperationService>(), provider.GetRequiredService<BackgroundTasks>(),
                provider.GetRequiredService<Observer>(), icon, notifications, settings, provider.GetRequiredService<TimeProvider>());
            // Right after claiming: launches wait for the first handler, and the core queues them until its loop runs.
            if (instance is not null) instance.Launched += (_, launch) => core.OnLaunch(launch);
            if (settings.Mode == DaemonMode.Tray) Console.WriteLine("Nix Manager is running in the tray.");
            return await core.RunAsync(new DesktopDashboardWindows(new RunicDesktopHost(provider, desktop), opened), desktop.ShutdownRequested);
        }
        finally
        {
            // Released before the services: a later launch then becomes the next instance instead of reaching this one.
            if (instance is not null) await instance.DisposeAsync();
            await icon.DisposeAsync();
            await notifications.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    /// <summary>
    /// Prints what the running instance says about a forwarded launch and returns its exit status, or null when it
    /// asks this process to go ahead without it.
    /// </summary>
    internal static int? Follow(IForwardedLaunchReply reply) => FollowAsync(reply).GetAwaiter().GetResult();

    /// <inheritdoc cref="Follow"/>
    internal static async Task<int?> FollowAsync(IForwardedLaunchReply reply)
    {
        try
        {
            var elsewhere = false;
            await foreach (var text in reply.ReadMessagesAsync())
            {
                var message = DaemonProtocol.ParseMessage(text);
                if (message?.Kind == "elsewhere") elsewhere = true;
                else if (message?.Message is { } note) Console.Error.WriteLine(note);
            }
            var exit = await reply.WaitForExitAsync();
            if (exit is PlatformResult<int>.Success(var code)) return elsewhere || code == DaemonProtocol.RunHere ? null : code;
            Console.Error.WriteLine("Error: The running Nix Manager ended before it answered.");
            return 1;
        }
        finally
        {
            await reply.DisposeAsync();
        }
    }

    /// <summary>Loads the configuration as the command line does, before any event loop runs; null after reporting why not.</summary>
    internal static Config? Load(string? flake, string? host) => Load(flake, host, out _);

    /// <inheritdoc cref="Load(string?, string?)"/>
    /// <param name="error">Why it could not be loaded, as reported.</param>
    internal static Config? Load(string? flake, string? host, out string? error)
    {
        (var config, error) = LoadAsync(flake, host).GetAwaiter().GetResult();
        return config;
    }

    /// <summary>Loads the configuration as the command line does; on failure, reports why and returns the reason.</summary>
    internal static async Task<(Config? Config, string? Error)> LoadAsync(string? flake, string? host)
    {
        try
        {
            return (await new ConfigLoader(new ProcessCommandRunner()).LoadAsync(flake, host, CliSession.PrivilegedPrefix), null);
        }
        catch (ManagerException error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return (null, error.Message);
        }
    }
}

/// <summary>The dashboard Window on the Desktop host, opened once and brought forward after that.</summary>
/// <param name="opened">Called with each Window it opens, such as to close the setup assistant's.</param>
internal sealed class DesktopDashboardWindows(IRunicWindowHost host, Action<IRunicWindow>? opened = null) : IDashboardWindows
{
    private IRunicWindow? _window;
    private Task? _closed;

    public bool IsOpen => _window is not null && _closed is { IsCompleted: false };

    public async Task<bool> ShowAsync(DesktopActivationContext? context, Action closed)
    {
        if (IsOpen)
        {
            // In serve-only mode there is no presentation to bring forward; the served page stays as it is.
            if (_window!.Host is DesktopBridgeWindow<DashboardViewModel> open && Owner(open) is { } owner)
                await PlatformServices.PresentAsync(owner, context);
            return false;
        }
        if (_window is { } previous)
        {
            _window = null;
            await previous.DisposeAsync();
        }
        var window = await DashboardApplication.OpenAsync(host);
        _window = window;
        _closed = WatchAsync(window, closed);
        opened?.Invoke(window);
        return true;
    }

    private static DesktopNativeOwner? Owner(DesktopBridgeWindow<DashboardViewModel> window)
    {
        try { return window.NativeOwner; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task WatchAsync(IRunicWindow window, Action closed)
    {
        try { await window.WaitForCloseAsync(); }
        finally { closed(); }
    }

    public async ValueTask CloseAsync()
    {
        if (_window is not { } window) return;
        _window = null;
        await window.DisposeAsync();
    }
}

/// <summary>
/// <c>nix-manager tray</c> with its options, recognised before the command line runs, as <c>ui</c> is: the systemd user
/// service that runs the daemon of D025.
/// </summary>
public static class DaemonCommand
{
    /// <param name="Flake">The checkout, from <c>--flake</c>.</param>
    /// <param name="Host">The host, from <c>--host</c>.</param>
    /// <param name="Interval">Minutes between update checks, from <c>--interval</c> (5–10080).</param>
    public sealed record Invocation(string? Flake, string? Host, int Interval);

    public static bool TryParse(IReadOnlyList<string> args, out Invocation invocation)
    {
        invocation = new Invocation(null, null, 360);
        var tray = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "tray", StringComparison.OrdinalIgnoreCase) && !tray) tray = true;
            else if (arg is "--plain" or "--verbose") { }
            else if (DashboardCommand.Value(args, ref i, "--flake") is { } flake) invocation = invocation with { Flake = flake };
            else if (DashboardCommand.Value(args, ref i, "--host") is { } host) invocation = invocation with { Host = host };
            else if (DashboardCommand.Value(args, ref i, "--interval") is { } text
                && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes)
                && minutes is >= 5 and <= 10_080)
                invocation = invocation with { Interval = minutes };
            // Anything else, such as an invalid interval or --help, goes to the command line, which explains it.
            else return false;
        }
        return tray;
    }

    /// <summary>
    /// Becomes the running instance and runs the daemon until it is stopped. While a dashboard started without the
    /// daemon is the running instance, it waits for that to close; while another daemon runs, it fails.
    /// </summary>
    public static int Run(Invocation invocation)
    {
        Setup.SetupAssistant.AllowFlakes(ManagerEnvironment.Current);
        if (DaemonApplication.Load(invocation.Flake, invocation.Host) is not { } config) return 1;
        var settings = new DaemonSettings { Mode = DaemonMode.Tray, CheckInterval = TimeSpan.FromMinutes(invocation.Interval) };
        var waiting = false;
        while (true)
        {
            // No event loop runs yet: waiting here keeps the main thread for the Desktop host.
            var claim = DaemonProtocol.ClaimAsync(new DaemonRequest(DaemonRequestKinds.Daemon)).GetAwaiter().GetResult();
            switch (claim)
            {
                case ApplicationInstanceClaim.Primary primary:
                    return DaemonApplication.Run(config, primary.Instance, settings);
                case ApplicationInstanceClaim.Forwarded { Reply: { } reply }:
                    if (DaemonApplication.Follow(reply) is { } code) return code;
                    if (!waiting) Console.Error.WriteLine("Waiting for the dashboard to close before starting the tray.");
                    waiting = true;
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                    break;
                default:
                    Console.Error.WriteLine(
                        "Without a per-user runtime directory (XDG_RUNTIME_DIR), command lines and later launches cannot reach the tray.");
                    return DaemonApplication.Run(config, null, settings);
            }
        }
    }
}
