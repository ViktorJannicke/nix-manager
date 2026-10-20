using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Operations;
using Runic.CommandLine.Processes;
using Runic.Platform.Linux.Polkit;

namespace NixManager.Privileged;

/// <summary>
/// The program pkexec runs as root: <c>nix-manager --privileged COMMAND ARGUMENT...</c>, with the commands and
/// checks listed below.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>activate-output switch|boot PATH</c>: only a NixOS system store path; sets the system profile to it
/// and runs its <c>switch-to-configuration</c>.</item>
/// <item><c>activate-generation N</c>: only an existing system generation, for going back after a failed apply.</item>
/// <item><c>rollback</c>: no arguments.</item>
/// <item><c>gc [N]</c>: a retention count from 1 to 100.</item>
/// </list>
/// The declaration (<c>NixManager.csproj</c>) admits only these commands, each with its arguments by position;
/// <see cref="PolkitHelper.Match"/> checks them and resets the process state pkexec inherited, and this class checks
/// their meaning: the resolved store path, an existing generation and the retention range. Standard input is not watched: a running step finishes when the application cancels or
/// exits.
/// </remarks>
public static partial class PrivilegedHelper
{
    public const string Verb = "--privileged";
    private const string Profile = Paths.SystemProfile;

    [GeneratedRegex("^/nix/store/[a-z0-9]{32}-nixos-system-[^/]+$")]
    private static partial Regex SystemOutput();

    /// <summary>Runs the helper with the process arguments; call it first in <c>Main</c>.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        // Match resets the process state and fails closed when this is not root for a pkexec caller.
        if (PolkitHelper.Match(PrivilegedActions.All, args, out var arguments) is null)
        {
            await Console.Error.WriteLineAsync("Error: Privileged operations require root through pkexec, with a known command.").ConfigureAwait(false);
            return 1;
        }
        try
        {
            await ExecuteAsync([.. arguments], new ProcessRunner(new LocalExecutablePolicy()), File.Exists).ConfigureAwait(false);
            return 0;
        }
        catch (ManagerException e)
        {
            // Never 126 or 127: pkexec reports dismissed and refused authorization with them. The caller shows this
            // standard error as the tool's output, in English.
            await Console.Error.WriteLineAsync($"Error: {e.Text.English}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Runs one tool with its output passed on, and fails when it does.</summary>
    private static async Task RunToolAsync(IProcessRunner runner, string[] argv)
    {
        var options = new ProcessExecutionOptions(standardOutputLimitBytes: 0, standardErrorLimitBytes: 0) { StandardInput = ProcessStandardInput.Closed };
        var result = await runner.RunAsync(new ProcessRequest(argv[0], argv[1..], options: options), new ConsoleLines(), CancellationToken.None)
            .ConfigureAwait(false);
        if (result.State != ProcessState.Exited) throw new ManagerException(DisplayText.Of("stored_cannot_start",
            ("command", argv[0]), ("error", DisplayText.Plain(result.Fault?.Message ?? result.State.ToString()))));
        if (result.ExitCode != 0) throw new ManagerException(DisplayText.Of("stored_privileged_operation_failed_exit", ("code", result.ExitCode ?? -1)));
    }

    /// <summary>The commands, after <see cref="PolkitHelper.Match"/>. Tests pass their own process runner and file checks.</summary>
    public static async Task ExecuteAsync(IReadOnlyList<string> arguments, IProcessRunner runner, Func<string, bool> exists,
        Func<string, string?>? realPath = null)
    {
        realPath ??= Native.RealPath;
        Task Run(params string[] argv) => RunToolAsync(runner, argv);
        // Like nixos-rebuild, run switch-to-configuration in its own transient unit so a restarting display
        // manager or session cannot interrupt it.
        async Task SwitchTo(string output, string mode)
        {
            const string systemdRun = "/run/current-system/sw/bin/systemd-run";
            string[] command = [$"{output}/bin/switch-to-configuration", mode];
            if (exists(systemdRun) && Directory.Exists("/run/systemd/system"))
                await Run([systemdRun, "-E", "LOCALE_ARCHIVE", "-E", "NIXOS_INSTALL_BOOTLOADER", "--collect", "--no-ask-password",
                    "--pipe", "--quiet", "--service-type=exec", "--unit=nix-manager-switch-to-configuration", .. command]).ConfigureAwait(false);
            else await Run(command).ConfigureAwait(false);
        }
        string Resolved(string path) => realPath(path) ?? throw new ManagerException(DisplayText.Of("stored_privileged_cannot_resolve", ("path", path)));

        switch (arguments)
        {
            case ["rollback"]:
                await Run("nix-env", "--profile", Profile, "--rollback").ConfigureAwait(false);
                await SwitchTo(Resolved(Profile), "switch").ConfigureAwait(false);
                break;
            case ["activate-output", var mode, var requested]:
            {
                // Only the resolved store path is trusted, and used from here on.
                var output = exists(requested) || Directory.Exists(requested) ? realPath(requested) ?? "" : "";
                if (!SystemOutput().IsMatch(output) || !exists($"{output}/bin/switch-to-configuration"))
                    throw new ManagerException(DisplayText.Of("stored_privileged_invalid_system_output"));
                await Run("nix-env", "--profile", Profile, "--set", output).ConfigureAwait(false);
                await SwitchTo(output, mode).ConfigureAwait(false);
                break;
            }
            case ["activate-generation", var text]:
            {
                // Going back after a failed activation: exactly the generation that was active before.
                var link = $"{Profile}-{text}-link";
                if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    || number < 1 || !(exists(link) || Directory.Exists(link)))
                    throw new ManagerException(DisplayText.Of("stored_privileged_invalid_system_generation"));
                await Run("nix-env", "--profile", Profile, "--switch-generation", number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await SwitchTo(Resolved(link), "switch").ConfigureAwait(false);
                break;
            }
            case ["gc", ..]:
            {
                var retention = 3;
                if (arguments.Count == 2 && (!int.TryParse(arguments[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out retention)
                    || retention is < 1 or > 100))
                    throw new ManagerException(DisplayText.Of("stored_privileged_invalid_generation_retention"));
                await Run("nix-env", "--profile", Profile, "--delete-generations", $"+{retention}").ConfigureAwait(false);
                // Like nixos-rebuild boot after collecting garbage: the boot menu lists only the kept generations,
                // so no entry starts a system the collection deletes.
                if (exists($"{Profile}/bin/switch-to-configuration")) await SwitchTo(Resolved(Profile), "boot").ConfigureAwait(false);
                await Run("nix-store", "--gc").ConfigureAwait(false);
                break;
            }
            default:
                // PolkitHelper.Match admits only the declared commands.
                throw new ManagerException(DisplayText.Of("stored_privileged_unknown_operation"));
        }
    }

    /// <summary>Passes the tools' output on to pkexec's caller, line by line.</summary>
    private sealed class ConsoleLines : IProcessOutputHandler
    {
        public async ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            var writer = line.Channel == ProcessOutputChannel.StandardError ? Console.Error : Console.Out;
            try
            {
                await writer.WriteLineAsync(line.Text).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The application is gone; the step still finishes.
            }
        }
    }
}
