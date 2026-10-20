using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Nix;
using NixManager.Nix.Syntax;

namespace NixManager.Edits;

/// <summary>What <c>moduleShape</c> reports about a file.</summary>
/// <param name="Editable">The file is a module whose settings can be edited.</param>
/// <param name="Names">The top-level names it sets.</param>
/// <param name="Settings">The full paths of the settings it writes.</param>
/// <param name="Imports">The relative paths it imports unconditionally.</param>
/// <param name="Importable">An import can be added (<c>addImport</c>).</param>
public sealed record ModuleShape(bool Editable, IReadOnlyList<string> Names, IReadOnlyList<string> Settings, IReadOnlyList<string> Imports, bool Importable);

/// <summary>Option settings of module files and their edits.</summary>
public static partial class ModuleOptions
{
    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("${", "\\${").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");

    /// <summary>Nix source for a JSON value, on one line (<c>renderValue</c>).</summary>
    /// <exception cref="ManagerException">A number is not finite.</exception>
    public static string RenderValue(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case JsonArray items:
                return items.Count > 0 ? $"[ {string.Join(' ', items.Select(RenderValue))} ]" : "[ ]";
            case JsonObject members:
            {
                var entries = JsOrder.Entries(members);
                if (entries.Count == 0) return "{ }";
                return $"{{ {string.Join(' ', entries.Select(entry =>
                    $"{(NixSyntax.Identifier(entry.Key) ? entry.Key : $"\"{Escape(entry.Key)}\"")} = {RenderValue(entry.Value)};"))} }}";
            }
        }
        switch (value.GetValueKind())
        {
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.String: return $"\"{Escape(value.GetValue<string>())}\"";
            case JsonValueKind.Number:
            {
                var number = double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number)) throw new ManagerException("Numbers must be finite.");
                // Number.isInteger: String(value); otherwise toFixed(12) where String would use an exponent.
                var text = JsJson.Number(number);
                if (number != Math.Floor(number) && text.Contains('e')) text = ToFixed12(number);
                return number < 0 ? $"({text})" : text;
            }
            default:
                return "null";
        }
    }

    // `Number.prototype.toFixed(12)` for the numbers JavaScript prints with an exponent, which are below 1e-6
    // here (larger ones are integers).
    private static string ToFixed12(double value) => value.ToString("F12", CultureInfo.InvariantCulture);

    private static readonly HashSet<string> Attrsets = ["attrset_expression", "rec_attrset_expression"];

    /// <summary>The attribute set a module evaluates to, through <c>{ ... }:</c>, <c>let ... in</c> and parentheses (<c>moduleSet</c>).</summary>
    internal static SyntaxNode? ModuleSet(SyntaxNode root)
    {
        var node = root.NamedChildren.FirstOrDefault(child => child.Type != "comment");
        while (node is not null)
        {
            if (node.Type is "function_expression" or "let_expression") node = node.ChildForFieldName("body");
            else if (node.Type == "parenthesized_expression") node = node.ChildForFieldName("expression");
            else break;
        }
        return node is not null && Attrsets.Contains(node.Type) ? node : null;
    }

    /// <summary>
    /// The attribute set a module's settings are written in (<c>moduleBody</c>): the body of
    /// <c>{ ... }: { ... }</c>, through <c>let ... in</c>, or its <c>config = { ... }</c> when the module also
    /// declares options.
    /// </summary>
    internal static SyntaxNode? ModuleBody(SyntaxNode root)
    {
        var node = ModuleSet(root);
        if (node is null) return null;
        var top = Bindings(node);
        var config = top.FirstOrDefault(binding => Names(binding) is { } names && string.Join('.', names) == "config");
        if (config?.ChildForFieldName("expression") is { } value) return Attrsets.Contains(value.Type) ? value : null;
        // Settings beside option declarations must be written under `config`.
        return top.Any(binding => Names(binding) is [ "options", .. ]) ? null : node;
    }

    /// <summary>The bindings of a set (<c>bindings</c>).</summary>
    internal static List<SyntaxNode> Bindings(SyntaxNode set) =>
        (set.NamedChildren.FirstOrDefault(child => child.Type == "binding_set")?.NamedChildren ?? [])
            .Where(child => child.Type == "binding").ToList();

    /// <summary>The attribute names a binding binds (<c>names</c>).</summary>
    internal static List<string>? Names(SyntaxNode binding) =>
        binding.ChildForFieldName("attrpath") is { } attrpath ? NixSyntax.AttrNames(attrpath) : null;

    private static bool StartsWith(IReadOnlyList<string> path, IReadOnlyList<string> prefix) =>
        prefix.Count <= path.Count && prefix.Select((name, index) => path[index] == name).All(same => same);

    /// <param name="Binding">Binding of exactly the option path.</param>
    /// <param name="Set">Deepest attribute set on the way.</param>
    /// <param name="Rest">The path remaining below <paramref name="Set"/>.</param>
    private sealed record Found(SyntaxNode? Binding, SyntaxNode Set, List<string> Rest);

    /// <summary>Where an option path is bound, or the deepest set holding part of it (<c>locate</c>).</summary>
    private static Found Locate(SyntaxNode set, IReadOnlyList<string> path)
    {
        var best = new Found(null, set, [.. path]);
        foreach (var binding in Bindings(set))
        {
            var bound = Names(binding);
            if (bound is null) continue;
            if (bound.Count == path.Count && StartsWith(path, bound)) return new Found(binding, set, []);
            if (StartsWith(bound, path))
                throw new ManagerException($"{string.Join('.', path)} is written as separate parts here. Edit it in the file.");
            var value = binding.ChildForFieldName("expression");
            if (bound.Count < path.Count && StartsWith(path, bound) && value is not null && Attrsets.Contains(value.Type))
            {
                var inner = Locate(value, path.Skip(bound.Count).ToList());
                if (inner.Binding is not null) return inner;
                if (inner.Rest.Count < best.Rest.Count) best = inner;
            }
        }
        return best;
    }

    /// <summary>Values Nix Manager can replace without losing meaning: plain data and package references (<c>literal</c>).</summary>
    internal static bool Literal(SyntaxNode? node)
    {
        if (node is null) return false;
        switch (node.Type)
        {
            case "integer_expression" or "float_expression" or "path_expression":
                return true;
            case "unary_expression":
                return Literal(node.ChildForFieldName("argument"));
            case "string_expression" or "indented_string_expression":
                return node.NamedChildren.All(part => part.Type != "interpolation");
            case "variable_expression":
                return node.Text is "true" or "false" or "null";
            case "select_expression":
                return NixSyntax.StaticPath(node) is [ "pkgs", .. ];
            case "parenthesized_expression":
                return Literal(node.ChildForFieldName("expression"));
            case "list_expression":
                return node.ChildrenForFieldName("element").All(Literal);
            case "attrset_expression":
                return Bindings(node).All(binding => Names(binding) is not null && Literal(binding.ChildForFieldName("expression")));
            default:
                return false;
        }
    }

    private static SyntaxNode ParseModule(string source)
    {
        var tree = PackageLists.ParseValid(source);
        return ModuleBody(tree.Root) ?? throw new ManagerException("This file is not a module Nix Manager can edit.");
    }

    private static string[] CheckPath(string option)
    {
        var path = option.Split('.');
        if (!path.All(NixSyntax.Identifier)) throw new ManagerException($"Unsupported option path: {option}");
        return path;
    }

    /// <summary>
    /// Sets an option in a module (<c>setOption</c>). An existing plain value is replaced in place; otherwise the
    /// binding is added to the deepest attribute set that already holds part of its path, so
    /// <c>programs.git = { ... }</c> gains <c>lfs.enable</c>.
    /// </summary>
    public static SourceEdit SetOption(string source, string option, JsonNode? value)
    {
        var path = CheckPath(option);
        var body = ParseModule(source);
        var found = Locate(body, path);
        var text = RenderValue(value);
        if (found.Binding is not null)
        {
            var expression = found.Binding.ChildForFieldName("expression")!;
            if (!Literal(expression))
            {
                var line = expression.Text.Split('\n')[0];
                throw new ManagerException($"{option} is computed here ({line[..Math.Min(60, line.Length)]}). Edit it in the file.");
            }
            return PackageLists.Apply(source, expression.StartIndex, expression.EndIndex, text);
        }
        return InsertBinding(source, found.Set, $"{string.Join('.', found.Rest)} = {text};");
    }

    [GeneratedRegex(JsText.NonSpace)]
    private static partial Regex NonSpace();

    /// <summary>Adds a binding as the last entry of a set, in the set's layout (<c>insertBinding</c>).</summary>
    private static SourceEdit InsertBinding(string source, SyntaxNode set, string binding)
    {
        // Inherits and comments count as content: insert after whatever comes last.
        var last = set.NamedChildren.FirstOrDefault(child => child.Type == "binding_set")?.LastNamedChild;
        var close = set.EndIndex - 1;
        var multiline = source[set.StartIndex..set.EndIndex].Contains('\n');
        if (!multiline)
        {
            if (last is not null) return PackageLists.Apply(source, last.EndIndex, last.EndIndex, $" {binding}");
            return PackageLists.Apply(source, set.StartIndex, set.EndIndex, $"{{ {binding} }}");
        }
        if (last is not null)
        {
            var at = PackageLists.LineEnd(source, last.EndIndex);
            if (!PackageLists.RestOfLine().IsMatch(source[last.EndIndex..at]))
                return PackageLists.Apply(source, last.EndIndex, last.EndIndex, $" {binding}");
            var indent = PackageLists.Indent().Match(source[PackageLists.LineStart(source, last.StartIndex)..]).Value;
            return PackageLists.Apply(source, at, at, $"\n{indent}{binding}");
        }
        var closing = PackageLists.LineStart(source, close);
        var space = PackageLists.LeadingSpace().Match(source[closing..close]).Value;
        if (NonSpace().IsMatch(source[closing..close]))
            return PackageLists.Apply(source, close, close, $" {binding} ");
        return PackageLists.Apply(source, closing, closing, $"{space}  {binding}\n");
    }

    /// <summary>
    /// Starts a list option in a module that has none, such as <c>home.packages = [ pkgs.fzf ];</c>
    /// (<c>startList</c>). A file that already binds the option is left alone: its list is not one Nix Manager
    /// can extend.
    /// </summary>
    public static SourceEdit StartList(string source, string option, string item)
    {
        var path = CheckPath(option);
        var body = ParseModule(source);
        var found = Locate(body, path);
        if (found.Binding is not null) throw new ManagerException($"{option} is computed here. Edit it in the file.");
        return InsertBinding(source, found.Set, $"{string.Join('.', found.Rest)} = [ {item} ];");
    }

    [GeneratedRegex(@"^\.{1,2}/[A-Za-z0-9._+/-]+\z")]
    private static partial Regex ImportablePath();

    [GeneratedRegex(@"^\.{1,2}/")]
    private static partial Regex Relative();

    /// <summary>Adds a relative path such as <c>./fzf.nix</c> to a module's <c>imports</c>, starting the list if needed (<c>addImport</c>).</summary>
    public static SourceEdit AddImport(string source, string path)
    {
        if (!ImportablePath().IsMatch(path)) throw new ManagerException($"{path} cannot be written as a Nix path.");
        var tree = PackageLists.ParseValid(source);
        var set = ModuleSet(tree.Root) ?? throw new ManagerException("This file is not a module Nix Manager can edit.");
        var binding = Bindings(set).FirstOrDefault(item => Names(item) is { } names && string.Join('.', names) == "imports");
        if (binding is null) return InsertBinding(source, set, $"imports = [ {path} ];");
        var list = binding.ChildForFieldName("expression");
        if (list?.Type != "list_expression")
            throw new ManagerException("This file computes its imports. Add the import in the file.");
        var elements = list.ChildrenForFieldName("element");
        if (elements.Any(element => element.Text == path)) throw new ManagerException($"The file already imports {path}.");
        var last = elements.Count > 0 ? elements[^1] : null;
        return PackageLists.AppendItem(source, (list.StartIndex, list.EndIndex), last is null ? null : (last.StartIndex, last.EndIndex), path);
    }

    /// <summary>Removes an option's binding so its default applies again (<c>unsetOption</c>).</summary>
    public static SourceEdit UnsetOption(string source, string option)
    {
        var path = CheckPath(option);
        var body = ParseModule(source);
        var binding = Locate(body, path).Binding ?? throw new ManagerException($"{option} is not set in this file.");
        var start = PackageLists.LineStart(source, binding.StartIndex);
        var end = PackageLists.LineEnd(source, binding.EndIndex);
        if (PackageLists.Blank().IsMatch(source[start..binding.StartIndex]) && PackageLists.BlankOrComment().IsMatch(source[binding.EndIndex..end]))
            return PackageLists.Apply(source, start, Math.Min(end + 1, source.Length), "");
        var space = PackageLists.Indent().Match(source[binding.EndIndex..]).Length;
        return PackageLists.Apply(source, binding.StartIndex, binding.EndIndex + space, "");
    }

    /// <summary>Names that structure a module rather than set options (<c>structural</c>).</summary>
    private static readonly HashSet<string> Structural = ["imports", "options", "disabledModules", "_file", "key", "meta", "_class"];

    /// <summary>Full paths of the settings below a set, following nested attribute sets (<c>leaves</c>).</summary>
    private static List<List<string>> Leaves(SyntaxNode set, IReadOnlyList<string>? prefix = null, int limit = 64)
    {
        prefix ??= [];
        var found = new List<List<string>>();
        foreach (var binding in Bindings(set))
        {
            var bound = Names(binding);
            if (bound is null || (prefix.Count == 0 && Structural.Contains(bound[0]))) continue;
            List<string> path = [.. prefix, .. bound];
            var value = binding.ChildForFieldName("expression");
            if (value?.Type == "attrset_expression" && Bindings(value).Count > 0) found.AddRange(Leaves(value, path, limit));
            else found.Add(path);
            if (found.Count >= limit) break;
        }
        return found.Take(limit).ToList();
    }

    private static SyntaxNode? ImportList(SyntaxNode? set) =>
        set is null ? null : Bindings(set).FirstOrDefault(binding => Names(binding) is { } names && string.Join('.', names) == "imports")
            ?.ChildForFieldName("expression");

    /// <summary>Relative paths a module always imports: the plain path items of its <c>imports</c> list (<c>imports</c>).</summary>
    private static List<string> Imports(SyntaxNode root)
    {
        var list = ImportList(ModuleSet(root));
        if (list?.Type != "list_expression") return [];
        return list.ChildrenForFieldName("element")
            .Where(element => element.Type == "path_expression" && Relative().IsMatch(element.Text))
            .Select(element => element.Text).ToList();
    }

    /// <summary>Whether <c>addImport</c> can add to a module: it has a plain list of imports, or none (<c>importable</c>).</summary>
    private static bool Importable(SyntaxNode root)
    {
        var set = ModuleSet(root);
        if (set is null) return false;
        var list = ImportList(set);
        return list is null || list.Type == "list_expression";
    }

    /// <summary>
    /// Whether a file is a module whose settings can be edited, the top-level names it sets, the full paths of the
    /// settings it writes, the relative paths it imports unconditionally, and whether an import can be added
    /// (<c>moduleShape</c>).
    /// </summary>
    public static ModuleShape Shape(string source)
    {
        var root = NixSyntax.Parse(source).Root;
        var failed = root.HasError;
        var body = failed ? null : ModuleBody(root);
        return new ModuleShape(
            body is not null,
            body is null ? [] : Bindings(body).Select(Names).OfType<List<string>>().Select(names => string.Join('.', names)).ToList(),
            body is null ? [] : Leaves(body).Select(path => string.Join('.', path)).ToList(),
            failed ? [] : Imports(root),
            !failed && Importable(root));
    }
}

