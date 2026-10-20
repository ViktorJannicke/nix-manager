using NixManager.Domain;

namespace NixManager.Cli;

public enum CliMode
{
    Run,
    Ui,
    Tray,
    Status,
    History,
}

/// <summary>A resolved command line.</summary>
public sealed record CliInvocation
{
    public required Request Request { get; init; }
    public string? Flake { get; init; }
    public string? Host { get; init; }
    public CliMode Mode { get; init; }
    public bool Json { get; init; }
    public bool Plain { get; init; }
    public bool Verbose { get; init; }
    public bool Browser { get; init; }
    public bool NoOpen { get; init; }
    public int Interval { get; init; } = 360;
}

/// <summary>The options the parser bound, before the tokens are resolved.</summary>
public sealed record CliOptions(
    string? Flake, string? Host, bool DryRun, bool Force, bool Json, bool Plain, bool Verbose, bool Browser, bool NoOpen, int Interval);

/// <summary>The root command's grammar: scope, modifier and action words in any order.</summary>
public static class CliArguments
{
    /// <summary>The accepted words: scopes, modifiers, actions and modes.</summary>
    public static readonly string[] Tokens =
    [
        "home", "system", "all", "update", "cleanup", "gc", "build", "switch", "boot", "rollback", "check",
        "t3code-update", "opencode-update", "pince-update", "ui", "tray", "status", "history",
    ];

    /// <summary>
    /// Resolves the words and options into one request or mode. A combination that does not make one request
    /// throws a <see cref="ManagerException"/>, a usage error.
    /// </summary>
    public static CliInvocation Resolve(IReadOnlyList<string> tokens, CliOptions options, IReadOnlyList<string> extra)
    {
        static void Fail(string message) => throw new ManagerException(message);
        var request = Request.Create() with { DryRun = options.DryRun, Force = options.Force, Extra = [.. extra] };
        if (options.Host is { Length: > 0 } host) request = request with { Host = host };
        var mode = CliMode.Run;
        bool action = false, scope = false, explicitMode = false;
        foreach (var raw in tokens)
        {
            var token = Tokens.FirstOrDefault(t => string.Equals(t, raw, StringComparison.OrdinalIgnoreCase)) ?? raw;
            switch (token)
            {
                case "home" or "system" or "all":
                    if (scope) Fail("Specify one scope.");
                    scope = true;
                    request = request with { Scope = token };
                    break;
                case "update":
                    request = request with { Update = true };
                    break;
                case "cleanup" or "gc":
                    request = request with { Cleanup = true };
                    break;
                case "ui" or "tray" or "status" or "history":
                    if (action || explicitMode) Fail("Specify one action.");
                    explicitMode = true;
                    mode = Enum.Parse<CliMode>(token, ignoreCase: true);
                    break;
                default:
                    if (!Tokens.Contains(token)) Fail($"Unknown word: {token}.");
                    if (action || explicitMode) Fail("Specify one action.");
                    action = true;
                    request = request with { Action = token };
                    break;
            }
        }
        if (options.Interval is < 5 or > 10_080) Fail("--interval must be 5–10080 minutes.");
        if (mode == CliMode.Run)
        {
            if (!action && request.Cleanup) request = request with { Action = Actions.Gc };
            else if (!action) Fail("Specify an action; see --help.");
            if (!scope && request.Action == Actions.Boot) request = request with { Scope = Scopes.System };
            RequestValidation.Validate(request);
            if (options.Browser || options.NoOpen || options.Interval != 360)
                Fail("Window and interval options require ui or tray.");
        }
        else if (scope || request.Update || request.Cleanup || request.Force || request.DryRun || request.Extra.Count > 0)
            Fail("Action modifiers require a build, activation, maintenance, or package action.");
        return new CliInvocation
        {
            Request = request,
            Flake = options.Flake,
            Host = options.Host,
            Mode = mode,
            Json = options.Json,
            Plain = options.Plain,
            Verbose = options.Verbose,
            Browser = options.Browser,
            NoOpen = options.NoOpen,
            Interval = options.Interval,
        };
    }
}
