using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Execution;
using NixManager.Nix.Syntax;
using NixManager.Nix;
using NixManager.Operations;

namespace NixManager.Software;

/// <summary>A package list item of a flake file, for mapping evaluated packages back to source (<c>SourceList</c>).</summary>
public sealed record SourceList(string File, string Binding, string? Attr, IReadOnlyList<string>? Path, bool Conditional);

/// <summary>What <see cref="Inventory.SourceLists"/> reports about one file: its shape and its unconditional package bindings.</summary>
public sealed record SourceModule(string File, bool Editable, IReadOnlyList<string> Names, IReadOnlyList<string> Settings,
    IReadOnlyList<string> Imports, bool Importable, IReadOnlyList<string> Lists);

/// <summary>A package a configuration installs, mapped back to the line that installs it (<c>InstalledPackage</c>).</summary>
public sealed record InstalledPackage : PackageInfo
{
    /// <summary>Flake-relative file, or a module path outside the flake.</summary>
    [JsonPropertyOrder(1)] public required string File { get; init; }
    /// <summary>Source attribute such as <c>pkgs.krita</c> when the item can be removed by editing.</summary>
    [JsonPropertyOrder(1)] public string? Attr { get; init; }
    /// <summary>Added by a module (for example <c>programs.git.enable</c>), not a package list.</summary>
    [JsonPropertyOrder(1)] public bool External { get; init; }
    /// <summary>
    /// Version in the generation running now when the checkout builds another one, or JSON null when the running
    /// generation lacks the package. Undefined (omitted) when nothing changes or the running generation is not this
    /// configuration's.
    /// </summary>
    [JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault), Runic.Application.Views.RunicBridgeCodec(typeof(ViewModels.PreviousVersionBridgeCodec))] public JsonElement Previous { get; init; }
}

/// <summary>A file with a package list a scope could add to (<c>PackageFile</c>).</summary>
public sealed record PackageFile(string File, IReadOnlyList<string> Bindings, bool Active, int Count);

/// <summary>What a host's configuration installs and which files it uses (<c>ScopeInventory</c>).</summary>
public sealed record ScopeInventory
{
    public required string Scope { get; init; }
    public IReadOnlyList<InstalledPackage> Packages { get; init; } = [];
    /// <summary>Files with a package list this scope could add to.</summary>
    public IReadOnlyList<PackageFile> Files { get; init; } = [];
    /// <summary>Module files whose settings can be edited.</summary>
    public IReadOnlyList<ModuleFile> Modules { get; init; } = [];
    /// <summary>Files the host imports that import others, which a new module file can be imported from.</summary>
    public IReadOnlyList<string> Importers { get; init; } = [];
    /// <summary>Flake paths each importer imports.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Imports { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, OptionValue> Options { get; init; } = new Dictionary<string, OptionValue>();
    /// <summary>Which non-free packages it accepts; absent when that does not evaluate.</summary>
    public UnfreePolicy? Unfree { get; init; }
    /// <summary>System scope: the accounts that can log in, when they evaluate.</summary>
    public IReadOnlyList<UserAccount>? Users { get; init; }
}

