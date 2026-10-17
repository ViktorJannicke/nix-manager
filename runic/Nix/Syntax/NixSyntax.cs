using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NixManager.Domain;

namespace NixManager.Nix.Syntax;

/// <summary>
/// One node of a parsed Nix file: the tree-sitter Nix grammar's concrete syntax tree, copied out of the native
/// tree so it outlives it. Indices count UTF-16 code units, as JavaScript's and .NET's strings do, so they match
/// the Bun version's (web-tree-sitter parses JavaScript strings as UTF-16 too).
/// </summary>
public sealed class SyntaxNode
{
    private readonly string _source;
    private readonly List<SyntaxNode> _children = [];
    private readonly List<string?> _fields = [];
    private List<SyntaxNode>? _named;

    internal SyntaxNode(string source, string type, bool named, int start, int end, bool hasError, SyntaxNode? parent)
    {
        _source = source;
        Type = type;
        IsNamed = named;
        StartIndex = start;
        EndIndex = end;
        HasError = hasError;
        Parent = parent;
    }

    /// <summary>The grammar's name for the node, such as <c>binding</c> or <c>list_expression</c>.</summary>
    public string Type { get; }
    public bool IsNamed { get; }
    public int StartIndex { get; }
    public int EndIndex { get; }
    /// <summary>The node or one below it is an error or a missing token (<c>hasError</c>).</summary>
    public bool HasError { get; }
    public SyntaxNode? Parent { get; }
    public string Text => _source[StartIndex..EndIndex];

    /// <summary>Every child, named or not.</summary>
    public IReadOnlyList<SyntaxNode> Children => _children;

    /// <summary>The named children, comments included (<c>namedChildren</c>).</summary>
    public IReadOnlyList<SyntaxNode> NamedChildren => _named ??= _children.Where(child => child.IsNamed).ToList();

    public SyntaxNode? LastNamedChild => NamedChildren.Count > 0 ? NamedChildren[^1] : null;

    /// <summary>The first child in the field <paramref name="name"/> (<c>childForFieldName</c>).</summary>
    public SyntaxNode? ChildForFieldName(string name)
    {
        for (var i = 0; i < _children.Count; i++)
            if (_fields[i] == name) return _children[i];
        return null;
    }

    /// <summary>Every child in the field <paramref name="name"/> (<c>childrenForFieldName</c>).</summary>
    public IReadOnlyList<SyntaxNode> ChildrenForFieldName(string name)
    {
        var found = new List<SyntaxNode>();
        for (var i = 0; i < _children.Count; i++)
            if (_fields[i] == name) found.Add(_children[i]);
        return found;
    }

    internal void Add(SyntaxNode child, string? field)
    {
        _children.Add(child);
        _fields.Add(field);
    }

    public override string ToString() => $"{Type} [{StartIndex}, {EndIndex})";
}

/// <summary>A parsed Nix file.</summary>
public sealed record SyntaxTree(string Source, SyntaxNode Root);

/// <summary>
/// The tree-sitter Nix grammar (<c>src/syntax.ts</c>). Its concrete syntax tree keeps every byte, so edits replace
/// exact source ranges and leave comments and formatting untouched. The Bun version loads the grammar built as
/// WebAssembly; the port loads the same grammar built as a native library, with the tree-sitter runtime:
/// <c>NIX_MANAGER_NIX_PARSER</c> names the grammar (<c>tree-sitter-nix/parser</c>) and
/// <c>NIX_MANAGER_TREE_SITTER</c> the runtime (<c>libtree-sitter.so</c>), which is otherwise found by name.
/// </summary>
public static unsafe partial class NixSyntax
{
    public const string ParserVariable = "NIX_MANAGER_NIX_PARSER";
    public const string RuntimeVariable = "NIX_MANAGER_TREE_SITTER";

    [StructLayout(LayoutKind.Sequential)]
    private struct TSNode
    {
        public uint Context0, Context1, Context2, Context3;
        public nint Id;
        public nint Tree;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TSTreeCursor
    {
        public nint Tree;
        public nint Id;
        public uint Context0, Context1, Context2;
    }

    private const int Utf16LittleEndian = 1;

