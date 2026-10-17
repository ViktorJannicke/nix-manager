using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Operations;

namespace NixManager.Software;

/// <summary>A package of a configuration (<c>PackageInfo</c>).</summary>
public record PackageInfo
{
    public required string Out { get; init; }
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Version { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Description { get; init; }
}

/// <summary>An option's value and the files that define it (<c>OptionValue</c>).</summary>
public sealed record OptionValue
{
    /// <summary>The value as JSON; undefined (default) when it does not evaluate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault), Runic.Application.Views.RunicBridgeCodec(typeof(ViewModels.JsonElementBridgeCodec))] public JsonElement Value { get; init; }
    /// <summary>Evaluation failed, or the option does not exist.</summary>
    public bool? Error { get; init; }
    public bool Exists { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>For a <c>nixpkgs.config</c> setting, the value the configuration's package set uses.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault), Runic.Application.Views.RunicBridgeCodec(typeof(ViewModels.JsonElementBridgeCodec))] public JsonElement Effective { get; init; }

    [JsonIgnore] public bool HasValue => Value.ValueKind != JsonValueKind.Undefined;
    [JsonIgnore] public bool HasEffective => Effective.ValueKind != JsonValueKind.Undefined;
}

/// <summary>Which non-free packages a configuration's package set accepts (<c>UnfreePolicy</c>).</summary>
public sealed record UnfreePolicy
{
    /// <summary><c>allowUnfree</c>.</summary>
    public bool All { get; init; }
    /// <summary><c>allowUnfreePackages</c>: package names allowed one by one.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];
    /// <summary>An <c>allowUnfreePredicate</c> decides, which cannot be asked without the package.</summary>
    public bool Predicate { get; init; }
}

/// <summary>An account that can log in, and the flake files that define it (<c>UserAccount</c>).</summary>
public sealed record UserAccount
{
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Description { get; init; }
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>A setting a flake file writes, to check whether the configuration imports that file (<c>Probe</c>).</summary>
public sealed record Probe(string File, string Option);

public sealed record FilePackages(string File, IReadOnlyList<PackageInfo> Items);

public sealed record NixpkgsFingerprint([property: Runic.Application.Views.RunicBridgeCodec(typeof(ViewModels.JsonNodeBridgeCodec))] JsonObject Config, string Names);

/// <summary>One evaluation of a configuration (<c>ConfigurationState</c>).</summary>
public sealed record ConfigurationState
{
    /// <summary>Packages each file adds to the package option; flake files are relative.</summary>
    public IReadOnlyList<FilePackages> Packages { get; init; } = [];
    public IReadOnlyDictionary<string, OptionValue> Options { get; init; } = new Dictionary<string, OptionValue>();
    /// <summary>Output path of each requested <c>pkgs</c> attribute, or null.</summary>
    public IReadOnlyList<string?> Resolved { get; init; } = [];
    /// <summary>For each probe, whether the option at its path has a definition in its file.</summary>
    public IReadOnlyList<bool> Used { get; init; } = [];
    public NixpkgsFingerprint? Nixpkgs { get; init; }
    public UnfreePolicy? Unfree { get; init; }
    public IReadOnlyList<UserAccount>? Users { get; init; }
}

/// <summary>What <see cref="Evaluation.EvaluateStateAsync"/> reads besides the packages.</summary>
public sealed record StateRequest
{
    public IReadOnlyList<string> Options { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<string>> Attrs { get; init; } = [];
    public IReadOnlyList<Probe> Probes { get; init; } = [];
    /// <summary>Describe the package set, to notice an edit that replaces it.</summary>
    public bool Nixpkgs { get; init; }
    /// <summary>List the user accounts that can log in.</summary>
    public bool Users { get; init; }
}

/// <summary>Evaluating a host's configurations (<c>configurationAttr</c>, <c>evaluateState</c>, <c>flakeSource</c>).</summary>
public static class Evaluation
{
    /// <summary>Attribute of a host's Home Manager or NixOS configuration (<c>configurationAttr</c>).</summary>
    public static string ConfigurationAttr(Config config, string host, string scope)
    {
        var resolved = HostResolution.Resolve(config.Hosts, host is { Length: > 0 } ? host : config.HostKey).Value;
        return scope == "home"
            ? $"homeConfigurations.{NixText.Attr(resolved.HomeConfiguration)}"
            : $"nixosConfigurations.{NixText.Attr(resolved.NixosConfiguration)}";
    }

