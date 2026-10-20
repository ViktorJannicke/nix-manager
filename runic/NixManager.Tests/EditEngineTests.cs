using System.Text.Json.Nodes;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Nix;
using NixManager.Nix.Syntax;
using NixManager.Reviews;
using NixManager.Software;

namespace NixManager.Tests;

/// <summary>The edit engine: <c>test/packages.test.ts</c>, <c>test/options.test.ts</c>, <c>test/changes.test.ts</c> and the draft parts of <c>test/edits.test.ts</c>.</summary>
public sealed class EditEngineTests
{
    private static async Task Fails(Action action, string text)
    {
        var error = Assert.Throws<ManagerException>(action);
        await Assert.That(error.Message).Contains(text);
    }

    private static bool Valid(string source) => !NixSyntax.Parse(source).Root.HasError;

    // Shaped like the category modules built with `mkPackageModule`.
    private const string Category = """
        mkPackageModule {
          name = "media";
          default = false;
          packages = with pkgs; [
            gimp3-with-plugins
            easyeffects # audio effects
            vorta
          ];
        }

        """;

    private const string Kde = """
        {
          packages =
            (with pkgs.kdePackages; [
              kate
              dolphin
            ])
            ++ (with pkgs; [ partitionmanager ])
            ++ lib.optionals config.extra [ zoom ];
        }

        """;

    [Test]
    public async Task Package_lists_resolve_names_through_their_with_scope()
    {
        var lists = PackageLists.Find(NixSyntax.Parse(Kde).Root);
        await Assert.That(lists.Select(list => $"{list.Scope}|{list.Conditional}|{string.Join(",", list.Items.Select(item => item.Attr ?? "?"))}"))
            .IsEquivalentTo(new[]
            {
                "pkgs.kdePackages|False|pkgs.kdePackages.kate,pkgs.kdePackages.dolphin",
                "pkgs|False|pkgs.partitionmanager",
                // No `with` scope: a bare name could be a let binding, so it stays unresolved.
                "|True|?",
            });
    }

    [Test]
    public async Task Adding_keeps_every_other_byte_and_matches_the_lists_style()
    {
        var added = PackageLists.AddPackage(Category, "pkgs.krita");
        await Assert.That(added.Source).IsEqualTo(Category.Replace("    vorta\n", "    vorta\n    krita\n"));
        await Assert.That(added.Source[..added.Start] + added.Source[(added.Start + added.Text.Length)..]).IsEqualTo(Category);
        await Assert.That(Valid(added.Source)).IsTrue();
        var commented = Category.Replace("    vorta\n", "");
        await Assert.That(PackageLists.AddPackage(commented, "pkgs.krita").Source)
            .IsEqualTo(commented.Replace("easyeffects # audio effects\n", "easyeffects # audio effects\n    krita\n"));
    }

    [Test]
    public async Task The_longest_matching_with_scope_receives_the_package()
    {
        await Assert.That(PackageLists.AddPackage(Kde, "pkgs.kdePackages.kcalc").Source)
            .IsEqualTo(Kde.Replace("      dolphin\n", "      dolphin\n      kcalc\n"));
        await Assert.That(PackageLists.AddPackage(Kde, "pkgs.krita").Source)
            .IsEqualTo(Kde.Replace("[ partitionmanager ]", "[ partitionmanager krita ]"));
    }

    [Test]
    public async Task Qualified_lists_and_names_that_are_not_identifiers_are_written_in_full()
    {
        const string qualified = "{ pkgs, ... }: {\n  home.packages = [\n    pkgs.gh\n  ];\n}\n";
        await Assert.That(PackageLists.AddPackage(qualified, "pkgs.nixd").Source)
            .IsEqualTo(qualified.Replace("    pkgs.gh\n", "    pkgs.gh\n    pkgs.nixd\n"));
        await Assert.That(PackageLists.AddPackage(Category, "pkgs.\"7zz\"").Text).IsEqualTo("\n    pkgs.\"7zz\"");
    }

    [Test]
    public async Task Empty_lists_and_a_closing_bracket_on_the_item_line_stay_well_formed()
    {
        await Assert.That(PackageLists.AddPackage("{ packages = with pkgs; [ ]; }", "pkgs.bat").Source).IsEqualTo("{ packages = with pkgs; [ bat ]; }");
        await Assert.That(PackageLists.AddPackage("{\n  packages = with pkgs; [\n  ];\n}\n", "pkgs.bat").Source)
            .IsEqualTo("{\n  packages = with pkgs; [\n    bat\n  ];\n}\n");
        await Assert.That(PackageLists.AddPackage("{\n  packages = with pkgs; [\n    fzf ];\n}\n", "pkgs.bat").Source)
            .IsEqualTo("{\n  packages = with pkgs; [\n    fzf bat ];\n}\n");
    }

