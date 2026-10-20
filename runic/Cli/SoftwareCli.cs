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

/// <summary>The kinds of software command.</summary>
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

/// <summary>A parsed <c>software</c> command.</summary>
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
    public bool Plain { get; init; }
    public bool System { get; init; }
    public string? File { get; init; }
    public string? NewFile { get; init; }
    public string? ImportFrom { get; init; }
}

/// <summary>
/// <c>nix-manager software ...</c>: the Software page's pending edits from a terminal. Edits go into the same draft
/// the dashboard shows; prepare and apply run the same reviewed operations through the command line's operation
/// path. <c>--flake</c>, <c>--host</c> and <c>--plain</c> are global options (<see cref="NixManagerCli.Catalog"/>),
/// so they may also precede <c>software</c>; the other options follow the command's name, before or after its
/// arguments.
/// </summary>
[CommandGroup("software", Description = "Install packages and change options through pending edits, like the Software page")]
public static partial class SoftwareCli
{
    private const string FlakeHelp = "Use a specific flake checkout", HostHelp = "Select a registered host configuration",
        PlainHelp = "Disable dynamic terminal presentation", SystemHelp = "Edit the system configuration instead of your home configuration",
        FileHelp = "Write into this file of the flake", NewFileHelp = "Create this module file and write into it",
        ImportFromHelp = "Import the new file from this module (default: its folder's imported module)";

