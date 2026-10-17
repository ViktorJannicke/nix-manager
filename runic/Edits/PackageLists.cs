using System.Text.RegularExpressions;
using NixManager.Domain;
using NixManager.Nix;
using NixManager.Nix.Syntax;

namespace NixManager.Edits;

/// <summary>An item of a package list (<c>PackageItem</c>).</summary>
/// <param name="Attr">Resolved attribute path such as <c>pkgs.kdePackages.kate</c>; null when computed.</param>
public sealed record PackageItem(string? Attr, IReadOnlyList<string>? Path, string Text, int Start, int End);

/// <summary>A list that makes up the value of a package binding (<c>PackageList</c>).</summary>
/// <param name="Scope">Innermost <c>with</c> environment, for example <c>pkgs</c>.</param>
/// <param name="Conditional">The list only applies under a condition such as <c>lib.optionals</c>.</param>
public sealed record PackageList(string Binding, string? Scope, bool Conditional, int Start, int End, IReadOnlyList<PackageItem> Items);

/// <summary>Package lists of Nix files and their edits (<c>src/packages.ts</c>).</summary>
public static partial class PackageLists
{
    /// <summary>Bindings whose value is a list of packages, matched by their full attrpath (<c>packageBindings</c>).</summary>
    public static readonly IReadOnlyList<string> Bindings = ["packages", "home.packages", "environment.systemPackages"];

    // `resolve`: a bare name inside `with pkgs;` refers to `pkgs.<name>`; qualified names are written out in full.
    private static List<string>? Resolve(SyntaxNode element, IReadOnlyList<string>? scope)
    {
        var path = NixSyntax.StaticPath(element);
        if (path is null) return null;
        if (element.Type == "variable_expression") return scope is null ? null : [.. scope, .. path];
        return path;
    }

    /// <summary>Lists that make up the value of a package binding, with how each item resolves (<c>packageLists</c>).</summary>
    public static List<PackageList> Find(SyntaxNode root)
    {
        var lists = new List<PackageList>();
        foreach (var binding in NixSyntax.Descendants(root))
        {
            if (binding.Type != "binding") continue;
            var attrpath = binding.ChildForFieldName("attrpath");
            var names = attrpath is null ? null : NixSyntax.AttrNames(attrpath);
            if (names is null) continue;
            var name = string.Join('.', names);
            if (!Bindings.Contains(name)) continue;

            void Visit(SyntaxNode node, IReadOnlyList<string>? scopePath, bool conditional)
            {
                var scope = scopePath is null ? null : NixSyntax.RenderPath(scopePath);
                switch (node.Type)
                {
                    case "list_expression":
                        lists.Add(new PackageList(name, scope, conditional, node.StartIndex, node.EndIndex,
                            node.ChildrenForFieldName("element").Select(element =>
                            {
                                var path = Resolve(element, scopePath);
                                return new PackageItem(path is null ? null : NixSyntax.RenderPath(path), path, element.Text,
                                    element.StartIndex, element.EndIndex);
                            }).ToList()));
                        return;
                    case "with_expression":
                    {
                        var environment = NixSyntax.StaticPath(node.ChildForFieldName("environment"));
                        if (node.ChildForFieldName("body") is { } body) Visit(body, environment, conditional);
                        return;
                    }
                    case "parenthesized_expression":
                        if (node.ChildForFieldName("expression") is { } inner) Visit(inner, scopePath, conditional);
                        return;
                    case "binary_expression":
                        if (node.ChildForFieldName("operator")?.Text != "++") return;
                        foreach (var side in new[] { "left", "right" })
                            if (node.ChildForFieldName(side) is { } child) Visit(child, scopePath, conditional);
                        return;
                    case "apply_expression":
                        // `lib.optionals cond [ ... ]` and similar helpers wrap a list.
                        if (node.ChildForFieldName("argument") is { } argument) Visit(argument, scopePath, true);
                        return;
                }
            }

            if (binding.ChildForFieldName("expression") is { } value) Visit(value, null, false);
        }
        return lists;
    }

    /// <summary>How <paramref name="attr"/> is written in <paramref name="list"/>: relative to its <c>with</c> scope when possible (<c>spelling</c>).</summary>
    private static string Spelling(PackageList list, string attr)
    {
        if (!string.IsNullOrEmpty(list.Scope) && attr.StartsWith($"{list.Scope}.", StringComparison.Ordinal))
        {
            var rest = attr[(list.Scope.Length + 1)..];
            // A quoted first segment is a string, not a name, inside `with`.
            if (NixSyntax.Identifier(rest.Split('.')[0])) return rest;
        }
        return attr;
    }

    /// <summary>Start of the line holding <paramref name="index"/> (<c>lineStart</c>).</summary>
    public static int LineStart(string source, int index) =>
        // lastIndexOf clamps a negative position to 0, where it still finds a leading newline.
        source.Length == 0 ? 0 : source.LastIndexOf('\n', Math.Clamp(index - 1, 0, source.Length - 1)) + 1;

