using System.Text.Json.Nodes;
using NixManager.Edits;
using NixManager.Nix;
using NixManager.Nix.Syntax;

namespace NixManager.Tests;

/// <summary>The native tree-sitter Nix grammar.</summary>
public sealed class SyntaxTests
{
    [Test]
    public async Task A_module_parses_into_the_grammars_nodes_with_UTF_16_ranges()
    {
        const string source = "{ pkgs, ... }:\n# Grüße\n{\n  home.packages = with pkgs; [ fzf pkgs.\"7zz\" ];\n}\n";
        var tree = NixSyntax.Parse(source);

        await Assert.That(tree.Root.Type).IsEqualTo("source_code");
        await Assert.That(tree.Root.HasError).IsFalse();
        var binding = NixSyntax.Descendants(tree.Root).First(node => node.Type == "binding");
        await Assert.That(NixSyntax.AttrNames(binding.ChildForFieldName("attrpath")!)).IsEquivalentTo(new[] { "home", "packages" });
        var with = binding.ChildForFieldName("expression")!;
        await Assert.That(with.Type).IsEqualTo("with_expression");
        await Assert.That(NixSyntax.StaticPath(with.ChildForFieldName("environment"))).IsEquivalentTo(new[] { "pkgs" });
        var elements = with.ChildForFieldName("body")!.ChildrenForFieldName("element");
        await Assert.That(elements.Select(e => e.Text)).IsEquivalentTo(new[] { "fzf", "pkgs.\"7zz\"" });
        await Assert.That(NixSyntax.RenderPath(NixSyntax.StaticPath(elements[1])!)).IsEqualTo("pkgs.\"7zz\"");
        // Indices count UTF-16 code units, past the umlauts of the comment.
        await Assert.That(source[elements[0].StartIndex..elements[0].EndIndex]).IsEqualTo("fzf");
    }

    [Test]
    public async Task Syntax_errors_are_reported()
    {
        await Assert.That(NixSyntax.Parse("{ a = ; }").Root.HasError).IsTrue();
    }

    [Test]
    public async Task JSON_text_is_written_as_JavaScript_writes_it()
    {
        var value = JsonNode.Parse("""{"b":1.50,"a":[true,null,"é\u0001\"",1e21,0.00001,-0.5,100]}""");
        await Assert.That(JsJson.Stringify(value)).IsEqualTo("{\"b\":1.5,\"a\":[true,null,\"é\\u0001\\\"\",1e+21,0.00001,-0.5,100]}");
        await Assert.That(JsJson.Indented(JsonNode.Parse("""{"a":[1],"b":{}}"""))).IsEqualTo("{\n  \"a\": [\n    1\n  ],\n  \"b\": {}\n}");
    }

    [Test]
    public async Task Edits_are_stored_with_their_kind_then_the_shared_members_then_their_own()
    {
        var created = new AddPackage("home", "a.nix", "pkgs.x");
        var decoded = Change.Decode(JsonNode.Parse("""{"attr":"pkgs.x","file":"a.nix","extra":1,"scope":"home","kind":"add-package"}"""));
        await Assert.That(decoded).IsEqualTo(created);
        await Assert.That(JsJson.Stringify(Change.ToJson([created]))).IsEqualTo("""[{"kind":"add-package","scope":"home","file":"a.nix","attr":"pkgs.x"}]""");
        var option = Change.Decode(JsonNode.Parse("""{"file":"b.nix","value":1.5,"option":"o.p","scope":"system","kind":"set-option","label":"L"}"""));
        await Assert.That(JsJson.Stringify(Change.ToJson([option, new SetOption("home", "c.nix", "o.q", null)])))
            .IsEqualTo("""[{"kind":"set-option","scope":"system","file":"b.nix","option":"o.p","value":1.5,"label":"L"},{"kind":"set-option","scope":"home","file":"c.nix","option":"o.q","value":null}]""");
    }
}