/// <summary>JavaScript's own member order of objects parsed from JSON.</summary>
public static class JsOrder
{
    /// <summary>
    /// <c>Object.entries(value)</c>: array-index keys (<c>"0"</c>, <c>"12"</c>) first in ascending order, then the
    /// other keys in the order they were written.
    /// </summary>
    public static List<KeyValuePair<string, JsonNode?>> Entries(JsonObject value)
    {
        static bool Index(string key) =>
            key.Length > 0 && key.All(char.IsAsciiDigit) && (key == "0" || key[0] != '0') &&
            ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number < uint.MaxValue;
        var entries = value.ToList();
        return entries.Where(entry => Index(entry.Key)).OrderBy(entry => ulong.Parse(entry.Key, CultureInfo.InvariantCulture))
            .Concat(entries.Where(entry => !Index(entry.Key))).ToList();
    }

    /// <summary>A copy of <paramref name="value"/> with every object in JavaScript's member order.</summary>
    public static JsonNode? Normalize(JsonNode? value) => value switch
    {
        JsonObject members => new JsonObject(Entries(members).Select(entry => KeyValuePair.Create(entry.Key, Normalize(entry.Value)))),
        JsonArray items => new JsonArray([.. items.Select(Normalize)]),
        _ => value?.DeepClone(),
    };
}
