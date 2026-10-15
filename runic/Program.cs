using NixManager.Cli;
using NixManager.Privileged;

// pkexec runs this program as root for the declared action; the helper must come first, before threads or
// processes start (PolkitHelper.Match resets the state pkexec inherited).
if (args.Length > 0 && args[0] == PrivilegedHelper.Verb) return await PrivilegedHelper.RunAsync(args);

// The dashboard and the tray (ui, tray) follow in W300-030 and W300-031; the command line runs here.
return await NixManagerCli.RunAsync(args);