    [Command("software search", Description = "Search the packages of the locked nixpkgs")]
    [CommandResult(SoftwareSearchResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<SoftwareSearchResult>> Search(
        [Argument(ValueName = "query", AllowMultipleValues = true, Required = true)] string[] query,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--limit", Description = "Show at most this many packages")] int limit = 20,
        [Option("--free", Description = "Only packages with a free license")] bool free = false,
        [Option("--working", Description = "Leave out packages marked as broken")] bool working = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Search, Query = query, Limit = limit, Free = free, Working = working, Flake = flake, Host = host, Plain = plain },
            session, context, CliFaults.SearchFailed, runner => runner.SearchAsync(), cancellationToken);

    [Command("software add", Description = "Install packages")]
    [CommandResult(SoftwareDraftResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<SoftwareDraftResult>> Add(
        [Argument(ValueName = "package", AllowMultipleValues = true, Required = true)] string[] packages,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--system", Description = SystemHelp)] bool system = false,
        [Option("--file", Description = FileHelp, ConflictsWith = ["new-file"])] string? file = null,
        [Option("--new-file", Description = NewFileHelp)] string? newFile = null,
        [Option("--import-from", Description = ImportFromHelp)] string? importFrom = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Add, Packages = packages, Flake = flake, Host = host, Plain = plain, System = system, File = file, NewFile = newFile, ImportFrom = importFrom },
            session, context, CliFaults.DraftNotChanged, runner => runner.AddAsync(), cancellationToken);

    [Command("software remove", Description = "Remove packages from the file that lists them")]
    [CommandResult(SoftwareDraftResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<SoftwareDraftResult>> Remove(
        [Argument(ValueName = "package", AllowMultipleValues = true, Required = true)] string[] packages,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--system", Description = SystemHelp)] bool system = false,
        [Option("--file", Description = FileHelp)] string? file = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Remove, Packages = packages, Flake = flake, Host = host, Plain = plain, System = system, File = file },
            session, context, CliFaults.DraftNotChanged, runner => runner.RemoveAsync(), cancellationToken);

    [Command("software set", Description = "Set an option; the value is JSON, or text when it is not JSON")]
    [CommandResult(SoftwareDraftResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<SoftwareDraftResult>> Set(
        [Argument(ValueName = "option")] string option,
        [Argument(ValueName = "value")] string value,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--system", Description = SystemHelp)] bool system = false,
        [Option("--file", Description = FileHelp, ConflictsWith = ["new-file"])] string? file = null,
        [Option("--new-file", Description = NewFileHelp)] string? newFile = null,
        [Option("--import-from", Description = ImportFromHelp)] string? importFrom = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Set, Option = option, Value = value, Flake = flake, Host = host, Plain = plain, System = system, File = file, NewFile = newFile, ImportFrom = importFrom },
            session, context, CliFaults.DraftNotChanged, runner => runner.SetAsync(), cancellationToken);

    [Command("software unset", Description = "Remove an option's setting so its default applies")]
    [CommandResult(SoftwareDraftResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<SoftwareDraftResult>> Unset(
        [Argument(ValueName = "option")] string option,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--system", Description = SystemHelp)] bool system = false,
        [Option("--file", Description = FileHelp)] string? file = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Unset, Option = option, Flake = flake, Host = host, Plain = plain, System = system, File = file },
            session, context, CliFaults.DraftNotChanged, runner => runner.SetAsync(), cancellationToken);

    [Command("software pending", Description = "List the pending edits")]
    [CommandResult(SoftwareDraftResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<SoftwareDraftResult>> Pending(
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--diff", Description = "Show the edited lines")] bool diff = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Pending, Diff = diff, Flake = flake, Host = host, Plain = plain },
            session, context, CliFaults.DraftUnavailable, runner => Task.FromResult(runner.Pending()), cancellationToken);

    [Command("software drop", Description = "Remove one pending edit by its number")]
    [CommandResult(SoftwareDraftResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<SoftwareDraftResult>> Drop(
        [Argument(ValueName = "number")] int number,
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Drop, Index = number, Flake = flake, Host = host, Plain = plain },
            session, context, CliFaults.DraftNotChanged, runner => Task.FromResult(runner.Drop()), cancellationToken);

    [Command("software clear", Description = "Remove every pending edit")]
    [CommandResult(SoftwareDraftResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<SoftwareDraftResult>> Clear(
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new SoftwareCommand { Action = SoftwareAction.Clear, Flake = flake, Host = host, Plain = plain },
            session, context, CliFaults.DraftNotChanged, runner => Task.FromResult(runner.Clear()), cancellationToken);

    [Command("software prepare", Description = "Build the pending edits into a review")]
    [CommandResult(RunResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<RunResult>> Prepare(
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        CancellationToken cancellationToken = default) =>
        OperateAsync(new SoftwareCommand { Action = SoftwareAction.Prepare, Flake = flake, Host = host, Plain = plain },
            session, context, runner => runner.Prepare(), cancellationToken);

    [Command("software apply", Description = "Write and activate the review prepared from the pending edits")]
    [CommandResult(RunResult.PayloadType, typeof(ManagerJsonContext))]
    internal static Task<CommandOutcome<RunResult>> Apply(
        [FromServices] CliSession session, CommandExecutionContext context,
        [Option("--flake", Description = FlakeHelp, ValueName = "PATH")] string? flake = null,
        [Option("--host", Description = HostHelp)] string? host = null,
        [Option("--plain", Description = PlainHelp)] bool plain = false,
        [Option("--boot", Description = "Activate the system on the next boot")] bool boot = false,
        [Option("--dry-run", Description = "Preview without making changes")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        OperateAsync(new SoftwareCommand { Action = SoftwareAction.Apply, Boot = boot, DryRun = dryRun, Flake = flake, Host = host, Plain = plain },
            session, context, runner => runner.Apply(), cancellationToken);

    /// <summary>
    /// Loads the configuration and runs a software command that finishes without an operation; <paramref name="summary"/>
    /// is its fault's message when it fails.
    /// </summary>
    private static async Task<CommandOutcome<T>> ExecuteAsync<T>(SoftwareCommand command, CliSession session, CommandExecutionContext context,
        string summary, Func<SoftwareRunner, Task<T>> run, CancellationToken cancellationToken)
    {
        Config config;
        try
        {
            config = await CliHandler.LoadAsync(command.Flake, command.Host, session).ConfigureAwait(false);
        }
        catch (ManagerException error)
        {
            return CliFaults.Failure<T>(CommandExitCategory.Unavailable, CliFaults.ConfigurationUnavailable, error);
        }
        try
        {
            return CommandOutcome.Success(await run(Runner(config, command, session, context, cancellationToken)).ConfigureAwait(false));
        }
        catch (ManagerException error)
        {
            return CliFaults.Failure<T>(CommandExitCategory.CommandFailure, summary, error);
        }
    }

    /// <summary>Loads the configuration and runs the operation a software command asks for on the command line's operation path.</summary>
    private static async Task<CommandOutcome<RunResult>> OperateAsync(SoftwareCommand command, CliSession session, CommandExecutionContext context,
        Func<SoftwareRunner, Request> request, CancellationToken cancellationToken)
    {
        Config config;
        try
        {
            config = await CliHandler.LoadAsync(command.Flake, command.Host, session).ConfigureAwait(false);
        }
        catch (ManagerException error)
        {
            return CliFaults.Failure<RunResult>(CommandExitCategory.Unavailable, CliFaults.ConfigurationUnavailable, error);
        }
        var json = context.OutputMode == CommandOutputMode.Json;
        try
        {
            var next = request(Runner(config, command, session, context, cancellationToken));
            var cli = new CliInvocation { Request = next, Json = json, Plain = command.Plain };
            var outcome = await CliHandler.RunAsync(cli, config, session.CreateClient(config, session.Runner), session, context, cancellationToken)
                .ConfigureAwait(false);
            if (next.Action == Actions.ReviewBuild && !next.DryRun && !json && outcome.IsSuccess)
                await context.Console.WriteErrorAsync(
                    "Review the changes with nix-manager ui, or write and activate them with nix-manager software apply (--boot for the next boot).\n".AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch (ManagerException error)
        {
            return CliFaults.Failure<RunResult>(CommandExitCategory.CommandFailure, CliFaults.OperationNotStarted, error);
        }
    }

    private static SoftwareRunner Runner(Config config, SoftwareCommand command, CliSession session, CommandExecutionContext context,
        CancellationToken cancellationToken) =>
        new(config, command,
            c => session.Runner.RunAsync(c, _ => { }, cancellationToken),
            note => context.Console.WriteErrorAsync($"{note}\n".AsMemory(), cancellationToken).AsTask(),
            session.Processes, cancellationToken);

    [GeneratedRegex("""\G("(?:[^"\\]|\\.)*"|[A-Za-z_][A-Za-z0-9_'-]*)(\.|\z)""")]
    private static partial Regex AttrPart();

    /// <summary>The names of a dotted attribute path, quoted names unquoted; null when it is not one.</summary>
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

    /// <summary>The prepared reviews, newest first.</summary>
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
    /// The pending edits as <c>software pending</c> lists them, with the edited lines when
    /// <paramref name="diff"/> is set.
    /// </summary>
    public static string PendingText(RenderedDraft draft, bool diff)
    {
        var text = new StringBuilder();
        if (draft.Changes.Count == 0) return "No pending edits.\n";
        var problems = new Dictionary<int, string>();
        foreach (var problem in draft.Problems) problems[problem.Index] = problem.Message.English;
        for (var index = 0; index < draft.Changes.Count; index++)
        {
            var described = ChangeRules.Describe(draft.Changes[index]);
            text.Append($"{(index + 1).ToString(CultureInfo.InvariantCulture),3}  {described.Title.English}\n     {described.Detail.English}\n");
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
/// Runs one software command. Commands that edit the draft or read the checkout finish here and return their
/// result; prepare and apply return the operation to run.
/// </summary>
internal sealed class SoftwareRunner(Config config, SoftwareCommand command, Func<Command, Task<CommandResult>> run, Func<string, Task> note,
    Runic.CommandLine.Processes.IProcessRunner? processes = null, CancellationToken cancellationToken = default)
{
    private readonly string _host = command.Host is { Length: > 0 } host ? host : config.HostKey;
    private readonly string _scope = command.System ? Targets.System : Targets.Home;
    private DisplayText Name => DisplayText.Of(_scope == Targets.Home ? "stored_host_home_configuration" : "stored_host_system", ("host", _host));
    private List<Change> _changes = [];
    private ScopeInventory? _inventory;

    private static ManagerException Fail(string key, params ReadOnlySpan<(string Name, object Value)> inputs) => new(DisplayText.Of(key, inputs));

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private async Task<ScopeInventory> InventoryAsync()
    {
        if (_inventory is not null) return _inventory;
        await note($"Reading {Name.English}...").ConfigureAwait(false);
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
        if (SoftwareCli.SplitAttr(attr) is not ["pkgs", _, ..]) throw Fail("stored_cli_not_a_package_attribute", ("name", name));
        return attr;
    }

    /// <summary>The package index of the checkout's lock file, built when missing.</summary>
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
            throw Fail("stored_step_failed_because", ("step", DisplayText.Of("stored_cli_read_package_catalog")), ("reason", DisplayText.Plain(error.Message)));
        }
    }

    /// <summary>The file named by --file or --new-file; a new file comes with the edit creating it.</summary>
    private async Task<(string File, List<Change> Create)?> ChosenAsync()
    {
        if (command.File is { Length: > 0 } file) return (file, []);
        if (command.NewFile is not { Length: > 0 } newFile) return null;
        var state = await InventoryAsync().ConfigureAwait(false);
        if (ChangeRules.NewFileProblem(Known(state), newFile) is { } problem) throw Fail("stored_edit_problem", ("subject", newFile), ("problem", problem));
        var importer = command.ImportFrom ?? ChangeRules.RankImporters(state.Importers, newFile, state.Imports).FirstOrDefault();
        if (string.IsNullOrEmpty(importer))
            throw Fail("stored_cli_no_importer_for_new_file", ("host", _host), ("file", newFile));
        return (newFile, [new CreateModule(_scope, newFile, importer)]);
    }

    /// <summary>Current value and defining flake files of an option.</summary>
    private async Task<OptionValue> OptionStateAsync(string option)
    {
        await note($"Reading {option} in {Name.English}...").ConfigureAwait(false);
        var source = await Evaluation.FlakeSourceAsync(config.Flake, run).ConfigureAwait(false);
        var state = await Evaluation.EvaluateStateAsync(source, Evaluation.ConfigurationAttr(config, _host, _scope), _scope,
            new StateRequest { Options = [option] }, run).ConfigureAwait(false);
        if (!state.Options.TryGetValue(option, out var value) || !value.Exists) throw Fail("stored_is_not_an_option_of", ("option", option), ("name", Name));
        return value with { Files = value.Files.Where(file => !file.StartsWith('/')).ToList() };
    }

    /// <summary>Saves the draft unless one of the new edits cannot be applied to the checkout, and returns it with the new edits' numbers.</summary>
    private SoftwareDraftResult Save(IReadOnlyList<Change> before, List<Change> changes)
    {
        var draft = Drafts.Public(config.Flake, changes);
        var added = Enumerable.Range(0, changes.Count).Where(index => !before.Any(change => ReferenceEquals(change, changes[index]))).ToList();
        if (draft.Problems.FirstOrDefault(item => added.Contains(item.Index)) is { } problem)
            throw Fail("stored_edit_problem", ("subject", ChangeRules.Describe(changes[problem.Index]).Title), ("problem", problem.Message));
        Drafts.Write(config, changes);
        return new SoftwareDraftResult { Draft = draft, Added = added.Select(index => index + 1).ToList() };
    }

    /// <summary>The draft as it is saved for this checkout.</summary>
    private List<Change> Changes() => _changes = Drafts.Read(config);

    public async Task<SoftwareSearchResult> SearchAsync()
    {
        var entries = (await CatalogAsync(true).ConfigureAwait(false))!;
        var query = string.Join(' ', command.Query);
        return new SoftwareSearchResult(query,
            Catalog.Search(entries, query, Math.Max(1, Math.Min(command.Limit, 200)), new CatalogFilter(command.Free, command.Working)));
    }

    public async Task<SoftwareDraftResult> AddAsync()
    {
        var changes = Changes();
        var attrs = command.Packages.Select(AttrOf).ToList();
        var index = await CatalogAsync(false).ConfigureAwait(false);
        if (index is not null)
        {
            var names = index.Select(entry => $"pkgs.{entry.A}").ToHashSet();
            if (attrs.FirstOrDefault(attr => !names.Contains(attr)) is { } missing)
                throw Fail("stored_cli_not_in_locked_nixpkgs", ("attr", missing));
        }
        var state = await InventoryAsync().ConfigureAwait(false);
        foreach (var attr in attrs)
        {
            var installed = state.Packages.FirstOrDefault(item => item.Attr == attr || (string.IsNullOrEmpty(item.Attr) && $"pkgs.{item.Name}" == attr));
            if (installed is not null)
                throw !string.IsNullOrEmpty(installed.Attr)
                    ? Fail("stored_cli_already_listed_in", ("attr", attr), ("file", installed.File))
                    : Fail("stored_cli_already_installed_by", ("attr", attr), ("origin", ChangeRules.Origin(installed.File)));
        }
        // By default packages join the used list holding the most packages.
        var best = state.Files
            .OrderByDescending(file => file.Active)
            .ThenByDescending(file => file.Count)
            .ThenBy(file => file.File, Comparer<string>.Create(JsText.LocaleCompare))
            .FirstOrDefault();
        var target = await ChosenAsync().ConfigureAwait(false) ?? (best is { Active: true } ? (best.File, []) : null);
        if (target is not { } chosen)
            throw Fail("stored_cli_no_package_list", ("name", Name));
        var next = chosen.Create.Concat(attrs.Select(attr => new AddPackage(_scope, chosen.File, attr))).ToList();
        var result = Save(changes, ChangeRules.Merge(changes, next));
        // Prepare would refuse these; say so now, with the edit that allows them.
        var allowed = changes.Any(change => change is SetOption { Option: "nixpkgs.config.allowUnfree", Value: JsonValue value }
                    && change.Scope == _scope && value.GetValueKind() == JsonValueKind.True);
        var refused = allowed ? [] : (index ?? [])
            .Where(entry => attrs.Contains($"pkgs.{entry.A}") && !entry.F && !ChangeRules.AcceptsUnfree(state.Unfree, entry.N)).ToList();
        if (refused.Count > 0)
            await note($"{string.Join(", ", refused.Select(entry => entry.N ?? entry.A))} {Plural(refused.Count, "is", "are")} not free software, which {Name.English} does not allow. To allow it: {TerminalRenderer.FixCommand(new Fix { Kind = "allow-unfree", Packages = [], Scope = _scope })}")
                .ConfigureAwait(false);
        return result;
    }

    public async Task<SoftwareDraftResult> RemoveAsync()
    {
        var changes = Changes();
        var state = await InventoryAsync().ConfigureAwait(false);
        var next = command.Packages.Select(AttrOf).Select(attr =>
        {
            var listed = state.Packages.FirstOrDefault(item => item.Attr == attr && (string.IsNullOrEmpty(command.File) || item.File == command.File));
            if (listed is not null) return new RemovePackage(_scope, listed.File, attr);
            var added = state.Packages.FirstOrDefault(item => $"pkgs.{item.Name}" == attr && item.External);
            throw added is not null
                ? Fail("stored_cli_added_by_setting", ("attr", attr), ("origin", ChangeRules.Origin(added.File)))
                : Fail(command.System ? "stored_cli_not_listed_in_configuration" : "stored_cli_not_listed_in_home_configuration", ("attr", attr), ("name", Name));
        }).ToList();
        return Save(changes, ChangeRules.Merge(changes, next));
    }

    /// <summary>Sets or unsets an option, as the command asks.</summary>
    public async Task<SoftwareDraftResult> SetAsync()
    {
        var changes = Changes();
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
            throw current.Files.Count > 0
                ? Fail("stored_cli_option_set_in_several_files", ("option", command.Option), ("files", string.Join(", ", current.Files)))
                : Fail("stored_cli_option_not_set_in_flake", ("option", command.Option));
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
                throw Fail("stored_cli_option_is_already", ("option", command.Option), ("value", ChangeRules.ShowValue(value)));
            next = [.. target?.Create ?? [], new SetOption(_scope, path, command.Option, value)];
        }
        else
        {
            if (!current.Files.Contains(path)) throw Fail("stored_cli_option_not_set_in_file", ("option", command.Option), ("path", path));
            next = [new UnsetOption(_scope, path, command.Option)];
        }
        return Save(changes, ChangeRules.Merge(changes, next));
    }

    public SoftwareDraftResult Pending() =>
        new() { Draft = Drafts.Public(config.Flake, Changes()), ShowDiff = command.Diff };

    public SoftwareDraftResult Drop()
    {
        var changes = Changes();
        if (command.Index < 1 || command.Index > changes.Count)
            throw changes.Count > 0 ? Fail("stored_cli_choose_an_edit", ("count", changes.Count)) : Fail("stored_there_are_no_pending_edits");
        var rest = ChangeRules.Drop(changes, command.Index - 1);
        Drafts.Write(config, rest);
        return new SoftwareDraftResult { Draft = Drafts.Public(config.Flake, rest), Dropped = [changes[command.Index - 1]] };
    }

    public SoftwareDraftResult Clear()
    {
        var changes = Changes();
        Drafts.Write(config, []);
        return new SoftwareDraftResult { Draft = Drafts.Public(config.Flake, []), Dropped = changes };
    }

    /// <summary>The operation that builds the pending edits into a review.</summary>
    public Request Prepare()
    {
        var changes = Changes();
        if (changes.Count == 0) throw Fail("stored_there_are_no_pending_edits");
        return Request.Create() with { Action = Actions.ReviewBuild, Scope = ChangeRules.ChangesScope(changes), Host = _host, Edits = true };
    }

    /// <summary>The operation that writes and activates the review prepared from the pending edits.</summary>
    public Request Apply()
    {
        var changes = Changes();
        var hash = Change.Hash(changes);
        var review = SoftwareCli.ListReviews(config).FirstOrDefault(item =>
            item.Edits?.Hash == hash && (item.Host is { Length: > 0 } h ? h : config.HostKey) == _host);
        if (changes.Count == 0 || review is null)
            throw Fail("stored_cli_no_prepared_review");
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
}
