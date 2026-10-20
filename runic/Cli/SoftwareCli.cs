using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Configuration;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Software;
using Runic.CommandLine;

namespace NixManager.Cli;

/// <summary>The kinds of software command (<c>Edit</c> in <c>src/software-cli.ts</c>).</summary>
public enum SoftwareAction
{
    Search,
    Add,
    Remove,
    Set,
    Unset,
    Pending,
    Drop,
    Clear,
    Prepare,
    Apply,
}

/// <summary>A parsed <c>software</c> command (<c>SoftwareCommand</c>).</summary>
public sealed record SoftwareCommand
{
    public required SoftwareAction Action { get; init; }
    public IReadOnlyList<string> Query { get; init; } = [];
    public int Limit { get; init; } = 20;
    public bool Free { get; init; }
    public bool Working { get; init; }
    public IReadOnlyList<string> Packages { get; init; } = [];
    public string Option { get; init; } = "";
    public string Value { get; init; } = "";
    public bool Diff { get; init; }
    public int Index { get; init; }
    public bool Boot { get; init; }
    public bool DryRun { get; init; }
    public string? Flake { get; init; }
    public string? Host { get; init; }
    public bool Json { get; init; }
    public bool Plain { get; init; }
    public bool System { get; init; }
    public string? File { get; init; }
    public string? NewFile { get; init; }
    public string? ImportFrom { get; init; }
}

/// <summary>
/// <c>nix-manager software ...</c>: the Software page's pending edits from a terminal (<c>src/software-cli.ts</c>).
/// Edits go into the same draft the dashboard shows; prepare and apply run the same reviewed operations through
/// the command line's operation path.
/// </summary>
[CommandGroup("software", Description = "Install packages and change options through pending edits, like the Software page")]
public static partial class SoftwareCli
{
    /// <summary>Options with a value, for moving options before the positional arguments.</summary>
    private static readonly HashSet<string> Valued = ["--flake", "--host", "--file", "--new-file", "--import-from", "--limit"];

    /// <summary>
    /// The arguments of a software command with options given before <c>software</c> moved behind it, or null for
    /// other commands (<c>software</c>).
    /// </summary>
    public static string[]? Arguments(IReadOnlyList<string> args)
    {
        var index = 0;
        while (index < args.Count && args[index].StartsWith("--", StringComparison.Ordinal)) index += Valued.Contains(args[index]) ? 2 : 1;
        if (index >= args.Count || args[index] != "software") return null;
        var after = args.Skip(index + 1).ToList();
        return ["software", .. after.Take(1), .. args.Take(index), .. after.Skip(1)];
    }

    /// <summary>
    /// The parser's view of <c>software SUB ...</c> (<c>parseSoftware</c>): options may follow the arguments, so
    /// they move first and the positional arguments follow <c>--</c>; arguments after a <c>--</c> stay positional.
    /// </summary>
    public static string[] Normalize(IReadOnlyList<string> args)
    {
        var head = args.Take(2).ToList();
        var rest = args.Skip(2).ToList();
        var options = new List<string>();
        var positional = new List<string>();
        for (var index = 0; index < rest.Count; index++)
        {
            var value = rest[index];
            if (value == "--")
            {
                positional.AddRange(rest.Skip(index + 1));
                break;
            }
            if (value.StartsWith("--", StringComparison.Ordinal) || value == "-h")
            {
                options.Add(value);
                if (Valued.Contains(value) && index + 1 < rest.Count) options.Add(rest[++index]);
            }
            else positional.Add(value);
        }
        return [.. head, .. options, .. positional.Count > 0 ? ["--", .. positional] : Array.Empty<string>()];
    }

    private const string FlakeHelp = "Use a specific flake checkout", HostHelp = "Select a registered host configuration",
        PlainHelp = "Disable dynamic terminal presentation", SystemHelp = "Edit the system configuration instead of your home configuration",
        FileHelp = "Write into this file of the flake", NewFileHelp = "Create this module file and write into it",
        ImportFromHelp = "Import the new file from this module (default: its folder's imported module)";