    private sealed class Runtime
    {
        public nint Language;
        public delegate* unmanaged[Cdecl]<nint> ParserNew;
        public delegate* unmanaged[Cdecl]<nint, void> ParserDelete;
        public delegate* unmanaged[Cdecl]<nint, nint, byte> ParserSetLanguage;
        public delegate* unmanaged[Cdecl]<nint, nint, char*, uint, int, nint> ParseStringEncoding;
        public delegate* unmanaged[Cdecl]<nint, void> TreeDelete;
        public delegate* unmanaged[Cdecl]<nint, TSNode> TreeRootNode;
        public delegate* unmanaged[Cdecl]<TSNode, nint> NodeType;
        public delegate* unmanaged[Cdecl]<TSNode, uint> NodeStartByte;
        public delegate* unmanaged[Cdecl]<TSNode, uint> NodeEndByte;
        public delegate* unmanaged[Cdecl]<TSNode, byte> NodeIsNamed;
        public delegate* unmanaged[Cdecl]<TSNode, byte> NodeHasError;
        public delegate* unmanaged[Cdecl]<TSNode, TSTreeCursor> CursorNew;
        public delegate* unmanaged[Cdecl]<TSTreeCursor*, void> CursorDelete;
        public delegate* unmanaged[Cdecl]<TSTreeCursor*, TSNode> CursorCurrentNode;
        public delegate* unmanaged[Cdecl]<TSTreeCursor*, nint> CursorCurrentFieldName;
        public delegate* unmanaged[Cdecl]<TSTreeCursor*, byte> CursorGotoFirstChild;
        public delegate* unmanaged[Cdecl]<TSTreeCursor*, byte> CursorGotoNextSibling;
        public delegate* unmanaged[Cdecl]<TSTreeCursor*, byte> CursorGotoParent;
    }

    private static readonly Lock Gate = new();
    private static Runtime? _runtime;

    /// <summary>Whether the grammar is configured, for tests that need it.</summary>
    public static bool Available => Environment.GetEnvironmentVariable(ParserVariable) is { Length: > 0 } path && File.Exists(path);