    /// <summary>End of the line holding <paramref name="index"/>, before its newline (<c>lineEnd</c>).</summary>
    public static int LineEnd(string source, int index)
    {
        var end = source.IndexOf('\n', Math.Clamp(index, 0, source.Length));
        return end < 0 ? source.Length : end;
    }

    /// <summary>Replaces <c>[start, end)</c> with <paramref name="text"/> (<c>apply</c>).</summary>
    public static SourceEdit Apply(string source, int start, int end, string text) => SourceEdit.Apply(source, start, end, text);

    internal static SyntaxTree ParseValid(string source)
    {
        var tree = NixSyntax.Parse(source);
        if (tree.Root.HasError) throw new ManagerException("The file has syntax errors.");
        return tree;
    }

    /// <summary>
    /// Adds a package to the unconditional list that already uses its scope (<c>addPackage</c>). The longest
    /// matching <c>with</c> scope wins, so <c>pkgs.kdePackages.kate</c> goes into
    /// <c>with pkgs.kdePackages; [ ... ]</c> when the file has such a list.
    /// </summary>
    public static SourceEdit AddPackage(string source, string attr, IReadOnlyList<string>? bindings = null)
    {
        bindings ??= Bindings;
        var tree = ParseValid(source);
        var lists = Find(tree.Root).Where(list => bindings.Contains(list.Binding)).ToList();
        if (lists.Any(list => list.Items.Any(item => item.Attr == attr)))
            throw new ManagerException($"{attr} is already listed in this file.");
        var target = lists
            .Where(list => !list.Conditional)
            .Select(list => (List: list,
                Score: !string.IsNullOrEmpty(list.Scope) && attr.StartsWith($"{list.Scope}.", StringComparison.Ordinal) ? list.Scope.Length : -1))
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.List)
            .FirstOrDefault()
            ?? throw new ManagerException("No editable package list was found.");
        var last = target.Items.Count > 0 ? target.Items[^1] : null;
        return AppendItem(source, (target.Start, target.End), last is null ? null : (last.Start, last.End), Spelling(target, attr));
    }

    [GeneratedRegex($@"^[ \t]*(#{JsText.Dot}*)?\z")]
    internal static partial Regex RestOfLine();

    [GeneratedRegex(@"^[ \t]*")]
    internal static partial Regex Indent();

    [GeneratedRegex($"^{JsText.Space}*")]
    internal static partial Regex LeadingSpace();

    [GeneratedRegex($@"^{JsText.Space}*\z")]
    internal static partial Regex Blank();

    [GeneratedRegex($@"^{JsText.Space}*(#{JsText.Dot}*)?\z")]
    internal static partial Regex BlankOrComment();

    /// <summary>
    /// Appends an item to the list spanning <paramref name="list"/>, in its layout (<c>appendItem</c>): on the
    /// same line for one-line lists, otherwise on its own line under the last item.
    /// </summary>
    public static SourceEdit AppendItem(string source, (int Start, int End) list, (int Start, int End)? last, string text)
    {
        var close = list.End - 1;
        var multiline = source[list.Start..list.End].Contains('\n');
        if (!multiline)
        {
            // `[ a b ]` becomes `[ a b c ]`; `[ ]` and `[]` become `[ c ]`.
            if (last is { } item) return Apply(source, item.End, item.End, $" {text}");
            return Apply(source, list.Start, list.End, $"[ {text} ]");
        }
        if (last is { } previous)
        {
            var at = LineEnd(source, previous.End);
            // `c ];` on one line: continue that line instead of splitting the bracket.
            if (!RestOfLine().IsMatch(source[previous.End..at]))
                return Apply(source, previous.End, previous.End, $" {text}");
            // Keep a trailing comment on the last item's line attached to it.
            var indent = Indent().Match(source[LineStart(source, previous.Start)..]).Value;
            return Apply(source, at, at, $"\n{indent}{text}");
        }
        var closing = LineStart(source, close);
        var space = LeadingSpace().Match(source[closing..close]).Value;
        return Apply(source, closing, closing, $"{space}  {text}\n");
    }

    /// <summary>Removes every listing of <paramref name="attr"/>, dropping the whole line when the item stands alone (<c>removePackage</c>).</summary>
    public static SourceEdit RemovePackage(string source, string attr)
    {
        var tree = NixSyntax.Parse(source);
        var items = Find(tree.Root).SelectMany(list => list.Items).Where(item => item.Attr == attr).ToList();
        if (items.Count == 0) throw new ManagerException($"{attr} is not listed in this file.");
        if (items.Count > 1) throw new ManagerException($"{attr} is listed {items.Count} times in this file.");
        var found = items[0];
        var start = LineStart(source, found.Start);
        var end = LineEnd(source, found.End);
        if (Blank().IsMatch(source[start..found.Start]) && BlankOrComment().IsMatch(source[found.End..end]))
            return Apply(source, start, Math.Min(end + 1, source.Length), "");
        // Inline item: remove it with the whitespace that separates it.
        var space = Indent().Match(source[found.End..]).Length;
        return Apply(source, found.Start, found.End + space, "");
    }
}
