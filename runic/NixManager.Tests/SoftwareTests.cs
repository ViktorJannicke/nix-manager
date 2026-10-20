using System.Text.Json;
using System.Text.Json.Nodes;
using NixManager.Cli;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Software;

namespace NixManager.Tests;

/// <summary>
/// The software functions that need neither nix nor the edit engine, compared with the recorded results in
/// <c>Fixtures/recorded-software-functions.json</c> and the recorded searches.
/// </summary>
public sealed class SoftwareTests
{
    private static readonly JsonNode Functions = JsonNode.Parse(File.ReadAllText(Path.Combine(RecordedFixtures.Directory, "recorded-software-functions.json")))!;

    private static List<string> Strings(JsonNode? node) => node!.AsArray().Select(item => item!.GetValue<string>()).ToList();

    private static Config TestConfig(string root) => new()
    {
        Flake = root,
        Hosts = new Dictionary<string, Host>
        {
            ["test"] = new() { System = "x86_64-linux", NixosConfiguration = "test-machine", NixosHostName = "test-machine", HomeConfiguration = "user-test" },
        },
        HostKey = "test",
        StateDir = Path.Combine(root, "state"),
        HomeProfile = Path.Combine(root, "profile"),
        HomeProfileExists = true,
        Privileged = [],
    };

    [Test]
    public async Task The_running_generation_reports_versions_as_the_Bun_version()
    {
        string[] names =
        [
            "git-2.50.0", "git-2.50.0-doc", "ripgrep-14.1.1", "fzf-0.65.2", "fzf-man", "hello-2.12.2", "hello-2.12.1",
            "python3-3.13.5", "python3-3.13.5-env", "krita-5.2.11", "nodejs-22.0.0", "nodejs-20.0.0",
        ];
        var paths = new HashSet<string> { "/nix/store/00000000000000000000000000000000-ripgrep-14.1.1" };
        foreach (var example in Functions["previousVersion"]!.AsArray())
        {
            var item = example!["item"]!;
            var package = new PackageInfo
            {
                Out = item["out"]!.GetValue<string>(),
                Name = item["name"]!.GetValue<string>(),
                Version = item["version"]?.GetValue<string>(),
            };
            var (report, version) = Inventory.PreviousVersion(paths, names, package);
            var expected = example["previous"];
            var actual = !report ? "undefined" : version;
            await Assert.That(actual).IsEqualTo(expected?.GetValue<string>()).Because(package.Out);
        }
    }

    [Test]
    public async Task Imported_files_follow_imports_as_the_Bun_version()
    {
        (string, IReadOnlyList<string>)[] modules =
        [
            ("flake.nix", []),
            ("hosts/test/default.nix", ["../../modules/nixos", "./hardware.nix"]),
            ("hosts/test/hardware.nix", []),
            ("modules/nixos/default.nix", ["./desktop.nix", "../shared/fonts.nix", "missing.nix"]),
            ("modules/nixos/desktop.nix", []),
            ("modules/shared/fonts.nix", ["../nixos/desktop.nix"]),
            ("home.nix", ["./modules/home"]),
            ("modules/home/default.nix", []),
            ("unused.nix", ["./modules/home/default.nix"]),
        ];
        foreach (var example in Functions["importedFiles"]!.AsArray())
        {
            var result = Inventory.ImportedFiles(modules, Strings(example!["used"])).Order(StringComparer.Ordinal).ToList();
            await Assert.That(string.Join(",", result)).IsEqualTo(string.Join(",", Strings(example["result"])));
        }
    }

    [Test]
    public async Task Attribute_paths_split_as_the_Bun_version()
    {
        foreach (var example in Functions["splitAttr"]!.AsArray())
        {
            var text = example!["text"]!.GetValue<string>();
            var expected = example["result"] is JsonArray names ? string.Join("|", Strings(names)) : null;
            var actual = SoftwareCli.SplitAttr(text) is { } result ? string.Join("|", result) : null;
            await Assert.That(actual).IsEqualTo(expected).Because(text);
        }
    }

    /// <summary>The options and arguments a command line binds, in a form that ignores their order.</summary>
    private static string? Parsed(string[] args)
    {
        var outcome = Runic.CommandLine.PortableCommandSyntaxAdapter.Instance.Parse(NixManagerCli.Catalog(), args, new Runic.CommandLine.ParseSettings());
        if (outcome.Invocation is not { } invocation) return null;
        static IEnumerable<string> Bound(IEnumerable<Runic.CommandLine.CommandValueBinding> bindings) =>
            bindings.Select(binding => $"{binding.Id}={string.Join(",", binding.Values)}").Order(StringComparer.Ordinal);
        return $"{invocation.Path} | {string.Join(" ", Bound(invocation.Options))} | {string.Join(" ", Bound(invocation.Arguments))}";
    }

