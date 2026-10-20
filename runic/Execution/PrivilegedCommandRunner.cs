using System.Collections.Immutable;
using NixManager.Domain;
using Runic.Platform;

namespace NixManager.Execution;

/// <summary>
/// Runs plan steps that start with the privileged entry point (<see cref="Config.Privileged"/>) through the
/// declared Polkit action, and every other step with <paramref name="inner"/>.
/// </summary>
/// <remarks>
/// The plan keeps the argument vector <c>pkexec PROGRAM --privileged COMMAND ARGUMENT...</c>, so a
/// dry run reads the same. Running it, Runic's provider starts pkexec with the declared program and the verb
/// <c>--privileged</c>, an argument vector and a minimal environment. A build out-link is resolved to its store
/// path first, which the helper checks again.
/// <para>
/// Cancellation while Polkit asks for authorization stops the request. Once the helper runs as root it cannot be
/// signalled: cancellation closes its standard input, which the helper ignores, so the step finishes and the
/// operation stops before the next one.
/// </para>
/// </remarks>
public sealed class PrivilegedCommandRunner(Config config, IDesktopPrivilegedActions actions, PrivilegedActionDeclaration declaration, ICommandRunner inner) : ICommandRunner
{
    public static bool IsPrivileged(Config config, Command command) =>
        command.Argv.Count > config.Privileged.Count && command.Argv.Take(config.Privileged.Count).SequenceEqual(config.Privileged);

    public async Task<CommandResult> RunAsync(Command command, Action<OutputEvent> output, CancellationToken cancellationToken)
    {
        if (!IsPrivileged(config, command)) return await inner.RunAsync(command, output, cancellationToken).ConfigureAwait(false);
        var arguments = command.Argv.Skip(config.Privileged.Count).ToArray();
        // The caller passes a build out-link; the declaration and the helper accept only its store path.
        if (arguments is ["activate-output", _, var link] && Operations.Native.RealPath(link) is { } storePath)
            arguments[2] = storePath;
        if (!declaration.AcceptsArguments([.. arguments]))
            throw new ManagerException(DisplayText.Of("stored_the_privileged_helper_does_not_accept_these", ("label", command.Label)));
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var request = new PrivilegedActionRequest(declaration, [.. arguments])
        {
            Output = line =>
            {
                var error = line.Channel == PrivilegedOutputChannel.StandardError;
                var text = line.Text + "\n";
                lock (stdout)
                {
                    if (error) stderr.Append(text);
                    else stdout.Append(text);
                }
                if (command.Quiet != true) output(new TextOutput(error ? "stderr" : "stdout", text));
            },
        };
        var result = await actions.RunAsync(request, cancellationToken).ConfigureAwait(false);
        string Tail()
        {
            lock (stdout)
            {
                var text = stderr.ToString();
                return (text.Length > 1500 ? text[^1500..] : text).Trim();
            }
        }
        switch (result)
        {
            case PlatformResult<PrivilegedActionCompletion>.Success { Value.Succeeded: true }:
                return new CommandResult(0, stdout.ToString(), stderr.ToString());
            case PlatformResult<PrivilegedActionCompletion>.Success(var completion):
                if (command.AllowFailure == true) return new CommandResult(completion.ExitCode, stdout.ToString(), stderr.ToString());
                var tail = Tail();
                throw new ManagerException(
                    DisplayText.Of("stored_failed_exit", ("label", command.Label), ("code", completion.ExitCode), ("output", DisplayText.Plain(tail))),
                    Explain.Explainer.Explain(stderr.ToString()));
            // The texts are pkexec's own, which the explanations of failures recognise.
            case PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.UserDismissed }:
                throw Failure(command, 126, "Error executing command as another user: Request dismissed");
            case PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied }:
                throw Failure(command, 127, "Error executing command as another user: Not authorized");
            case PlatformResult<PrivilegedActionCompletion>.Unavailable unavailable:
                throw new ManagerException(DisplayText.Of("stored_polkit_is_unavailable_an_authentication_agent_and",
                    ("label", command.Label), ("reason", DisplayText.Plain($"{unavailable.Reason}"))));
            case PlatformResult<PrivilegedActionCompletion>.Failed failed:
                throw new ManagerException(DisplayText.Of("stored_privileged_helper_could_not_run", ("label", command.Label),
                    ("reason", DisplayText.Plain($"{failed.Code}{(failed.Diagnostic?.Message is { } message ? $": {message}" : "")}"))));
            default:
                throw new ManagerException(DisplayText.Of("stored_privileged_unexpected_result", ("label", command.Label), ("result", DisplayText.Plain($"{result}"))));
        }
    }

    private static ManagerException Failure(Command command, int code, string text) =>
        new(DisplayText.Of("stored_failed_exit", ("label", command.Label), ("code", code), ("output", DisplayText.Plain(text))), Explain.Explainer.Explain(text));
}