/// <summary>What a host's configuration installs, mapped back to the lines that install it (<c>src/software.ts</c>).</summary>
public static partial class Inventory
{
    /// <summary><c>.nix</c> files of a flake source, relative to it, sorted (<c>nixFiles</c>).</summary>
    public static List<string> NixFiles(string source)
    {
        var found = new List<string>();
        void Walk(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                // A symbolic link is not followed into, as readdir's Dirent.isDirectory reports it.
                var link = entry.LinkTarget is not null;
                if (entry is DirectoryInfo && !link) Walk(entry.FullName);
                else if (entry.Name.EndsWith(".nix", StringComparison.Ordinal))
                    found.Add(Path.GetRelativePath(source, entry.FullName));
            }
        }
        Walk(source);
        found.Sort(string.CompareOrdinal);
        return found;
    }

    /// <summary>Package list items of every file, for mapping evaluated packages back to source (<c>sourceLists</c>).</summary>
    public static (List<SourceList> Items, List<SourceModule> Modules) SourceLists(string source)
    {
        var items = new List<SourceList>();
        var modules = new List<SourceModule>();
        foreach (var file in NixFiles(source))
        {
            var text = File.ReadAllText(Path.Combine(source, file));
            var tree = NixSyntax.Parse(text);
            var lists = PackageLists.Find(tree.Root);
            foreach (var list in lists)
                foreach (var item in list.Items)
                    items.Add(new SourceList(file, list.Binding, item.Attr, item.Path, list.Conditional));
            var shape = ModuleOptions.Shape(text);
            modules.Add(new SourceModule(file, shape.Editable, shape.Names, shape.Settings, shape.Imports, shape.Importable,
                lists.Where(list => !list.Conditional).Select(list => list.Binding).Distinct().ToList()));
        }
        return (items, modules);
    }

    /// <summary>
    /// Adds the files that used files import unconditionally, transitively: a module that only imports others shows
    /// no settings of its own (<c>importedFiles</c>).
    /// </summary>
    public static HashSet<string> ImportedFiles(IEnumerable<(string File, IReadOnlyList<string> Imports)> modules, IEnumerable<string> used)
    {
        var known = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (file, imports) in modules) known[file] = imports;
        var result = new HashSet<string>(used);
        var queue = new Stack<string>(result);
        while (queue.Count > 0)
        {
            var file = queue.Pop();
            foreach (var target in known.GetValueOrDefault(file) ?? [])
            {
                var path = PosixPath.Normalize(PosixPath.Join(PosixPath.Dirname(file), target));
                var child = path.EndsWith(".nix", StringComparison.Ordinal) ? path : $"{path}/default.nix";
                if (known.ContainsKey(child) && result.Add(child)) queue.Push(child);
            }
        }
        return result;
    }

    /// <summary>Store path names of the generation a scope runs now, when the host is this computer (<c>runningGeneration</c>).</summary>
    public static async Task<List<string>?> RunningGenerationAsync(Config config, string host, string scope, Func<Command, Task<CommandResult>> run)
    {
        if (host != config.HostKey || (scope == Targets.Home && !config.HomeProfileExists)) return null;
        var profile = scope == Targets.Home ? config.HomeProfile : "/run/current-system";
        try
        {
            var result = await run(new Command
            {
                Label = "Read the running generation",
                Argv = ["nix-store", "--query", "--requisites", profile],
                Quiet = true,
                AllowFailure = true,
            }).ConfigureAwait(false);
            var paths = result.Code == 0 ? result.Stdout.Split('\n').Where(line => line.Length > 0).ToList() : [];
            return paths.Count > 0 ? paths : null;
        }
        catch (ManagerException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\d")]
    private static partial Regex Digit();

    [GeneratedRegex("^/nix/store/[a-z0-9]{32}-")]
    private static partial Regex StorePrefix();

    /// <summary>
    /// What the running generation has of a package (<c>previousVersion</c>): <c>Report</c> false when there is
    /// nothing to report (undefined), otherwise the other version, or null when it lacks the package.
    /// </summary>
    public static (bool Report, string? Version) PreviousVersion(IReadOnlySet<string> paths, IReadOnlyList<string> names, PackageInfo item)
    {
        if (paths.Contains(item.Out)) return (false, null);
        var prefix = $"{item.Name}-";
        var versions = new List<string>();
        foreach (var name in names)
            if (name.StartsWith(prefix, StringComparison.Ordinal) && Digit().IsMatch(name[prefix.Length..]) && !versions.Contains(name[prefix.Length..]))
                versions.Add(name[prefix.Length..]);
        // The same version built again, for example after a dependency changed.
        if (item.Version is { Length: > 0 } version
            && versions.Any(v => v == version || v.StartsWith($"{version}-", StringComparison.Ordinal)))
            return (false, null);
        if (versions.Count == 0) return (true, null);
        versions.Sort(string.CompareOrdinal);
        return (true, versions[0]);
    }

    private static JsonElement Previous((bool Report, string? Version) previous) => !previous.Report ? default
        : JsonSerializer.SerializeToElement(previous.Version, ManagerJson.Default.String);

    /// <summary>What a host's configuration installs, mapped back to the lines that install it (<c>inventory</c>).</summary>
    public static async Task<ScopeInventory> ReadAsync(Config config, string host, string scope, Func<Command, Task<CommandResult>> run,
        IReadOnlyList<string>? options = null)
    {
        var source = await Evaluation.FlakeSourceAsync(config.Flake, run).ConfigureAwait(false);
        var (items, modules) = SourceLists(source);
        var resolvable = items.Where(item => item.Path is { Count: > 1 } path && path[0] == "pkgs").ToList();
        var editable = modules.Where(module => module.Editable && module.File != "flake.nix").ToList();
        var probes = editable.SelectMany(module => module.Settings.Take(4).Select(option => new Probe(module.File, option))).ToList();
        var evaluation = Evaluation.EvaluateStateAsync(source, Evaluation.ConfigurationAttr(config, host, scope), scope, new StateRequest
        {
            Options = options ?? [],
            Attrs = resolvable.Select(item => (IReadOnlyList<string>)item.Path!.Skip(1).ToList()).ToList(),
            Probes = probes,
            Users = scope == Targets.System,
        }, run);
        var generation = RunningGenerationAsync(config, host, scope, run);
        await Task.WhenAll(evaluation, generation).ConfigureAwait(false);
        var state = await evaluation.ConfigureAwait(false);
        var running = await generation.ConfigureAwait(false);
        var paths = new HashSet<string>(running ?? []);
        var names = (running ?? []).Select(path => StorePrefix().Replace(path, "")).ToList();
        var counts = new Dictionary<string, int>();
        foreach (var file in state.Packages) counts[file.File] = file.Items.Count;
        var used = ImportedFiles(modules.Select(module => (module.File, module.Imports)),
            probes.Where((_, index) => index < state.Used.Count && state.Used[index]).Select(probe => probe.File).Concat(counts.Keys));
        var bySource = new Dictionary<string, string>();
        for (var index = 0; index < resolvable.Count; index++)
            if (index < state.Resolved.Count && state.Resolved[index] is { } output)
                bySource[$"{resolvable[index].File}\0{output}"] = resolvable[index].Attr!;
        var files = modules.Select(module => module.File).ToHashSet();
        var packages = state.Packages.SelectMany(group => group.Items.Select(item => new InstalledPackage
        {
            Out = item.Out,
            Name = item.Name,
            Version = item.Version,
            Description = item.Description,
            File = group.File,
            Attr = bySource.GetValueOrDefault($"{group.File}\0{item.Out}"),
            External = !files.Contains(group.File),
            Previous = running is null ? default : Previous(PreviousVersion(paths, names, item)),
        })).ToList();
        var bindings = Drafts.ScopeBindings(scope);
        var importers = modules.Where(module => module.Importable && module.Imports.Count > 0 && used.Contains(module.File)).ToList();
        var imports = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var module in importers)
            imports[module.File] = module.Imports.Select(path => PosixPath.Normalize(PosixPath.Join(PosixPath.Dirname(module.File), path))).ToList();
        return new ScopeInventory
        {
            Scope = scope,
            Packages = packages.OrderBy(package => package.Name, Comparer<string>.Create(JsText.LocaleCompare)).ToList(),
            Files = modules
                .Where(module => module.Lists.Any(bindings.Contains))
                .Select(module => new PackageFile(module.File, module.Lists, used.Contains(module.File), counts.GetValueOrDefault(module.File)))
                .OrderByDescending(file => file.Active)
                .ThenBy(file => file.File, Comparer<string>.Create(JsText.LocaleCompare))
                .ToList(),
            Modules = editable.Select(module => new ModuleFile(module.File, module.Names,
                used.Contains(module.File) ? true : module.Settings.Count > 0 ? false : null)).ToList(),
            Importers = importers.Select(module => module.File).ToList(),
            Imports = imports,
            Options = state.Options,
            Unfree = state.Unfree,
            Users = state.Users,
        };
    }
}