    [Test]
    [Arguments(new[] { "--flake", "/x", "--host", "h", "software", "add", "fzf", "--system" }, new[] { "software", "add", "--flake", "/x", "--host", "h", "fzf", "--system" })]
    [Arguments(new[] { "--plain", "software", "pending", "--diff" }, new[] { "software", "pending", "--diff", "--plain" })]
    [Arguments(new[] { "software", "apply", "--dry-run", "--boot" }, new[] { "software", "apply", "--boot", "--dry-run" })]
    [Arguments(new[] { "software", "add", "fzf", "--system", "--file", "a.nix", "git" }, new[] { "software", "add", "--system", "--file", "a.nix", "--", "fzf", "git" })]
    [Arguments(new[] { "software", "search", "--limit", "3", "git" }, new[] { "software", "search", "git", "--limit", "3" })]
    public async Task Shared_options_may_precede_the_software_command_and_options_may_follow_its_arguments(string[] args, string[] same)
    {
        var parsed = Parsed(args);
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed).IsEqualTo(Parsed(same));
    }

    [Test]
    public async Task Posix_paths_resolve_as_node_does()
    {
        foreach (var example in Functions["normalize"]!.AsArray())
            await Assert.That(PosixPath.Normalize(example!["path"]!.GetValue<string>())).IsEqualTo(example["result"]!.GetValue<string>());
        foreach (var example in Functions["join"]!.AsArray())
            await Assert.That(PosixPath.Join([.. Strings(example!["parts"])])).IsEqualTo(example["result"]!.GetValue<string>());
        foreach (var example in Functions["dirname"]!.AsArray())
            await Assert.That(PosixPath.Dirname(example!["path"]!.GetValue<string>())).IsEqualTo(example["result"]!.GetValue<string>());
    }

    /// <summary>The recorded <c>software search</c> cases, run against the search directly.</summary>
    [Test]
    public async Task Catalog_search_ranks_as_recorded()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(RecordedFixtures.Directory, "cli-cases-software.json")))!.AsArray();
        var recorded = JsonNode.Parse(File.ReadAllText(Path.Combine(RecordedFixtures.Directory, "recorded-cli-software.json")))!.AsArray()
            .ToDictionary(item => item!["name"]!.GetValue<string>());
        var checkedCases = 0;
        foreach (var test in cases)
        {
            var args = Strings(test!["args"]);
            if (args is not ["software", "search", ..] || recorded[test["name"]!.GetValue<string>()]!["exitCode"]!.GetValue<int>() != 0) continue;
            var catalog = test["files"]!.AsObject().First(file => file.Key.Contains("/catalog/", StringComparison.Ordinal)).Value!.GetValue<string>();
            var entries = ManagerJson.Deserialize<List<CatalogEntry>>(catalog)!;
            int limit = 20;
            bool free = false, working = false;
            var query = new List<string>();
            for (var i = 2; i < args.Count; i++)
                switch (args[i])
                {
                    case "--limit": limit = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--free": free = true; break;
                    case "--working": working = true; break;
                    case "--output": i++; break;
                    case "--": break;
                    default: query.Add(args[i]); break;
                }
            var results = Catalog.Search(entries, string.Join(' ', query), Math.Max(1, Math.Min(limit, 200)), new CatalogFilter(free, working));
            var stdout = recorded[test["name"]!.GetValue<string>()]!["stdout"]!.GetValue<string>();
            var expected = stdout.TrimStart().StartsWith('{')
                ? JsonNode.Parse(stdout)!["payload"]!["packages"]!.AsArray().Select(entry => entry!["a"]!.GetValue<string>()).ToList()
                : stdout.Split('\n').Where(line => line.Length > 0 && !line.StartsWith(' ') && !line.StartsWith("No packages", StringComparison.Ordinal))
                    .Select(line => line.Split(' ')[0]).ToList();
            await Assert.That(string.Join(",", results.Select(entry => entry.A))).IsEqualTo(string.Join(",", expected)).Because(string.Join(" ", args));
            checkedCases++;
        }
        await Assert.That(checkedCases).IsGreaterThan(5);
    }

    [Test]
    public async Task Apps_are_found_by_their_shown_name_and_come_first()
    {
        List<CatalogEntry> entries =
        [
            new() { A = "gimp", N = "gimp", D = "GNU Image Manipulation Program", F = true },
            new() { A = "gimp-tool", N = "gimp-tool", D = "Image tool", F = true },
            new() { A = "pinta", N = "pinta", D = "Drawing", F = true },
        ];
        var apps = new Dictionary<string, AppInfo> { ["pinta"] = new() { Id = "com.github.PintaProject.Pinta", Name = "Pinta Image Editor", Keywords = ["paint"] } };
        var results = Catalog.Search(entries, "image", 10, null, apps);
        await Assert.That(results.Select(entry => entry.A).First()).IsEqualTo("pinta");
        await Assert.That(Catalog.Search(entries, "paint", 10, null, apps).Single().A).IsEqualTo("pinta");
        await Assert.That(Catalog.Search(entries, "  ", 10)).IsEmpty();
    }

    [Test]
    public async Task The_catalog_path_carries_the_lock_hash_and_version()
    {
        var config = TestConfig("/flake");
        await Assert.That(Catalog.PathFor(config, new string('a', 64))).IsEqualTo($"/flake/state/catalog/{new string('a', 32)}.v2.json");
    }

    private static string Script(string directory, string body)
    {
        var path = Path.Combine(directory, "fake-nix");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Test]
    public async Task Building_the_catalog_writes_the_evaluation_to_its_file_and_removes_older_ones()
    {
        var root = Directory.CreateTempSubdirectory("nix-manager-catalog-").FullName;
        try
        {
            var config = TestConfig(root);
            var target = Catalog.PathFor(config, new string('b', 64));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var older = Path.Combine(Path.GetDirectoryName(target)!, "old.v1.json");
            File.WriteAllText(older, "[]");
            var program = Script(root, $$"""
                printf '%s\n' "$@" > '{{root}}/args'
                printf '[{"a":"fzf","n":"fzf","v":"1","d":null,"h":null,"p":"fzf","l":[],"f":true,"b":false,"g":false}]'
                """);

            await Catalog.BuildAsync(config, "test", target, program: program);

            var args = File.ReadAllLines(Path.Combine(root, "args"));
            await Assert.That(string.Join(" ", args.Take(3))).IsEqualTo($"eval --json {root}#{Evaluation.ConfigurationAttr(config, "test", "home")}.pkgs");
            await Assert.That(args[3]).IsEqualTo("--apply");
            var entries = Catalog.Read(target);
            await Assert.That(entries.Single().A).IsEqualTo("fzf");
            await Assert.That(File.Exists(older)).IsFalse();
            await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(target)!).Count()).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task A_failed_catalog_evaluation_reports_the_end_of_its_errors_and_leaves_nothing()
    {
        var root = Directory.CreateTempSubdirectory("nix-manager-catalog-").FullName;
        try
        {
            var config = TestConfig(root);
            var target = Catalog.PathFor(config, new string('c', 64));
            var program = Script(root, "echo '[partial'; echo 'error: attribute missing' >&2; exit 1");

            var error = await Assert.That(() => Catalog.BuildAsync(config, "test", target, program: program)).Throws<ManagerException>();

            await Assert.That(error!.Message).IsEqualTo("Package catalog evaluation failed. error: attribute missing");
            await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(target)!)).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Nix_files_are_listed_relative_and_sorted_without_following_links()
    {
        var root = Directory.CreateTempSubdirectory("nix-manager-files-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "modules", "home"));
            File.WriteAllText(Path.Combine(root, "flake.nix"), "{}");
            File.WriteAllText(Path.Combine(root, "Z.nix"), "{}");
            File.WriteAllText(Path.Combine(root, "modules", "home", "default.nix"), "{}");
            File.WriteAllText(Path.Combine(root, "modules", "readme.md"), "");
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), Path.Combine(root, "modules"));

            await Assert.That(string.Join(",", Inventory.NixFiles(root))).IsEqualTo("Z.nix,flake.nix,modules/home/default.nix");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task An_installed_package_keeps_the_Bun_version_member_order_and_its_previous_version()
    {
        InstalledPackage package = new()
        {
            Out = "/nix/store/x-git-2.51.0", Name = "git", Version = "2.51.0", Description = null, File = "home.nix", Attr = "pkgs.git",
            Previous = JsonSerializer.SerializeToElement<string?>(null, ManagerJson.Default.String),
        };
        var inventory = new ScopeInventory { Scope = "home", Packages = [package, package with { Previous = default, Attr = null, External = true }] };

        var json = JsonNode.Parse(ManagerJson.Serialize(inventory))!;

        await Assert.That(string.Join(",", json["packages"]![0]!.AsObject().Select(member => member.Key)))
            .IsEqualTo("out,name,version,description,file,attr,external,previous");
        await Assert.That(json["packages"]![0]!["previous"]).IsNull();
        await Assert.That(json["packages"]![1]!.AsObject().ContainsKey("previous")).IsFalse();
        await Assert.That(json["packages"]![1]!.AsObject().ContainsKey("attr")).IsFalse();
        await Assert.That(json.AsObject().ContainsKey("unfree")).IsFalse();
    }

    [Test]
    public async Task Reviews_are_listed_newest_first_and_unreadable_ones_left_out()
    {
        var root = Directory.CreateTempSubdirectory("nix-manager-reviews-").FullName;
        try
        {
            var config = TestConfig(root);
            void Write(string id, string text)
            {
                Directory.CreateDirectory(Path.Combine(config.StateDir, "reviews", id));
                File.WriteAllText(Path.Combine(config.StateDir, "reviews", id, "review.json"), text);
            }
            Write("aaaaaaaa", """{"id":"aaaaaaaa","host":"test","scope":"home","createdAt":"2026-01-01T00:00:00.000Z","fingerprint":"","source":"checkout"}""");
            Write("bbbbbbbb", """{"id":"bbbbbbbb","host":"test","scope":"home","createdAt":"2026-02-01T00:00:00.000Z","fingerprint":"","source":"checkout"}""");
            Write("cccccccc", "not json");
            Directory.CreateDirectory(Path.Combine(config.StateDir, "reviews", "dddddddd"));

            await Assert.That(string.Join(",", SoftwareCli.ListReviews(config).Select(review => review.Id))).IsEqualTo("bbbbbbbb,aaaaaaaa");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Option_documentation_is_searched_by_name_first()
    {
        List<OptionDoc> docs =
        [
            new() { N = "programs.git.enable", D = "Whether to enable Git." },
            new() { N = "programs.git.userName", D = "Default user name to use." },
            new() { N = "programs.gitui.enable", D = "Whether to enable gitui." },
            new() { N = "services.git-daemon.enable", D = "Whether to enable Git daemon, which serves git repositories.", R = false },
            new() { N = "programs.git.package", D = "The git package.", R = true },
        ];

        await Assert.That(string.Join(",", OptionDocs.Search(docs, "programs.git").Select(doc => doc.N)))
            .IsEqualTo("programs.git.enable,programs.git.userName,programs.gitui.enable");
        await Assert.That(string.Join(",", OptionDocs.Search(docs, "git enable").Select(doc => doc.N)))
            .IsEqualTo("programs.git.enable,programs.gitui.enable,services.git-daemon.enable");
        await Assert.That(OptionDocs.Search(docs, " ")).IsEmpty();
    }

    [Test]
    public async Task The_memo_keeps_a_result_for_its_key_and_forgets_failures()
    {
        var memo = new Memo<int>();
        var calls = 0;
        Task<int> Compute() => Task.FromResult(++calls);
        await Assert.That(await memo.Get("a", "1", Compute)).IsEqualTo(1);
        await Assert.That(await memo.Get("a", "1", Compute)).IsEqualTo(1);
        await Assert.That(await memo.Get("a", "2", Compute)).IsEqualTo(2);
        var failed = memo.Get("b", "1", () => Task.FromException<int>(new ManagerException(DisplayText.Plain("no"))));
        await Assert.That(() => failed).Throws<ManagerException>();
        await Assert.That(await memo.Get("b", "1", Compute)).IsEqualTo(3);
        // A computation that fails later is forgotten as it fails.
        var later = new TaskCompletionSource<int>();
        var pending = memo.Get("c", "1", () => later.Task);
        later.SetException(new ManagerException(DisplayText.Plain("later")));
        await Assert.That(() => pending).Throws<ManagerException>();
        await Assert.That(await memo.Get("c", "1", Compute)).IsEqualTo(4);
    }

    [Test]
    public async Task The_software_service_reports_the_catalog_and_searches_it_once_loaded()
    {
        var root = Directory.CreateTempSubdirectory("nix-manager-store-").FullName;
        try
        {
            var config = TestConfig(root);
            var hash = new string('d', 64);
            var service = new SoftwareService(config, new FakeRunner());
            var changed = new SemaphoreSlim(0);
            service.Changed += () => changed.Release();

            await Assert.That(service.CatalogStatus(null).State).IsEqualTo("missing");
            await Assert.That(service.CatalogStatus(hash).State).IsEqualTo("missing");
            Directory.CreateDirectory(Path.GetDirectoryName(Catalog.PathFor(config, hash))!);
            File.WriteAllText(Catalog.PathFor(config, hash),
                """[{"a":"fzf","n":"fzf","v":"1","d":"Fuzzy finder","h":null,"p":"fzf","l":[],"f":true,"b":false,"g":false}]""");
            await Assert.That(service.CatalogStatus(hash).State).IsEqualTo("building");
            await Assert.That(await changed.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();

            var status = service.CatalogStatus(hash);
            await Assert.That(status.State).IsEqualTo("ready");
            await Assert.That(status.Count).IsEqualTo(1);
            await Assert.That(status.BuiltAt!).EndsWith("Z");
            await Assert.That(service.Search(hash, "fzf").Single().A).IsEqualTo("fzf");
            await Assert.That(service.Search(new string('e', 64), "fzf")).IsEmpty();
            await Assert.That(service.Lookup(hash, ["fzf", "git"]).Keys.Single()).IsEqualTo("fzf");
            await Assert.That(async () => { await service.ValuesAsync("", "home", ["bad option"]); }).Throws<ManagerException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
