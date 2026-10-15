using NixManager.Cli;
using NixManager.Daemon;
using NixManager.Dashboard;
using NixManager.Privileged;

// pkexec runs this program as root for the declared action; the helper must come first, before threads or
// processes start (PolkitHelper.Match resets the state pkexec inherited).
if (args.Length > 0 && args[0] == PrivilegedHelper.Verb) return await PrivilegedHelper.RunAsync(args);

// The daemon (tray) and the dashboard (ui) start the Desktop host, which must happen before any await.
if (DaemonCommand.TryParse(args, out var daemon)) return DaemonCommand.Run(daemon);
if (DashboardCommand.TryParse(args, out var dashboard)) return DashboardCommand.Run(dashboard);

// The command line runs here; its operations go to the daemon when one runs (D025.4).
return await NixManagerCli.RunAsync(args);