    private static Runtime Load()
    {
        lock (Gate)
        {
            if (_runtime is not null) return _runtime;
            var grammar = Environment.GetEnvironmentVariable(ParserVariable);
            if (string.IsNullOrEmpty(grammar))
                throw new ManagerException($"{ParserVariable} is not set.");
            var library = Environment.GetEnvironmentVariable(RuntimeVariable) is { Length: > 0 } path
                ? NativeLibrary.Load(path)
                : NativeLibrary.TryLoad("libtree-sitter.so.0", out var found) ? found : NativeLibrary.Load("libtree-sitter.so");
            var language = NativeLibrary.Load(grammar);
            nint Export(string name) => NativeLibrary.GetExport(library, name);
            var runtime = new Runtime
            {
                Language = ((delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(language, "tree_sitter_nix"))(),
                ParserNew = (delegate* unmanaged[Cdecl]<nint>)Export("ts_parser_new"),
                ParserDelete = (delegate* unmanaged[Cdecl]<nint, void>)Export("ts_parser_delete"),
                ParserSetLanguage = (delegate* unmanaged[Cdecl]<nint, nint, byte>)Export("ts_parser_set_language"),
                ParseStringEncoding = (delegate* unmanaged[Cdecl]<nint, nint, char*, uint, int, nint>)Export("ts_parser_parse_string_encoding"),
                TreeDelete = (delegate* unmanaged[Cdecl]<nint, void>)Export("ts_tree_delete"),
                TreeRootNode = (delegate* unmanaged[Cdecl]<nint, TSNode>)Export("ts_tree_root_node"),
                NodeType = (delegate* unmanaged[Cdecl]<TSNode, nint>)Export("ts_node_type"),
                NodeStartByte = (delegate* unmanaged[Cdecl]<TSNode, uint>)Export("ts_node_start_byte"),
                NodeEndByte = (delegate* unmanaged[Cdecl]<TSNode, uint>)Export("ts_node_end_byte"),
                NodeIsNamed = (delegate* unmanaged[Cdecl]<TSNode, byte>)Export("ts_node_is_named"),
                NodeHasError = (delegate* unmanaged[Cdecl]<TSNode, byte>)Export("ts_node_has_error"),
                CursorNew = (delegate* unmanaged[Cdecl]<TSNode, TSTreeCursor>)Export("ts_tree_cursor_new"),
                CursorDelete = (delegate* unmanaged[Cdecl]<TSTreeCursor*, void>)Export("ts_tree_cursor_delete"),
                CursorCurrentNode = (delegate* unmanaged[Cdecl]<TSTreeCursor*, TSNode>)Export("ts_tree_cursor_current_node"),
                CursorCurrentFieldName = (delegate* unmanaged[Cdecl]<TSTreeCursor*, nint>)Export("ts_tree_cursor_current_field_name"),
                CursorGotoFirstChild = (delegate* unmanaged[Cdecl]<TSTreeCursor*, byte>)Export("ts_tree_cursor_goto_first_child"),
                CursorGotoNextSibling = (delegate* unmanaged[Cdecl]<TSTreeCursor*, byte>)Export("ts_tree_cursor_goto_next_sibling"),
                CursorGotoParent = (delegate* unmanaged[Cdecl]<TSTreeCursor*, byte>)Export("ts_tree_cursor_goto_parent"),
            };
            if (runtime.Language == 0) throw new ManagerException("The Nix grammar did not load.");
            return _runtime = runtime;
        }
    }

    // Node type names are static strings of the grammar; one managed copy each.
    private static readonly Dictionary<nint, string> TypeNames = [];

    private static string TypeName(Runtime runtime, TSNode node)
    {
        var pointer = runtime.NodeType(node);
        lock (TypeNames)
        {
            if (!TypeNames.TryGetValue(pointer, out var name))
                TypeNames[pointer] = name = Marshal.PtrToStringUTF8(pointer) ?? "";
            return name;
        }
    }

    /// <summary>Parses Nix source (<c>parseNix</c>). Syntax errors are part of the tree (<see cref="SyntaxNode.HasError"/>).</summary>
    public static SyntaxTree Parse(string source)
    {
        var runtime = Load();
        var parser = runtime.ParserNew();
        if (parser == 0) throw new ManagerException("The Nix parser returned no tree.");
        try
        {
            if (runtime.ParserSetLanguage(parser, runtime.Language) == 0)
                throw new ManagerException("The Nix grammar does not match the tree-sitter runtime.");
            nint tree;
            fixed (char* text = source)
                tree = runtime.ParseStringEncoding(parser, 0, text, (uint)(source.Length * 2), Utf16LittleEndian);
            if (tree == 0) throw new ManagerException("The Nix parser returned no tree.");
            try
            {
                return new SyntaxTree(source, Copy(runtime, source, runtime.TreeRootNode(tree)));
            }
            finally
            {
                runtime.TreeDelete(tree);
            }
        }
        finally
        {
            runtime.ParserDelete(parser);
        }
    }

    private static SyntaxNode Copy(Runtime runtime, string source, TSNode root)
    {
        SyntaxNode Node(TSNode node, SyntaxNode? parent) => new(source, TypeName(runtime, node), runtime.NodeIsNamed(node) != 0,
            (int)(runtime.NodeStartByte(node) / 2), (int)(runtime.NodeEndByte(node) / 2), runtime.NodeHasError(node) != 0, parent);
        var top = Node(root, null);
        var cursor = runtime.CursorNew(root);
        try
        {
            // Depth-first with the cursor, which visits each child once; ts_node_child is linear per call.
            var current = top;
            if (runtime.CursorGotoFirstChild(&cursor) == 0) return top;
            while (true)
            {
                var node = runtime.CursorCurrentNode(&cursor);
                var field = runtime.CursorCurrentFieldName(&cursor);
                var copy = Node(node, current);
                current.Add(copy, field == 0 ? null : Marshal.PtrToStringUTF8(field));
                if (runtime.CursorGotoFirstChild(&cursor) != 0)
                {
                    current = copy;
                    continue;
                }
                while (runtime.CursorGotoNextSibling(&cursor) == 0)
                {
                    if (runtime.CursorGotoParent(&cursor) == 0 || current.Parent is null) return top;
                    current = current.Parent;
                }
            }
        }
        finally
        {
            runtime.CursorDelete(&cursor);
        }
    }

    /// <summary>Depth-first walk over named nodes (<c>descendants</c>).</summary>
    public static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
    {
        foreach (var child in node.NamedChildren)
        {
            yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_'-]*\z")] // \z: JavaScript's $ does not match before a final newline.
    private static partial Regex IdentifierPattern();

    public static bool Identifier(string name) => IdentifierPattern().IsMatch(name);

    /// <summary>Attribute names of a static attrpath, or null for interpolated names (<c>attrNames</c>).</summary>
    public static List<string>? AttrNames(SyntaxNode attrpath)
    {
        var names = new List<string>();
        foreach (var attr in attrpath.ChildrenForFieldName("attr"))
        {
            if (attr.Type == "identifier") names.Add(attr.Text);
            else if (attr.Type == "string_expression" && attr.NamedChildren.All(part => part.Type == "string_fragment"))
                names.Add(string.Concat(attr.NamedChildren.Select(part => part.Text)));
            else return null;
        }
        return names;
    }

    /// <summary>Dotted path of <c>a.b.c</c> style expressions, or null for anything computed (<c>staticPath</c>).</summary>
    public static List<string>? StaticPath(SyntaxNode? node)
    {
        if (node is null) return null;
        if (node.Type == "variable_expression") return [node.ChildForFieldName("name")!.Text];
        if (node.Type == "select_expression" && node.ChildForFieldName("default") is null)
        {
            var @base = StaticPath(node.ChildForFieldName("expression"));
            var rest = node.ChildForFieldName("attrpath");
            var names = rest is null ? null : AttrNames(rest);
            return @base is not null && names is not null ? [.. @base, .. names] : null;
        }
        if (node.Type == "parenthesized_expression") return StaticPath(node.ChildForFieldName("expression"));
        return null;
    }

    /// <summary>A path written as Nix reads it: plain names as they are, others quoted (<c>renderPath</c>).</summary>
    public static string RenderPath(IEnumerable<string> path) =>
        string.Join('.', path.Select(name => Identifier(name) ? name : JsJson.Quote(name)));
}
