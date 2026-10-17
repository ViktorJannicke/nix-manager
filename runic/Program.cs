using NixManager.Cli;
using NixManager.Privileged;

// pkexec runs this program as root for the declared action; the helper must come first, before threads or
// processes start (PolkitHelper.Match resets the state pkexec inherited).
if (args.Length > 0 && args[0] == PrivilegedHelper.Verb) return await PrivilegedHelper.RunAsync(args);

// The command line runs here, before any await: `ui` (the dashboard) and `tray` (the daemon) start the Desktop host
// on this thread once it has returned. Its operations go to the daemon when one runs (D025.4).
return NixManagerCli.Run(args);