    [Test]
    public async Task Adding_refuses_duplicates_and_files_without_an_editable_list()
    {
        await Fails(() => PackageLists.AddPackage(Category, "pkgs.vorta"), "already listed");
        await Fails(() => PackageLists.AddPackage("{ packages = lib.optionals x [ a ]; }", "pkgs.bat"), "No editable package list");
        await Fails(() => PackageLists.AddPackage("{ packages = [ ", "pkgs.bat"), "syntax errors");
    }

    [Test]
    public async Task Removing_drops_the_items_line_or_just_the_item_when_it_shares_a_line()
    {
        await Assert.That(PackageLists.RemovePackage(Category, "pkgs.easyeffects").Source).IsEqualTo(Category.Replace("    easyeffects # audio effects\n", ""));
        await Assert.That(PackageLists.RemovePackage(Kde, "pkgs.partitionmanager").Source).IsEqualTo(Kde.Replace("[ partitionmanager ]", "[ ]"));
        await Fails(() => PackageLists.RemovePackage(Category, "pkgs.krita"), "not listed");
    }

    private const string Module = """
        { config, pkgs, ... }:
        let
          name = "viktor";
        in
        {
          # Shell
          programs.git = {
            enable = true;
            lfs.enable = false; # large files
          };
          programs.direnv.enable = true;
          home.username = name;
        }

        """;

    [Test]
    public async Task Values_render_as_Nix_with_strings_escaped()
    {
        await Assert.That(ModuleOptions.RenderValue(true)).IsEqualTo("true");
        await Assert.That(ModuleOptions.RenderValue(-3)).IsEqualTo("(-3)");
        await Assert.That(ModuleOptions.RenderValue(1.5)).IsEqualTo("1.5");
        await Assert.That(ModuleOptions.RenderValue("a \"b\" ${c}\n")).IsEqualTo("\"a \\\"b\\\" \\${c}\\n\"");
        await Assert.That(ModuleOptions.RenderValue(JsonNode.Parse("""["a","b"]"""))).IsEqualTo("[ \"a\" \"b\" ]");
        await Assert.That(ModuleOptions.RenderValue(JsonNode.Parse("""{"enable":true,"a b":4}"""))).IsEqualTo("{ enable = true; \"a b\" = 4; }");
        await Assert.That(ModuleOptions.RenderValue(null)).IsEqualTo("null");
        // JavaScript's number text: exponent forms through toFixed(12), and integer keys first.
        await Assert.That(ModuleOptions.RenderValue(JsonNode.Parse("1.5e-7"))).IsEqualTo("0.000000150000");
        await Assert.That(ModuleOptions.RenderValue(JsonNode.Parse("-2.5e-7"))).IsEqualTo("(-0.000000250000)");
        await Assert.That(ModuleOptions.RenderValue(JsonNode.Parse("1e21"))).IsEqualTo("1e+21");
        await Assert.That(ModuleOptions.RenderValue(JsonNode.Parse("""{"b":1,"2":2,"1":3}"""))).IsEqualTo("{ \"1\" = 3; \"2\" = 2; b = 1; }");
    }

    [Test]
    public async Task Existing_plain_values_are_replaced_in_place()
    {
        await Assert.That(ModuleOptions.SetOption(Module, "programs.git.lfs.enable", true).Source)
            .IsEqualTo(Module.Replace("lfs.enable = false;", "lfs.enable = true;"));
        await Assert.That(ModuleOptions.SetOption(Module, "programs.direnv.enable", false).Source)
            .IsEqualTo(Module.Replace("programs.direnv.enable = true;", "programs.direnv.enable = false;"));
    }

    [Test]
    public async Task New_options_join_the_deepest_set_that_holds_part_of_their_path()
    {
        await Assert.That(ModuleOptions.SetOption(Module, "programs.git.signing.signByDefault", true).Source).IsEqualTo(Module.Replace(
            "    lfs.enable = false; # large files\n", "    lfs.enable = false; # large files\n    signing.signByDefault = true;\n"));
        var added = ModuleOptions.SetOption(Module, "programs.zoxide.enable", true);
        await Assert.That(added.Source).IsEqualTo(Module.Replace("  home.username = name;\n", "  home.username = name;\n  programs.zoxide.enable = true;\n"));
        await Assert.That(Valid(added.Source)).IsTrue();
    }