    [Command("software search", Description = "Search the packages of the locked nixpkgs")]
    internal static Task<CommandOutcome<string>> Search(
        [Argument(ValueName = "query", AllowMultipleValues = true)] string[] query,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--limit", Description = "Show at most this many packages")] int limit = 20,
        [Option("--free", Description = "Only packages with a free license")] bool free = false,
        [Option("--working", Description = "Leave out packages marked as broken")] bool working = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Search, Query = query, Limit = limit, Free = free, Working = working, Flake = flake, Host = host, Plain = plain },
            session, context, cancellationToken);

    [Command("software add", Description = "Install packages")]
    internal static Task<CommandOutcome<string>> Add(
        [Argument(ValueName = "package", AllowMultipleValues = true)] string[] packages,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--system", Description = SystemHelp)] bool system = false,
        [Option("--file", Description = FileHelp)] string? file = null,
        [Option("--new-file", Description = NewFileHelp)] string? newFile = null,
        [Option("--import-from", Description = ImportFromHelp)] string? importFrom = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Add, Packages = packages, Flake = flake, Host = host, Plain = plain, System = system, File = file, NewFile = newFile, ImportFrom = importFrom },
            session, context, cancellationToken);

    [Command("software remove", Description = "Remove packages from the file that lists them")]
    internal static Task<CommandOutcome<string>> Remove(
        [Argument(ValueName = "package", AllowMultipleValues = true)] string[] packages,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--system", Description = SystemHelp)] bool system = false,
        [Option("--file", Description = FileHelp)] string? file = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Remove, Packages = packages, Flake = flake, Host = host, Plain = plain, System = system, File = file },
            session, context, cancellationToken);

    [Command("software set", Description = "Set an option; the value is JSON, or text when it is not JSON")]
    internal static Task<CommandOutcome<string>> Set(
        [Argument(ValueName = "option")] string option,
        [Argument(ValueName = "value")] string value,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--system", Description = SystemHelp)] bool system = false,
        [Option("--file", Description = FileHelp)] string? file = null,
        [Option("--new-file", Description = NewFileHelp)] string? newFile = null,
        [Option("--import-from", Description = ImportFromHelp)] string? importFrom = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Set, Option = option, Value = value, Flake = flake, Host = host, Plain = plain, System = system, File = file, NewFile = newFile, ImportFrom = importFrom },
            session, context, cancellationToken);

    [Command("software unset", Description = "Remove an option's setting so its default applies")]
    internal static Task<CommandOutcome<string>> Unset(
        [Argument(ValueName = "option")] string option,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--system", Description = SystemHelp)] bool system = false,
        [Option("--file", Description = FileHelp)] string? file = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Unset, Option = option, Flake = flake, Host = host, Plain = plain, System = system, File = file },
            session, context, cancellationToken);

    [Command("software pending", Description = "List the pending edits")]
    internal static Task<CommandOutcome<string>> Pending(
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--diff", Description = "Show the edited lines")] bool diff = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Pending, Diff = diff, Flake = flake, Host = host, Plain = plain },
            session, context, cancellationToken);

    [Command("software drop", Description = "Remove one pending edit by its number")]
    internal static Task<CommandOutcome<string>> Drop(
        [Argument(ValueName = "number")] int number,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Drop, Index = number, Flake = flake, Host = host, Plain = plain },
            session, context, cancellationToken);

    [Command("software clear", Description = "Remove every pending edit")]
    internal static Task<CommandOutcome<string>> Clear(
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Clear, Flake = flake, Host = host, Plain = plain },
            session, context, cancellationToken);

    [Command("software prepare", Description = "Build the pending edits into a review")]
    internal static Task<CommandOutcome<string>> Prepare(
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Prepare, Flake = flake, Host = host, Plain = plain },
            session, context, cancellationToken);

    [Command("software apply", Description = "Write and activate the review prepared from the pending edits")]
    internal static Task<CommandOutcome<string>> Apply(
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp)] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--boot", Description = "Activate the system on the next boot")] bool boot = false,
        [Option("--dry-run", Description = "Show the steps without running them")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Apply, Boot = boot, DryRun = dryRun, Flake = flake, Host = host, Plain = plain },
            session, context, cancellationToken);

    /// <summary>
    /// Loads the configuration and runs a software command; prepare and apply continue on the command line's
    /// operation path (<c>main</c>'s software branch).
    /// </summary>
    private static async Task<CommandOutcome<string>> ExecuteAsync(SoftwareCommand command, CliSession session, CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        // At least one package or search word, as the Bun version's Args.atLeast(1) requires.
        if (command.Action is SoftwareAction.Search && command.Query.Count == 0) return Missing("<query>");
        if (command.Action is SoftwareAction.Add or SoftwareAction.Remove && command.Packages.Count == 0) return Missing("<package>");
        command = command with { Json = context.OutputMode == CommandOutputMode.Json };
        try
        {
            var config = await new ConfigLoader(session.Runner, session.Environment)
                .LoadAsync(command.Flake, command.Host, CliSession.PrivilegedPrefix).ConfigureAwait(false);
            var output = new StringBuilder();
            var runner = new SoftwareRunner(config, command,
                c => session.Runner.RunAsync(c, _ => { }, cancellationToken),
                note => context.Console.WriteErrorAsync($"{note}\n".AsMemory(), cancellationToken).AsTask(),
                session.Processes, cancellationToken);
            Request? next;
            try
            {
                next = await runner.RunAsync(output).ConfigureAwait(false);
            }
            finally
            {
                // What was printed before a failure stays printed, as console.log wrote it at once.
                if (output.Length > 0)
                {
                    if (command.Json) session.Output.Json = output.ToString().TrimEnd('\n');
                    else session.Output.Human = output.ToString();
                }
            }
            if (next is null) return CommandOutcome.Success("");
            var cli = new CliInvocation { Request = next, Json = command.Json, Plain = command.Plain };
            var outcome = await CliHandler.RunAsync(cli, config, session.CreateClient(config, session.Runner), session, context, cancellationToken)
                .ConfigureAwait(false);
            if (next.Action == Actions.ReviewBuild && !next.DryRun && !command.Json && outcome.IsSuccess)
                await context.Console.WriteErrorAsync(
                    "Review the changes with nix-manager ui, or write and activate them with nix-manager software apply (--boot for the next boot).\n".AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch (ManagerException error)
        {
            session.Output.Explanation = error.Explanation;
            return CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, new CommandFault("NM0001", error.Message));
        }
    }

    private static CommandOutcome<string> Missing(string argument) =>
        CommandOutcome.Failure<string>(CommandExitCategory.Usage, new CommandFault("RCLI1005", $"Missing argument {argument}"));

    [GeneratedRegex("""\G("(?:[^"\\]|\\.)*"|[A-Za-z_][A-Za-z0-9_'-]*)(\.|\z)""")]
    private static partial Regex AttrPart();

    /// <summary>The names of a dotted attribute path, quoted names unquoted; null when it is not one (<c>splitAttr</c> in <c>src/edits.ts</c>).</summary>
    public static List<string>? SplitAttr(string text)
    {
        var names = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var match = AttrPart().Match(text, index);
            if (!match.Success) return null;
            var name = match.Groups[1].Value;
            names.Add(name.StartsWith('"') ? JsonNode.Parse(name)!.GetValue<string>() : name);
            index = match.Index + match.Length;
            if (match.Groups[2].Length == 0 && index < text.Length) return null;
        }
        return names.Count > 0 ? names : null;
    }

    /// <summary>The prepared reviews, newest first (<c>listReviews</c> in <c>src/reviews.ts</c>).</summary>
    public static List<BuildReview> ListReviews(Config config)
    {
        var root = Path.Combine(config.StateDir, "reviews");
        if (!Directory.Exists(root)) return [];
        var found = new List<BuildReview>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (ManagerJson.Deserialize<BuildReview>(File.ReadAllText(Path.Combine(directory, "review.json"))) is { Id.Length: > 0 } review)
                    found.Add(review);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException) { }
        }
        return found.OrderByDescending(review => review.CreatedAt, Comparer<string>.Create(JsText.LocaleCompare)).ToList();
    }

    /// <summary>
    /// The pending edits as <c>software pending</c> lists them (<c>printPending</c>), with the edited lines when
    /// <paramref name="diff"/> is set.
    /// </summary>
    public static string PendingText(RenderedDraft draft, bool diff)
    {
        var text = new StringBuilder();
        if (draft.Changes.Count == 0) return "No pending edits.\n";
        var problems = new Dictionary<int, string>();
        foreach (var problem in draft.Problems) problems[problem.Index] = problem.Message;
        for (var index = 0; index < draft.Changes.Count; index++)
        {
            var described = ChangeRules.Describe(draft.Changes[index]);
            text.Append($"{(index + 1).ToString(CultureInfo.InvariantCulture),3}  {described.Title}\n     {described.Detail}\n");
            if (problems.TryGetValue(index, out var message)) text.Append($"     Cannot apply: {message}\n");
        }
        if (!diff) return text.ToString();
        foreach (var file in draft.Files)
        {
            text.Append($"\n--- {(file.Created == true ? "/dev/null" : $"a/{file.Path}")}\n+++ b/{file.Path}\n");
            foreach (var hunk in file.Hunks)
            {
                text.Append($"@@ line {hunk.Line.ToString(CultureInfo.InvariantCulture)} @@\n");
                foreach (var line in hunk.Lines) text.Append($"{line.Kind}{line.Text}\n");
            }
        }
        return text.ToString();
    }
}