    /// <summary>Store copy of the flake's tracked files, uncommitted edits included (<c>flakeSource</c>).</summary>
    public static async Task<string> FlakeSourceAsync(string flake, Func<Command, Task<CommandResult>> run)
    {
        var result = await run(new Command { Label = "Read flake source", Argv = ["nix", "flake", "metadata", "--json", flake], Quiet = true }).ConfigureAwait(false);
        try
        {
            if (JsonNode.Parse(result.Stdout)?["path"] is JsonValue path && path.TryGetValue<string>(out var text)) return text;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        throw new ManagerException("Invalid flake metadata: the path is missing.");
    }

    private static string NixString(string text) =>
        $"\"{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("${", "\\${", StringComparison.Ordinal)}\"";

    private static string NixPaths(IEnumerable<IReadOnlyList<string>> paths) =>
        $"[ {string.Join(' ', paths.Select(path => $"[ {string.Join(' ', path.Select(NixString))} ]"))} ]";

    /// <summary>Names the module system may record for a flake file: <c>dir/default.nix</c> is also imported as <c>dir</c>.</summary>
    private static string[] ModuleKeys(string source, string file)
    {
        var path = $"{source}/{file}";
        return file.EndsWith("/default.nix", StringComparison.Ordinal) ? [path, path[..^"/default.nix".Length]]
            : file == "default.nix" ? [path, source]
            : [path];
    }

    /// <summary>Evaluation helpers shared by every expression applied to a configuration.</summary>
    public const string Prelude = """

          inherit (builtins) tryEval isAttrs isList isFunction concatMap elem;
          get = path: set:
            if path == [ ] then set
            else if isAttrs set && set ? ${builtins.head path} then get (builtins.tail path) set.${builtins.head path}
            else null;
          str = v: if builtins.isString v then v else null;
          # JSON-safe data: packages become store paths, functions disappear.
          plain = depth: v:
            if depth > 8 || isFunction v then null
            else if isAttrs v then (if v ? outPath then toString v.outPath else builtins.mapAttrs (_: plain (depth + 1)) v)
            else if isList v then map (plain (depth + 1)) v
            else if builtins.isPath v then toString v
            else v;

        """;

    private const string NixpkgsExpression = """
        {
            config = builtins.mapAttrs (_: v: let r = tryEval (let x = plain 0 v; in builtins.deepSeq x x); in if r.success then r.value else "<error>") c.pkgs.config;
            names = builtins.hashString "sha256" (builtins.concatStringsSep "," (builtins.attrNames c.pkgs));
          }
        """;

    private const string UsersExpression = """

            let r = tryEval (let
              users = c.config.users.users;
              defs = c.options.users.users.definitionsWithLocations;
              names = builtins.filter (n: users.${n}.isNormalUser or false) (builtins.attrNames users);
              mentions = n: d: let t = tryEval (isAttrs d.value && d.value ? ${n}); in t.success && t.value;
              x = map (n: {
                name = n;
                description = let d = users.${n}.description or ""; in if builtins.isString d && d != "" then d else null;
                groups = builtins.filter builtins.isString (users.${n}.extraGroups or [ ]);
                files = map (d: toString d.file) (builtins.filter (mentions n) defs);
              }) names;
            in builtins.deepSeq x x);
            in if r.success then r.value else null
        """;

    public static string PackagesOption(string scope) => scope == "home" ? "home.packages" : "environment.systemPackages";

    /// <summary>The expression <see cref="EvaluateStateAsync"/> applies to the configuration.</summary>
    public static string StateExpression(string source, string scope, StateRequest request, IReadOnlyList<string> options)
    {
        var probes = string.Join(' ', request.Probes.Select(probe =>
            $"{{ files = [ {string.Join(' ', ModuleKeys(source, probe.File).Select(NixString))} ]; path = [ {string.Join(' ', probe.Option.Split('.').Select(NixString))} ]; }}"));
        return $$"""
            c:
            let
              {{Prelude}}
              info = p:
                let v = {
                  out = p.outPath;
                  name = (p.pname or (builtins.parseDrvName (p.name or "unknown")).name);
                  version = str (p.version or null);
                  description = str (p.meta.description or null);
                }; r = tryEval (builtins.deepSeq v v);
                in if r.success then [ r.value ] else [ ];
              # List items can carry their own mkIf, mkOverride or mkOrder.
              items = p:
                if !isAttrs p then [ ]
                else if p ? outPath then info p
                else if (p._type or "") == "if" then (if p.condition then items p.content else [ ])
                else if elem (p._type or "") [ "override" "order" ] then items p.content
                else [ ];
              # A setting below an option, such as nixpkgs.config.allowUnfree, exists when
              # the option's value is a set; its files are those whose definition has it.
              value = path:
                let
                  at = optionAt path c.options;
                  below = at != null && at.rest != [ ];
                  parent = tryEval (isAttrs (get (builtins.genList (builtins.elemAt path) (builtins.length path - 1)) c.config));
                  exists = at != null && (!below || (parent.success && parent.value));
                  v = tryEval (let x = plain 0 (get path c.config); in builtins.deepSeq x x);
                  defines = d: let r = tryEval (isAttrs d.value && get at.rest d.value != null); in r.success && r.value;
                  files = tryEval (let x =
                    if !exists then [ ]
                    else if below then map (d: toString d.file) (builtins.filter defines at.option.definitionsWithLocations)
                    else map toString at.option.files; in builtins.deepSeq x x);
                  nixpkgsConfig = builtins.length path > 2 && builtins.head path == "nixpkgs" && builtins.elemAt path 1 == "config";
                  effective = tryEval (let x = plain 0 (get (builtins.tail (builtins.tail path)) c.pkgs.config); in builtins.deepSeq x x);
                in
                  { inherit exists; files = if files.success then files.value else [ ]; }
                  // (if exists && v.success then { value = v.value; } else { error = true; })
                  // (if nixpkgsConfig && effective.success then { effective = effective.value; } else { });
              resolve = path:
                let r = tryEval (let v = get path c.pkgs; in if isAttrs v && v ? outPath then v.outPath else null);
                in if r.success then r.value else null;
              # The option a setting path belongs to and the path below it: freeform settings sit below one.
              optionAt = path: o:
                if isAttrs o && (o._type or "") == "option" then { option = o; rest = path; }
                else if path == [ ] || !(isAttrs o) || !(o ? ${builtins.head path}) then null
                else optionAt (builtins.tail path) o.${builtins.head path};
              used = probe:
                let
                  at = optionAt probe.path c.options;
                  o = if at == null then null else at.option;
                  r = tryEval (o != null && builtins.any (file: elem (toString file) probe.files) o.files);
                in r.success && r.value;
            in {
              packages = map (d: { file = d.file; items = concatMap items d.value; })
                c.options.{{PackagesOption(scope)}}.definitionsWithLocations;
              options = map value {{NixPaths(options.Select(option => (IReadOnlyList<string>)option.Split('.')))}};
              resolved = map resolve {{NixPaths(request.Attrs)}};
              used = map used [ {{probes}} ];
              nixpkgs = {{(request.Nixpkgs ? NixpkgsExpression : "null")}};
              unfree =
                let r = tryEval (let k = c.pkgs.config; x = {
                  all = k.allowUnfree or false == true;
                  packages = builtins.filter builtins.isString (k.allowUnfreePackages or [ ]);
                  predicate = builtins.isFunction (k.allowUnfreePredicate or null);
                }; in builtins.deepSeq x x);
                in if r.success then r.value else null;
              users = {{(request.Users ? UsersExpression : "null")}};
            }
            """;
    }

    private static JsonElement Element(JsonNode? node) =>
        JsonSerializer.Deserialize(node is null ? "null" : node.ToJsonString(), ManagerJson.Default.JsonElement);

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray items ? items.Select(Text).OfType<string>().ToList() : [];

    /// <summary>
    /// One evaluation of a configuration: the packages each file contributes, the values and definition files of
    /// options, and the output paths of package attributes (<c>evaluateState</c>). <paramref name="source"/> is a
    /// store path of the flake's files.
    /// </summary>
    public static async Task<ConfigurationState> EvaluateStateAsync(string source, string configuration, string scope, StateRequest request,
        Func<Command, Task<CommandResult>> run)
    {
        var options = request.Options.Distinct().ToList();
        var result = await run(new Command
        {
            Label = "Evaluate configuration",
            Argv = ["nix", "eval", "--json", $"path:{source}#{configuration}", "--apply", StateExpression(source, scope, request, options)],
            Quiet = true,
        }).ConfigureAwait(false);
        JsonObject state;
        try
        {
            state = JsonNode.Parse(result.Stdout) as JsonObject ?? throw new JsonException("expected an object");
        }
        catch (JsonException e)
        {
            throw new ManagerException($"Invalid configuration evaluation: {e.Message}");
        }
        string Local(string file)
        {
            if (!file.StartsWith($"{source}/", StringComparison.Ordinal)) return file;
            var path = file[(source.Length + 1)..];
            // A directory import names the directory; its module is default.nix.
            return path.EndsWith(".nix", StringComparison.Ordinal) ? path : $"{path}/default.nix";
        }
        var packages = new List<(string File, List<PackageInfo> Items)>();
        foreach (var entry in state["packages"] as JsonArray ?? [])
        {
            var key = Local(Text(entry?["file"]) ?? "");
            var items = (entry?["items"] as JsonArray ?? []).Select(item => new PackageInfo
            {
                Out = Text(item?["out"]) ?? "",
                Name = Text(item?["name"]) ?? "",
                Version = Text(item?["version"]),
                Description = Text(item?["description"]),
            });
            var known = packages.FindIndex(pair => pair.File == key);
            if (known >= 0) packages[known].Items.AddRange(items);
            else packages.Add((key, items.ToList()));
        }
        var values = state["options"] as JsonArray ?? [];
        var optionValues = new Dictionary<string, OptionValue>();
        for (var i = 0; i < options.Count; i++)
        {
            var value = values.Count > i ? values[i] as JsonObject : null;
            optionValues[options[i]] = new OptionValue
            {
                Value = value is not null && value.ContainsKey("value") ? Element(value["value"]) : default,
                Error = value?["error"] is JsonValue error && error.TryGetValue<bool>(out var failed) ? failed : null,
                Exists = value?["exists"] is JsonValue exists && exists.TryGetValue<bool>(out var found) && found,
                // Definitions without a file, such as Home Manager's own, cannot be edited.
                Files = Strings(value?["files"]).Select(Local).Where(file => file != "<unknown-file>").ToList(),
                Effective = value is not null && value.ContainsKey("effective") ? Element(value["effective"]) : default,
            };
        }
        return new ConfigurationState
        {
            Packages = packages.Select(pair => new FilePackages(pair.File, pair.Items)).ToList(),
            Options = optionValues,
            Resolved = (state["resolved"] as JsonArray ?? []).Select(Text).ToList(),
            Used = (state["used"] as JsonArray ?? []).Select(item => item is JsonValue v && v.TryGetValue<bool>(out var used) && used).ToList(),
            Nixpkgs = state["nixpkgs"] is JsonObject nixpkgs
                ? new NixpkgsFingerprint(nixpkgs["config"] as JsonObject ?? [], Text(nixpkgs["names"]) ?? "")
                : null,
            Unfree = state["unfree"] is JsonObject unfree
                ? new UnfreePolicy
                {
                    All = unfree["all"] is JsonValue all && all.TryGetValue<bool>(out var allowed) && allowed,
                    Packages = Strings(unfree["packages"]),
                    Predicate = unfree["predicate"] is JsonValue predicate && predicate.TryGetValue<bool>(out var decides) && decides,
                }
                : null,
            Users = state["users"] is JsonArray users
                ? users.Select(user => new UserAccount
                {
                    Name = Text(user?["name"]) ?? "",
                    Description = Text(user?["description"]),
                    Groups = Strings(user?["groups"]),
                    Files = Strings(user?["files"]).Select(Local).Where(file => file != "<unknown-file>").ToList(),
                }).ToList()
                : null,
        };
    }
}