    [Test]
    public async Task Modules_with_options_and_config_and_empty_modules_are_editable()
    {
        const string declared = "{ lib, ... }: {\n  options.my.flag = lib.mkEnableOption \"flag\";\n  config = {\n    services.a.enable = true;\n  };\n}\n";
        await Assert.That(ModuleOptions.SetOption(declared, "services.b.enable", true).Source)
            .IsEqualTo(declared.Replace("    services.a.enable = true;\n", "    services.a.enable = true;\n    services.b.enable = true;\n"));
        await Assert.That(ModuleOptions.SetOption("{ ... }: { }\n", "programs.fzf.enable", true).Source).IsEqualTo("{ ... }: { programs.fzf.enable = true; }\n");
        await Assert.That(ModuleOptions.SetOption("{ ... }:\n{\n}\n", "programs.fzf.enable", true).Source).IsEqualTo("{ ... }:\n{\n  programs.fzf.enable = true;\n}\n");
    }

    [Test]
    public async Task Computed_values_split_definitions_and_non_modules_are_refused()
    {
        await Fails(() => ModuleOptions.SetOption(Module, "home.username", "x"), "computed here");
        await Fails(() => ModuleOptions.SetOption(Module, "programs", JsonNode.Parse("""{"git":{"enable":true}}""")), "separate parts");
        await Fails(() => ModuleOptions.SetOption("mkPackageModule { name = \"x\"; }", "a.b", true), "not a module");
        await Fails(() => ModuleOptions.SetOption(Module, "programs.${x}.enable", true), "Unsupported option path");
        // JavaScript's `$` does not match before a final newline.
        await Fails(() => ModuleOptions.SetOption(Module, "programs.x\n", true), "Unsupported option path");
    }

    [Test]
    public async Task Unsetting_removes_the_bindings_line_or_just_the_binding()
    {
        await Assert.That(ModuleOptions.UnsetOption(Module, "programs.git.lfs.enable").Source).IsEqualTo(Module.Replace("    lfs.enable = false; # large files\n", ""));
        await Assert.That(ModuleOptions.UnsetOption("{ a.b = 1; c = 2; }", "a.b").Source).IsEqualTo("{ c = 2; }");
        await Fails(() => ModuleOptions.UnsetOption(Module, "programs.fzf.enable"), "not set");
    }

    [Test]
    public async Task Module_shape_lists_names_setting_paths_and_static_imports()
    {
        var shape = ModuleOptions.Shape(Module);
        await Assert.That(shape.Editable).IsTrue();
        await Assert.That(shape.Names).IsEquivalentTo(new[] { "programs.git", "programs.direnv.enable", "home.username" });
        await Assert.That(shape.Settings).IsEquivalentTo(new[] { "programs.git.enable", "programs.git.lfs.enable", "programs.direnv.enable", "home.username" });
        await Assert.That(shape.Imports).IsEmpty();
        await Assert.That(shape.Importable).IsTrue();
        await Assert.That(ModuleOptions.Shape("mkPackageModule { }").Editable).IsFalse();
        const string aggregate = "{ lib, ... }: {\n  imports = [ ./git.nix ../shared ./missing.nix ];\n  options.my.flag = lib.mkEnableOption \"flag\";\n  config.home.stateVersion = \"25.05\";\n}\n";
        var shaped = ModuleOptions.Shape(aggregate);
        await Assert.That(shaped.Editable).IsFalse();
        await Assert.That(shaped.Imports).IsEquivalentTo(new[] { "./git.nix", "../shared", "./missing.nix" });
        await Assert.That(shaped.Importable).IsTrue();
        await Assert.That(ModuleOptions.Shape("{ imports = lib.optionals x [ ./a.nix ]; }").Imports).IsEmpty();
        await Assert.That(ModuleOptions.Shape("{ imports = lib.optionals x [ ./a.nix ]; }").Importable).IsFalse();
        var commented = ModuleOptions.Shape("# Shared host settings\n\n{ pkgs, ... }:\nlet x = 1; in\n{\n  imports = [ ./a.nix ];\n  services.x.enable = true;\n}\n");
        await Assert.That(commented.Editable).IsTrue();
        await Assert.That(commented.Imports).IsEquivalentTo(new[] { "./a.nix" });
        await Assert.That(commented.Importable).IsTrue();
    }

