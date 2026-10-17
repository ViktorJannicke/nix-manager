using System.Collections.Immutable;
using NixManager.Domain;
using NixManager.Operations;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace NixManager.Daemon;

/// <summary>What a later launch asks the running instance (D025.3, D025.4).</summary>
public static class DaemonRequestKinds
{
    /// <summary>Open or raise the dashboard (<c>nix-manager ui</c>).</summary>
    public const string Ui = "ui";
    /// <summary>Run an operation and stream its events back (the command line).</summary>
    public const string Operation = "operation";
    /// <summary>Become the daemon (<c>nix-manager tray</c>, the systemd user service).</summary>
    public const string Daemon = "daemon";
}

/// <summary>The configuration a request is for: an instance serves only its own.</summary>
/// <param name="Flake">The checkout.</param>
/// <param name="HostKey">The host.</param>
/// <param name="StateDir">The state directory, whose operation lock and records both processes must share.</param>
public sealed record DaemonIdentity(string Flake, string HostKey, string StateDir)
{
    public static DaemonIdentity Of(Config config) => new(config.Flake, config.HostKey, config.StateDir);
}

/// <summary>A launch forwarded to the running instance, as the one argument after <see cref="DaemonProtocol.Marker"/>.</summary>
/// <param name="Kind">One of <see cref="DaemonRequestKinds"/>.</param>
/// <param name="Identity">The configuration the launching process loaded; null when it named none (a plain <c>ui</c>).</param>
/// <param name="Request">The operation to run, for <see cref="DaemonRequestKinds.Operation"/>.</param>
public sealed record DaemonRequest(string Kind, DaemonIdentity? Identity = null, Request? Request = null);

/// <summary>One message of the running instance's reply, as one compact JSON document.</summary>
/// <remarks>
/// An operation's reply is its <see cref="ManagerEvent"/> sequence as the operation service published it, then its
/// final record. The launching process feeds them to the same presentation as a run in its own process, so the
/// command line prints the same output either way.
/// </remarks>
public sealed record DaemonMessage
{
    /// <summary><c>event</c>, <c>result</c>, <c>error</c>, <c>elsewhere</c> or <c>note</c>.</summary>
    public required string Kind { get; init; }
    public ManagerEvent? Event { get; init; }
    public Operation? Operation { get; init; }
    public string? Message { get; init; }
    public Explanation? Explanation { get; init; }

    public static DaemonMessage Of(ManagerEvent e) => new() { Kind = "event", Event = e };
    public static DaemonMessage Result(Operation operation) => new() { Kind = "result", Operation = operation };
    public static DaemonMessage Error(ManagerException error) => new() { Kind = "error", Message = error.Message, Explanation = error.Explanation };
    /// <summary>The instance runs another configuration; the launching process runs the request itself.</summary>
    public static DaemonMessage Elsewhere(string why) => new() { Kind = "elsewhere", Message = why };
    /// <summary>A line for the launching process to print on standard error.</summary>
    public static DaemonMessage Note(string text) => new() { Kind = "note", Message = text };
}

/// <summary>The single-instance protocol between the daemon and later launches.</summary>
public static class DaemonProtocol
{
    /// <summary>The application ID that names the instance socket; the Polkit action's.</summary>
    public const string ApplicationId = "org.nixos.nix-manager";

    /// <summary>The first argument of a forwarded request; the second is the <see cref="DaemonRequest"/> as JSON.</summary>
    public const string Marker = "--nix-manager-daemon-request";

    /// <summary>Set to <c>0</c> to never hand work to a running instance (and never become one from <c>ui</c>).</summary>
    public const string Switch = "NIX_MANAGER_DAEMON";

    /// <summary>The exit status of a reply whose request the launching process should run itself.</summary>
    public const int RunHere = 75;

    /// <summary>Every process answers launches and waits for replies (D025.4).</summary>
    public static ApplicationInstanceOptions Options { get; } = new() { ReplyToLaunches = true };

    public static bool Disabled(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(Switch) == "0";

    public static ApplicationLaunch Launch(DaemonRequest request, DesktopActivationContext? context = null) =>
        new(ImmutableArray.Create(Marker, ManagerJson.Serialize(request, ManagerJson.Compact)), Environment.CurrentDirectory)
        {
            PlatformContext = context ?? ApplicationLaunch.FromProcess([]).PlatformContext,
        };

    /// <summary>The request a launch carries, or null for a launch from elsewhere, such as an older build.</summary>
    public static DaemonRequest? Parse(ApplicationLaunch launch)
    {
        if (launch.Arguments is not [Marker, var json]) return null;
        try { return ManagerJson.Deserialize<DaemonRequest>(json, ManagerJson.Compact); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static string Serialize(DaemonMessage message) => ManagerJson.Serialize(message, ManagerJson.Compact);

    public static DaemonMessage? ParseMessage(string json)
    {
        try { return ManagerJson.Deserialize<DaemonMessage>(json, ManagerJson.Compact); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Claims the instance for this process, or forwards <paramref name="request"/> to the process that holds it.
    /// Null when there is no instance to use: no per-user runtime directory, the switch is off, or an instance that
    /// does not reply (an older build) holds it.
    /// </summary>
    public static async Task<ApplicationInstanceClaim?> ClaimAsync(DaemonRequest request, CancellationToken cancellationToken = default)
    {
        if (Disabled()) return null;
        var claim = await ApplicationInstance.ClaimAsync(ApplicationId, Launch(request), Options, cancellationToken).ConfigureAwait(false);
        return claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: var value } ? value : null;
    }
}
