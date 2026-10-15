using NixManager.Cli;
using NixManager.Dashboard;
using NixManager.Privileged;

// pkexec runs this program as root for the declared action; the helper must come first, before threads or
// processes start (PolkitHelper.Match resets the state pkexec inherited).
if (args.Length > 0 && args[0] == PrivilegedHelper.Verb) return await PrivilegedHelper.RunAsync(args);

// The dashboard (ui) starts the Desktop host, which must happen before any await.
if (DashboardCommand.TryParse(args, out var dashboard)) return DashboardCommand.Run(dashboard);

// The tray follows in W300-031; the command line runs here.
return await NixManagerCli.RunAsync(args);