    [Test]
    public async Task Imports_are_added_to_the_list_in_its_layout_or_start_one()
    {
        const string aggregate = "{ lib, ... }:\n{\n  imports = [\n    ./git.nix # vcs\n  ];\n  options.x = 1;\n}\n";
        await Assert.That(ModuleOptions.AddImport(aggregate, "./fzf.nix").Source)
            .IsEqualTo("{ lib, ... }:\n{\n  imports = [\n    ./git.nix # vcs\n    ./fzf.nix\n  ];\n  options.x = 1;\n}\n");
        await Assert.That(ModuleOptions.AddImport("{ imports = [ ./a.nix ]; }", "../b/c.nix").Source).IsEqualTo("{ imports = [ ./a.nix ../b/c.nix ]; }");
        await Assert.That(ModuleOptions.AddImport("{ pkgs, ... }:\n{\n  home.x = 1;\n}\n", "./fzf.nix").Source)
            .IsEqualTo("{ pkgs, ... }:\n{\n  home.x = 1;\n  imports = [ ./fzf.nix ];\n}\n");
        await Fails(() => ModuleOptions.AddImport("{ imports = [ ./a.nix ]; }", "./a.nix"), "already imports");
        await Fails(() => ModuleOptions.AddImport("{ imports = map f xs; }", "./a.nix"), "computes its imports");
        await Fails(() => ModuleOptions.AddImport("{ }", "./a b.nix"), "Nix path");
    }

    [Test]
    public async Task A_list_option_starts_in_a_module_without_one()
    {
        await Assert.That(ModuleOptions.StartList("{ pkgs, ... }:\n{\n}\n", "home.packages", "pkgs.fzf").Source)
            .IsEqualTo("{ pkgs, ... }:\n{\n  home.packages = [ pkgs.fzf ];\n}\n");
        await Fails(() => ModuleOptions.StartList("{ home.packages = cfg.extra; }", "home.packages", "pkgs.fzf"), "computed");
    }

    [Test]
    public async Task Settings_beside_option_declarations_need_a_config_set()
    {
        const string conditional = "{ lib, config, ... }: {\n  options.my.flag = lib.mkEnableOption \"flag\";\n  config = lib.mkIf config.my.flag { services.a.enable = true; };\n}\n";
        await Fails(() => ModuleOptions.SetOption(conditional, "services.b.enable", true), "not a module");
        await Fails(() => ModuleOptions.SetOption("{ lib, ... }: { options.my.flag = lib.mkEnableOption \"x\"; }", "a.b", true), "not a module");
    }

    private static readonly Change Add = new AddPackage("home", "a.nix", "pkgs.btop");
    private static Change Set(JsonNode? value, string file = "a.nix") => new SetOption("home", file, "programs.fzf.enable", value);
    private static readonly Change Create = new CreateModule("home", "m/fzf.nix", "m/default.nix");

    private static string Text(IEnumerable<Change> changes) => JsJson.Stringify(Change.ToJson(changes));

    [Test]
    public async Task Edits_replace_cancel_and_deduplicate_pending_edits()
    {
        await Assert.That(Text(ChangeRules.Merge([Add], [Add]))).IsEqualTo(Text([Add]));
        await Assert.That(ChangeRules.Merge([Add], [new RemovePackage("home", "a.nix", "pkgs.btop")])).IsEmpty();
        await Assert.That(Text(ChangeRules.Merge([Set(true)], [Set(false)]))).IsEqualTo(Text([Set(false)]));
        await Assert.That(ChangeRules.Merge([Set(true)], [Set(false)], _ => true)).IsEmpty();
        await Assert.That(Text(ChangeRules.Merge([Create], [Create, Set(true, Create.File)]))).IsEqualTo(Text([Create, Set(true, Create.File)]));
        await Assert.That(ChangeRules.ChangesScope([Add])).IsEqualTo("home");
        await Assert.That(ChangeRules.ChangesScope([Add, new AddPackage("system", "a.nix", "pkgs.btop")])).IsEqualTo("all");
    }

    [Test]
    public async Task Dropping_a_created_file_drops_the_edits_written_into_it()
    {
        var into = Set(true, Create.File);
        await Assert.That(Text(ChangeRules.Drop([Add, Create, into], 1))).IsEqualTo(Text([Add]));
        await Assert.That(Text(ChangeRules.Drop([Add, Create, into], 2))).IsEqualTo(Text([Add, Create]));
    }

    [Test]
    public async Task Features_go_into_their_own_module_or_a_new_one_beside_single_topic_modules()
    {
        var services = new[] { "bluetooth", "podman", "ssh" }.Select(name => new ModuleFile($"modules/nixos/services/{name}.nix", [], true)).ToList();
        await Assert.That(ChangeRules.FeatureModule(services, "podman")).IsEqualTo("modules/nixos/services/podman.nix");
        await Assert.That(ChangeRules.FeatureModule(services, "flatpak")).IsNull();
        await Assert.That(ChangeRules.FeatureModule([new("configuration.nix", [], true), new("hardware.nix", [], true)], "flatpak")).IsEqualTo("configuration.nix");
        await Assert.That(ChangeRules.FeatureModule([new("old/flatpak.nix", [], false), new("configuration.nix", [], true)], "flatpak")).IsEqualTo("configuration.nix");
        await Assert.That(ChangeRules.FeatureModule([], "flatpak")).IsNull();
    }