/// <summary>
/// Runs one software command (<c>runSoftware</c>). Commands that edit the draft or read the checkout finish here;
/// prepare and apply return the operation to run.
/// </summary>
internal sealed class SoftwareRunner(Config config, SoftwareCommand command, Func<Command, Task<CommandResult>> run, Func<string, Task> note,
    Runic.CommandLine.Processes.IProcessRunner? processes = null, CancellationToken cancellationToken = default)
{
    private readonly string _host = command.Host is { Length: > 0 } host ? host : config.HostKey;
    private readonly string _scope = command.System ? Targets.System : Targets.Home;
    private string Name => _scope == Targets.Home ? $"{_host}'s home configuration" : $"{_host}'s system";
    private List<Change> _changes = [];
    private ScopeInventory? _inventory;

    private static ManagerException Fail(string message) => new(message);

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private async Task<ScopeInventory> InventoryAsync()
    {
        if (_inventory is not null) return _inventory;
        await note($"Reading {Name}...").ConfigureAwait(false);
        return _inventory = await Inventory.ReadAsync(config, _host, _scope, run).ConfigureAwait(false);
    }

    private HashSet<string> Known(ScopeInventory state) =>
    [
        .. state.Modules.Select(module => module.File),
        .. state.Files.Select(file => file.File),
        .. state.Importers,
        .. _changes.OfType<CreateModule>().Select(change => change.File),
    ];

    private static string AttrOf(string name)
    {
        var attr = name.StartsWith("pkgs.", StringComparison.Ordinal) ? name : $"pkgs.{name}";
        if (SoftwareCli.SplitAttr(attr) is not ["pkgs", _, ..]) throw Fail($"{name} is not a package attribute.");
        return attr;
    }

    /// <summary>The package index of the checkout's lock file, built when missing (<c>catalog</c>).</summary>
    private async Task<List<CatalogEntry>?> CatalogAsync(bool build)
    {
        var path = Catalog.PathFor(config, Repository.HashText(Repository.ReadLock(config.Flake)));
        if (!File.Exists(path))
        {
            if (!build) return null;
            await note("Building the package index for this lock file. This takes a minute the first time.").ConfigureAwait(false);
            await Catalog.BuildAsync(config, config.HostKey, path, processes, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            return Catalog.Read(path);
        }
        catch (JsonException error)
        {
            throw Fail($"Read package catalog: {error.Message}");
        }
    }

    /// <summary>The file named by --file or --new-file; a new file comes with the edit creating it.</summary>
    private async Task<(string File, List<Change> Create)?> ChosenAsync()
    {
        if (command.File is { Length: > 0 } && command.NewFile is { Length: > 0 }) throw Fail("Use either --file or --new-file.");
        if (command.File is { Length: > 0 } file) return (file, []);
        if (command.NewFile is not { Length: > 0 } newFile) return null;
        var state = await InventoryAsync().ConfigureAwait(false);
        if (ChangeRules.NewFileProblem(Known(state), newFile) is { } problem) throw Fail($"{newFile}: {problem}");
        var importer = command.ImportFrom ?? ChangeRules.RankImporters(state.Importers, newFile, state.Imports).FirstOrDefault();
        if (string.IsNullOrEmpty(importer))
            throw Fail($"No file {_host} imports has an imports list to add {newFile} to. Use --import-from.");
        return (newFile, [new CreateModule(_scope, newFile, importer)]);
    }

    /// <summary>Current value and defining flake files of an option.</summary>
    private async Task<OptionValue> OptionStateAsync(string option)
    {
        await note($"Reading {option} in {Name}...").ConfigureAwait(false);
        var source = await Evaluation.FlakeSourceAsync(config.Flake, run).ConfigureAwait(false);
        var state = await Evaluation.EvaluateStateAsync(source, Evaluation.ConfigurationAttr(config, _host, _scope), _scope,
            new StateRequest { Options = [option] }, run).ConfigureAwait(false);
        if (!state.Options.TryGetValue(option, out var value) || !value.Exists) throw Fail($"{option} is not an option of {Name}.");
        return value with { Files = value.Files.Where(file => !file.StartsWith('/')).ToList() };
    }

    /// <summary>Saves the draft unless one of the new edits cannot be applied to the checkout (<c>save</c>).</summary>
    private void Save(IReadOnlyList<Change> before, List<Change> changes, StringBuilder output)
    {
        var draft = Drafts.Public(config.Flake, changes);
        var added = new HashSet<int>(Enumerable.Range(0, changes.Count).Where(index => !before.Any(change => ReferenceEquals(change, changes[index]))));
        if (draft.Problems.FirstOrDefault(item => added.Contains(item.Index)) is { } problem)
            throw Fail($"{ChangeRules.Describe(changes[problem.Index]).Title}: {problem.Message}");
        Drafts.Write(config, changes);
        if (command.Json)
        {
            output.Append(ManagerJson.Serialize(draft)).Append('\n');
            return;
        }
        for (var index = 0; index < changes.Count; index++)
            if (added.Contains(index))
            {
                var text = ChangeRules.Describe(changes[index]);
                output.Append($"+ {text.Title} ({text.Detail})\n");
            }
        output.Append($"{changes.Count} pending {Plural(changes.Count, "edit", "edits")}. Run nix-manager software prepare to build {Plural(changes.Count, "it", "them")}.\n");
    }

    public async Task<Request?> RunAsync(StringBuilder output)
    {
        _changes = Drafts.Read(config);
        var changes = _changes;
        switch (command.Action)
        {
            case SoftwareAction.Search:
            {
                var entries = (await CatalogAsync(true).ConfigureAwait(false))!;
                var query = string.Join(' ', command.Query);
                var results = Catalog.Search(entries, query, Math.Max(1, Math.Min(command.Limit, 200)), new CatalogFilter(command.Free, command.Working));
                if (command.Json) output.Append(ManagerJson.Serialize(results)).Append('\n');
                else if (results.Count == 0) output.Append($"No packages match \"{query}\".\n");
                else
                    foreach (var entry in results)
                        output.Append($"{entry.A}{(entry.V is { Length: > 0 } v ? $" {v}" : "")}{(entry.B ? " (broken)" : "")}{(entry.F ? "" : " (unfree)")}\n    {entry.D ?? "No description."}\n");
                return null;
            }
            case SoftwareAction.Add:
            {
                var attrs = command.Packages.Select(AttrOf).ToList();
                var index = await CatalogAsync(false).ConfigureAwait(false);
                if (index is not null)
                {
                    var names = index.Select(entry => $"pkgs.{entry.A}").ToHashSet();
                    if (attrs.FirstOrDefault(attr => !names.Contains(attr)) is { } missing)
                        throw Fail($"{missing} is not in the locked nixpkgs. Find packages with nix-manager software search.");
                }
                var state = await InventoryAsync().ConfigureAwait(false);
                foreach (var attr in attrs)
                {
                    var installed = state.Packages.FirstOrDefault(item => item.Attr == attr || (string.IsNullOrEmpty(item.Attr) && $"pkgs.{item.Name}" == attr));
                    if (installed is not null)
                        throw Fail(!string.IsNullOrEmpty(installed.Attr)
                            ? $"{attr} is already listed in {installed.File}."
                            : $"{attr} is already installed by {ChangeRules.Origin(installed.File)}.");
                }
                // By default packages join the used list holding the most packages.
                var best = state.Files
                    .OrderByDescending(file => file.Active)
                    .ThenByDescending(file => file.Count)
                    .ThenBy(file => file.File, Comparer<string>.Create(JsText.LocaleCompare))
                    .FirstOrDefault();
                var target = await ChosenAsync().ConfigureAwait(false) ?? (best is { Active: true } ? (best.File, []) : null);
                if (target is not { } chosen)
                    throw Fail($"No file of {Name} has a package list. Choose one with --file, or create one with --new-file.");
                var next = chosen.Create.Concat(attrs.Select(attr => new AddPackage(_scope, chosen.File, attr))).ToList();
                Save(changes, ChangeRules.Merge(changes, next), output);
                // Prepare would refuse these; say so now, with the edit that allows them.
                var allowed = changes.Any(change => change is SetOption { Option: "nixpkgs.config.allowUnfree", Value: JsonValue value }
                    && change.Scope == _scope && value.GetValueKind() == JsonValueKind.True);
                var refused = allowed ? [] : (index ?? [])
                    .Where(entry => attrs.Contains($"pkgs.{entry.A}") && !entry.F && !ChangeRules.AcceptsUnfree(state.Unfree, entry.N)).ToList();
                if (refused.Count > 0)
                    await note($"{string.Join(", ", refused.Select(entry => entry.N ?? entry.A))} {Plural(refused.Count, "is", "are")} not free software, which {Name} does not allow. To allow it: {TerminalRenderer.FixCommand(new Fix { Kind = "allow-unfree", Packages = [], Scope = _scope })}")
                        .ConfigureAwait(false);
                return null;
            }
            case SoftwareAction.Remove:
            {
                var state = await InventoryAsync().ConfigureAwait(false);
                var next = command.Packages.Select(AttrOf).Select(attr =>
                {
                    var listed = state.Packages.FirstOrDefault(item => item.Attr == attr && (string.IsNullOrEmpty(command.File) || item.File == command.File));
                    if (listed is not null) return new RemovePackage(_scope, listed.File, attr);
                    var added = state.Packages.FirstOrDefault(item => $"pkgs.{item.Name}" == attr && item.External);
                    throw Fail(added is not null
                        ? $"{attr} is added by {ChangeRules.Origin(added.File)}, not a package list. Turn off the setting that adds it instead."
                        : $"{attr} is not listed in a file of {Name}.{(command.System ? "" : " For system packages, add --system.")}");
                }).ToList();
                Save(changes, ChangeRules.Merge(changes, next), output);
                return null;
            }
            case SoftwareAction.Set or SoftwareAction.Unset:
            {
                var current = await OptionStateAsync(command.Option).ConfigureAwait(false);
                var target = await ChosenAsync().ConfigureAwait(false);
                // A setting nothing sets yet goes where the dashboard would put it.
                if (target is null && command.Action == SoftwareAction.Set && current.Files.Count == 0)
                {
                    var state = await InventoryAsync().ConfigureAwait(false);
                    var ranked = ChangeRules.RankModules(state.Modules, _scope, command.Option);
                    var topic = ChangeRules.OptionTopic(command.Option);
                    if (ChangeRules.FeatureModule(ranked, topic) is { } existing) target = (existing, []);
                    else
                    {
                        var file = ChangeRules.FreePath(Known(state), topic, ranked.FirstOrDefault(module => module.Active != false)?.File,
                            _scope == Targets.Home ? "modules/home" : "modules/nixos");
                        if (ChangeRules.RankImporters(state.Importers, file, state.Imports).FirstOrDefault() is { } importer)
                            target = (file, [new CreateModule(_scope, file, importer)]);
                    }
                    if (target is { } written)
                        await note($"Writing to {written.File}{(written.Create is [CreateModule created, ..] ? $" (new, imported from {created.Importer})" : "")}.").ConfigureAwait(false);
                }
                var path = target?.File ?? (current.Files.Count == 1 ? current.Files[0] : null);
                if (path is null)
                    throw Fail(current.Files.Count > 0
                        ? $"{command.Option} is set in {string.Join(", ", current.Files)}. Choose one with --file."
                        : $"{command.Option} is not set in the flake yet. Choose where to write it with --file <path> or --new-file <path>.");
                List<Change> next;
                if (command.Action == SoftwareAction.Set)
                {
                    JsonNode? value;
                    try
                    {
                        value = JsonNode.Parse(command.Value);
                    }
                    catch (JsonException)
                    {
                        value = JsonValue.Create(command.Value);
                    }
                    if (current.Error != true && current.HasValue && JsJson.Stringify(current.Value) == JsJson.Stringify(value) && current.Files.Contains(path))
                        throw Fail($"{command.Option} is already {ChangeRules.ShowValue(value)}.");
                    next = [.. target?.Create ?? [], new SetOption(_scope, path, command.Option, value)];
                }
                else
                {
                    if (!current.Files.Contains(path)) throw Fail($"{command.Option} is not set in {path}.");
                    next = [new UnsetOption(_scope, path, command.Option)];
                }
                Save(changes, ChangeRules.Merge(changes, next), output);
                return null;
            }
            case SoftwareAction.Pending:
            {
                var draft = Drafts.Public(config.Flake, changes);
                output.Append(command.Json ? ManagerJson.Serialize(draft) + "\n" : SoftwareCli.PendingText(draft, command.Diff));
                return null;
            }
            case SoftwareAction.Drop:
            {
                if (command.Index < 1 || command.Index > changes.Count)
                    throw Fail(changes.Count > 0 ? $"Choose an edit from 1 to {changes.Count}." : "There are no pending edits.");
                var removed = ChangeRules.Describe(changes[command.Index - 1]);
                var rest = ChangeRules.Drop(changes, command.Index - 1);
                Drafts.Write(config, rest);
                if (command.Json) output.Append(ManagerJson.Serialize(Drafts.Public(config.Flake, rest))).Append('\n');
                else output.Append($"Dropped: {removed.Title}. {rest.Count} pending {Plural(rest.Count, "edit", "edits")} left.\n");
                return null;
            }
            case SoftwareAction.Clear:
            {
                Drafts.Write(config, []);
                if (command.Json) output.Append("{\"changes\":[]}\n");
                else output.Append(changes.Count > 0 ? $"Dropped {changes.Count} pending {Plural(changes.Count, "edit", "edits")}.\n" : "No pending edits.\n");
                return null;
            }
            case SoftwareAction.Prepare:
            {
                if (changes.Count == 0) throw Fail("There are no pending edits.");
                return Request.Create() with { Action = Actions.ReviewBuild, Scope = ChangeRules.ChangesScope(changes), Host = _host, Edits = true };
            }
            case SoftwareAction.Apply:
            {
                var hash = Change.Hash(changes);
                var review = SoftwareCli.ListReviews(config).FirstOrDefault(item =>
                    item.Edits?.Hash == hash && (item.Host is { Length: > 0 } h ? h : config.HostKey) == _host);
                if (changes.Count == 0 || review is null)
                    throw Fail("No prepared review matches the pending edits. Run nix-manager software prepare first.");
                return Request.Create() with
                {
                    Action = Actions.ReviewApply,
                    ReviewId = review.Id,
                    Scope = review.Scope,
                    Host = _host,
                    Activation = command.Boot ? "boot" : "switch",
                    DryRun = command.DryRun,
                };
            }
            default:
                throw new InvalidOperationException($"Unknown software command {command.Action}.");
        }
    }
}

/// <summary>
/// The outcome of a software command as the Bun version writes it: like <see cref="CliOutcomeSink"/>, except that a
/// command line the parser rejects is reported without the <c>Error:</c> prefix, as <c>@effect/cli</c> reports it.
/// </summary>
public sealed class SoftwareOutcomeSink(CliOutput output) : ICommandOutcomeSink
{
    private readonly CliOutcomeSink _sink = new(output);

    public async ValueTask WriteAsync<TResult>(CommandDescriptor command, CommandExecutionContext context, CommandOutcome<TResult> outcome,
        ICommandResultCodec<TResult> codec, int exitCode, IReadOnlyList<CommandDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (outcome.IsSuccess || outcome.ExitCategory != CommandExitCategory.Usage)
        {
            await _sink.WriteAsync(command, context, outcome, codec, exitCode, diagnostics, cancellationToken).ConfigureAwait(false);
            return;
        }
        await context.Console.WriteErrorAsync($"{outcome.Fault?.Message ?? "Command-line parsing failed."}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