    [Test]
    public async Task New_module_paths_are_free_valid_and_imported_from_the_closest_folder()
    {
        var known = new HashSet<string> { "modules/home/programs/fzf.nix" };
        await Assert.That(ChangeRules.FreePath(known, "fzf", "modules/home/programs/git.nix", "modules/home")).IsEqualTo("modules/home/programs/fzf-2.nix");
        await Assert.That(ChangeRules.FreePath(known, "Virtual Machines", null, "modules/nixos")).IsEqualTo("modules/nixos/virtual-machines.nix");
        await Assert.That(ChangeRules.NewFileProblem(known, "modules/home/programs/fzf.nix")).Contains("exists");
        await Assert.That(ChangeRules.NewFileProblem(known, "../x.nix")).Contains("inside the flake");
        await Assert.That(ChangeRules.NewFileProblem(known, "modules/x.txt")).Contains(".nix");
        await Assert.That(ChangeRules.NewFileProblem(known, "modules/x.nix")).IsNull();
        await Assert.That(ChangeRules.RankImporters(
                ["hosts/desktop/home.nix", "modules/home/default.nix", "modules/home/programs/default.nix", "modules/home/programs/git.nix"], "modules/home/programs/fzf.nix"))
            .IsEquivalentTo(new[] { "modules/home/programs/default.nix", "modules/home/programs/git.nix", "modules/home/default.nix", "hosts/desktop/home.nix" });
        var services = new[] { "ssh", "podman" }.Select(name => $"modules/nixos/services/{name}.nix").ToList();
        var imports = new Dictionary<string, IReadOnlyList<string>>
        {
            ["profiles/workstation.nix"] = services,
            ["hosts/desktop/system.nix"] = ["profiles/workstation.nix", "modules/nixos/services/libvirt.nix"],
        };
        await Assert.That(ChangeRules.RankImporters(["hosts/desktop/system.nix", "profiles/workstation.nix"], "modules/nixos/services/flatpak.nix", imports)[0])
            .IsEqualTo("profiles/workstation.nix");
    }

    [Test]
    public async Task Non_free_packages_are_accepted_by_allowUnfree_by_name_or_by_a_predicate()
    {
        var policy = new UnfreePolicy { All = false, Packages = ["steam"], Predicate = false };
        await Assert.That(ChangeRules.AcceptsUnfree(policy, "steam")).IsTrue();
        await Assert.That(ChangeRules.AcceptsUnfree(policy, "discord")).IsFalse();
        await Assert.That(ChangeRules.AcceptsUnfree(policy, null)).IsFalse();
        await Assert.That(ChangeRules.AcceptsUnfree(policy with { All = true }, "discord")).IsTrue();
        await Assert.That(ChangeRules.AcceptsUnfree(policy with { Predicate = true }, "discord")).IsTrue();
        await Assert.That(ChangeRules.AcceptsUnfree(null, "discord")).IsTrue();
    }

    [Test]
    public async Task Only_installing_and_removing_your_own_apps_is_applied_in_one_step()
    {
        var remove = new RemovePackage("home", "a.nix", "pkgs.btop");
        var unfree = new SetOption("home", "a.nix", "nixpkgs.config.allowUnfree", true);
        var module = new CreateModule("home", "b.nix", "a.nix");
        await Assert.That(ChangeRules.OneStepProblem([unfree, module, Add, remove])).IsNull();
        await Assert.That(ChangeRules.OneStepProblem([])).IsNotNull();
        await Assert.That(ChangeRules.OneStepProblem([new AddPackage("system", "a.nix", "pkgs.btop")])).Contains("computer itself");
        await Assert.That(ChangeRules.OneStepProblem([Add, new SetOption("home", "a.nix", "nixpkgs.config.allowUnfree", false)])).Contains("settings");
        await Assert.That(ChangeRules.OneStepProblem([new SetOption("home", "a.nix", "services.syncthing.enable", true)])).Contains("settings");

        BuildReview Review() => new()
        {
            Id = "abababab", Host = "test", Scope = "home", CreatedAt = "", Fingerprint = "", Source = "checkout",
            Edits = new ReviewEdits { Changes = [Add], Hash = "", Effects = [new EditEffect { Scope = "home" }] },
            Home = new ReviewOutput { Path = "/nix/store/home", Compared = true },
        };
        await Assert.That(ChangeRules.OneStepReviewProblem(Review())).IsNull();
        var system = Review();
        system.System = system.Home;
        await Assert.That(ChangeRules.OneStepReviewProblem(system)).Contains("computer itself");
        var unedited = Review();
        unedited.Edits = null;
        await Assert.That(ChangeRules.OneStepReviewProblem(unedited)).IsNotNull();
        var setting = Review();
        setting.Edits!.Effects = [new EditEffect { Scope = "home", Options = [new OptionEffect { Option = "xdg.enable" }] }];
        await Assert.That(ChangeRules.OneStepReviewProblem(setting)).Contains("settings");
    }

    [Test]
    public async Task Changes_are_described_in_words()
    {
        await Assert.That(ChangeRules.Describe(Add)).IsEqualTo(new ChangeText("Install btop", "a.nix"));
        await Assert.That(ChangeRules.Describe(Set(true)).Title).IsEqualTo("programs.fzf.enable on");
        await Assert.That(ChangeRules.Describe(new SetOption("home", "a.nix", "x.y", JsonNode.Parse("[1,\"a\",null,[2,3]]"), "Shell")).Title)
            .IsEqualTo("Shell: x.y 1, a, , 2,3");
        await Assert.That(ChangeRules.Describe(new SetOption("home", "a.nix", "x.y", new JsonArray())).Title).IsEqualTo("x.y empty");
        await Assert.That(ChangeRules.Describe(new SetOption("home", "a.nix", "x.y", null, "")).Title).IsEqualTo("x.y not set");
        await Assert.That(ChangeRules.Describe(new UnsetOption("home", "a.nix", "x.y")).Title).IsEqualTo("Reset x.y to its default");
        await Assert.That(ChangeRules.Describe(Create)).IsEqualTo(new ChangeText("Create fzf.nix", "m/fzf.nix, imported from m/default.nix"));
        await Assert.That(ChangeRules.ShowValue(JsonNode.Parse("""{"a":1}"""))).IsEqualTo("{\"a\":1}");
        await Assert.That(ChangeRules.OptionTopic("programs.fzf.enable")).IsEqualTo("fzf");
        await Assert.That(ChangeRules.OptionTopic("home.packages")).IsEqualTo("home");
        await Assert.That(ChangeRules.Origin("/nix/store/0123456789abcdefghijklmnopqrstuv-source/nixos/modules/services/x.nix")).IsEqualTo("NixOS: services/x.nix");
        await Assert.That(ChangeRules.Origin("/nix/store/0123456789abcdefghijklmnopqrstuv-source/modules/programs/git.nix")).IsEqualTo("Home Manager: programs/git.nix");
        await Assert.That(ChangeRules.Origin("modules/a.nix")).IsEqualTo("modules/a.nix");
    }

    private sealed class Checkout : IDisposable
    {
        public Config Config { get; }
        public string Flake => Config.Flake;

        public Checkout()
        {
            Config = TestConfig.Create(Operations.Native.RealPath(TestConfig.TemporaryFlake()));
            Directory.CreateDirectory(Path.Combine(Flake, "modules"));
            File.WriteAllText(Path.Combine(Flake, "modules/apps.nix"), "{ pkgs, ... }: {\n  home.packages = with pkgs; [\n    git\n  ];\n}\n");
        }

        public void Dispose() => Directory.Delete(Flake, true);
    }

    private static readonly Change Btop = new AddPackage("home", "modules/apps.nix", "pkgs.btop");

    [Test]
    public async Task Line_diffs_group_changes_into_hunks_with_context()
    {
        var hunks = Drafts.DiffLines(string.Join("\n", "abcdefgh".Select(c => c.ToString())), string.Join("\n", "aBcdefghi".Select(c => c.ToString())), 1);
        await Assert.That(hunks.Count).IsEqualTo(2);
        await Assert.That(hunks[0].Line).IsEqualTo(1);
        await Assert.That(hunks[0].Lines).IsEquivalentTo(new DiffLine[] { new(" ", "a"), new("-", "b"), new("+", "B"), new(" ", "c") });
        await Assert.That(hunks[1].Line).IsEqualTo(8);
        await Assert.That(hunks[1].Lines).IsEquivalentTo(new DiffLine[] { new(" ", "h"), new("+", "i") });
    }

    [Test]
    public async Task Drafts_apply_changes_in_order_and_report_the_ones_that_cannot_apply()
    {
        using var checkout = new Checkout();
        var (files, problems) = Drafts.Render(checkout.Flake,
        [
            Btop,
            new AddPackage("home", "modules/apps.nix", "pkgs.git"),
            new SetOption("home", "modules/apps.nix", "programs.bat.enable", true),
            new AddPackage("home", "../outside.nix", "pkgs.btop"),
        ]);
        await Assert.That(files[0].After).IsEqualTo("{ pkgs, ... }: {\n  home.packages = with pkgs; [\n    git\n    btop\n  ];\n  programs.bat.enable = true;\n}\n");
        await Assert.That(problems.Select(problem => problem.Index)).IsEquivalentTo(new[] { 1, 3 });
        await Assert.That(problems[0].Message).Contains("already listed");
        await Assert.That(problems[1].Message).IsEqualTo("Invalid file: ../outside.nix");
    }

    [Test]
    public async Task A_created_module_is_imported_by_its_parent_and_takes_later_edits()
    {
        using var checkout = new Checkout();
        await Assert.That(Drafts.ImportPath("modules/default.nix", "modules/programs/fzf.nix")).IsEqualTo("./programs/fzf.nix");
        await Assert.That(Drafts.ImportPath("modules/home/default.nix", "modules/fzf.nix")).IsEqualTo("../fzf.nix");
        var create = new CreateModule("home", "modules/programs/fzf.nix", "modules/apps.nix");
        var (files, problems) = Drafts.Render(checkout.Flake,
        [
            create,
            new SetOption("home", create.File, "programs.fzf.enable", true),
            new AddPackage("home", create.File, "pkgs.fd"),
            create,
            new CreateModule("home", "modules/apps.nix", "modules/apps.nix"),
        ]);
        await Assert.That(problems.Select(problem => $"{problem.Index} {problem.Message}"))
            .IsEquivalentTo(new[] { "3 modules/programs/fzf.nix already exists.", "4 modules/apps.nix already exists." });
        await Assert.That(files.Select(file => $"{file.Path} {file.Created ?? false} {file.BaseHash == ""}"))
            .IsEquivalentTo(new[] { "modules/apps.nix False False", "modules/programs/fzf.nix True True" });
        await Assert.That(files[0].After).Contains("  imports = [ ./programs/fzf.nix ];\n}");
        await Assert.That(files[1].After).IsEqualTo("{ pkgs, ... }:\n{\n  programs.fzf.enable = true;\n  home.packages = [ pkgs.fd ];\n}\n");
        await Assert.That(files[1].Hunks[0].Lines.Any(line => line.Kind == "-")).IsFalse();
    }

    [Test]
    public async Task Source_paths_stay_inside_the_flake()
    {
        using var checkout = new Checkout();
        await Assert.That(Drafts.SourcePath(checkout.Flake, "modules/apps.nix")).IsEqualTo($"{checkout.Flake}/modules/apps.nix");
        foreach (var file in new[] { "/etc/x.nix", "../x.nix", "a.txt", "./a.nix", "a//b.nix", "modules/../a.nix", "..x.nix" })
            await Fails(() => Drafts.SourcePath(checkout.Flake, file), $"Invalid file: {file}");
        var outside = Directory.CreateTempSubdirectory("nix-manager-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "x.nix"), "{ }");
            File.CreateSymbolicLink(Path.Combine(checkout.Flake, "link.nix"), Path.Combine(outside, "x.nix"));
            await Fails(() => Drafts.SourcePath(checkout.Flake, "link.nix"), "link.nix is outside the flake.");
            // A missing file is fine: it is checked when read.
            await Assert.That(Drafts.SourcePath(checkout.Flake, "new.nix")).IsEqualTo($"{checkout.Flake}/new.nix");
            var (_, problems) = Drafts.Render(checkout.Flake, [new AddPackage("home", "new.nix", "pkgs.x")]);
            await Assert.That(problems[0].Message).IsEqualTo("new.nix does not exist.");
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Test]
    public async Task The_draft_is_stored_for_its_checkout_only()
    {
        using var checkout = new Checkout();
        var config = checkout.Config;
        await Assert.That(Drafts.Read(config)).IsEmpty();
        Drafts.Write(config, [Btop, Set(true)]);
        var path = Path.Combine(config.StateDir, "draft.json");
        await Assert.That(File.ReadAllText(path)).IsEqualTo(JsJson.Indented(new JsonObject
        {
            ["flake"] = config.Flake,
            ["changes"] = JsonNode.Parse("""
                [{"kind":"add-package","scope":"home","file":"modules/apps.nix","attr":"pkgs.btop"},
                 {"kind":"set-option","scope":"home","file":"a.nix","option":"programs.fzf.enable","value":true}]
                """),
        }) + "\n");
        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(File.GetUnixFileMode(config.StateDir)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.That(Drafts.Read(config).SequenceEqual([Btop, Set(true)])).IsTrue();
        await Assert.That(Drafts.Read(config with { Flake = "/elsewhere" })).IsEmpty();
        File.WriteAllText(path, """{"flake":"x","changes":[{"kind":"nope"}]}""".Replace("\"x\"", JsJson.Quote(config.Flake)));
        await Assert.That(Drafts.Read(config)).IsEmpty();
        File.WriteAllText(path, "not json");
        await Assert.That(Drafts.Read(config)).IsEmpty();
        Drafts.Write(config, []);
        await Assert.That(Drafts.Read(config)).IsEmpty();
        await Assert.That(Directory.GetFiles(config.StateDir).Select(Path.GetFileName)).IsEquivalentTo(new[] { "draft.json" });
    }

    [Test]
    public async Task A_draft_written_before_edits_were_typed_still_reads()
    {
        using var checkout = new Checkout();
        var config = checkout.Config;
        // Members in any order, the kind last, unknown members, and option values of every shape.
        Directory.CreateDirectory(config.StateDir);
        File.WriteAllText(Path.Combine(config.StateDir, "draft.json"), $$"""
            {
              "flake": {{JsJson.Quote(config.Flake)}},
              "changes": [
                { "kind": "add-package", "attr": "pkgs.btop", "scope": "home", "file": "modules/apps.nix" },
                { "file": "a.nix", "value": { "b": [1.50, null], "a": "x" }, "option": "o.p", "scope": "system", "kind": "set-option", "label": "L", "extra": 1 },
                { "kind": "set-option", "scope": "home", "file": "a.nix", "option": "o.q", "value": null },
                { "kind": "unset-option", "scope": "home", "file": "a.nix", "option": "o.r" },
                { "kind": "create-module", "scope": "home", "file": "m/new.nix", "importer": "m/default.nix", "label": "New" },
                { "kind": "remove-package", "scope": "home", "file": "a.nix", "attr": "pkgs.git" }
              ]
            }
            """);

        await Assert.That(Drafts.Read(config).SequenceEqual([
            Btop,
            new SetOption("system", "a.nix", "o.p", JsonNode.Parse("""{ "a": "x", "b": [1.5, null] }"""), "L"),
            new SetOption("home", "a.nix", "o.q", null),
            new UnsetOption("home", "a.nix", "o.r"),
            new CreateModule("home", "m/new.nix", "m/default.nix", "New"),
            new RemovePackage("home", "a.nix", "pkgs.git"),
        ])).IsTrue();
    }

    [Test]
    public async Task Edits_are_equal_by_their_members_and_option_values_by_their_JSON()
    {
        await Assert.That(Set(JsonNode.Parse("""{ "a": [1, true] }"""))).IsEqualTo(Set(JsonNode.Parse("""{"a":[1,true]}""")));
        await Assert.That(Set(JsonNode.Parse("""{ "a": [1, true] }""")).GetHashCode()).IsEqualTo(Set(JsonNode.Parse("""{"a":[1,true]}""")).GetHashCode());
        await Assert.That(Set(true)).IsNotEqualTo(Set(false));
        await Assert.That(Set(null)).IsNotEqualTo(Set(JsonValue.Create("")));
        await Assert.That<Change>(new AddPackage("home", "a.nix", "pkgs.x")).IsNotEqualTo(new RemovePackage("home", "a.nix", "pkgs.x"));
        await Assert.That(new AddPackage("home", "a.nix", "pkgs.x", "L")).IsNotEqualTo(new AddPackage("home", "a.nix", "pkgs.x"));
        // A review's edits leave the draft by value, each once.
        await Assert.That(SourceEdits.Remaining([Add, Set(true), Add, Create], [Set(JsonValue.Create(true)), Add])).IsEquivalentTo([Add, Create]);
    }

    [Test]
    public async Task The_public_draft_carries_hunks_and_the_change_hash()
    {
        using var checkout = new Checkout();
        var draft = Drafts.Public(checkout.Flake, [Btop]);
        await Assert.That(draft.Hash).IsEqualTo(Change.Hash([Btop]));
        await Assert.That(draft.Files.Single().Path).IsEqualTo("modules/apps.nix");
        await Assert.That(draft.Files.Single().Created).IsNull();
        await Assert.That(draft.Files.Single().Hunks.Single().Lines.Count(line => line.Kind == "+")).IsEqualTo(1);
    }
}
