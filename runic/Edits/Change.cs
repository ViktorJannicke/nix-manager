using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NixManager.Domain;
using NixManager.Nix;
using NixManager.Observation;
using NixManager.Operations;
using Runic.Application.Views;

namespace NixManager.Edits;

/// <summary>The kinds of pending edit: <c>kind</c> in stored JSON and <c>$case</c> on the Bridge.</summary>
public static class ChangeKinds
{
    public const string AddPackage = "add-package", RemovePackage = "remove-package", SetOption = "set-option",
        UnsetOption = "unset-option", CreateModule = "create-module";
}

/// <summary>The configurations an edit can target.</summary>
public static class Targets
{
    public const string Home = "home", System = "system";
}

/// <summary>
/// One pending edit of the draft. Stored JSON (<c>draft.json</c>, reviews, events) names the case in
/// <c>kind</c>, followed by <c>scope</c>, <c>file</c>, the case's own members and <c>label</c> when there is one;
/// the Bridge names it in <c>$case</c>.
/// </summary>
/// <remarks>Two edits are equal when their members are, option values compared as JSON.</remarks>
/// <param name="Scope"><c>home</c> or <c>system</c>.</param>
/// <param name="File">Path of the edited file inside the flake.</param>
/// <param name="Label">
/// What the change belongs to, such as an experience title: a message of the catalogue, or English text in edits
/// stored before labels were messages.
/// </param>
[RunicUnion(typeof(AddPackage), typeof(RemovePackage), typeof(SetOption), typeof(UnsetOption), typeof(CreateModule))]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AddPackage), ChangeKinds.AddPackage)]
[JsonDerivedType(typeof(RemovePackage), ChangeKinds.RemovePackage)]
[JsonDerivedType(typeof(SetOption), ChangeKinds.SetOption)]
[JsonDerivedType(typeof(UnsetOption), ChangeKinds.UnsetOption)]
[JsonDerivedType(typeof(CreateModule), ChangeKinds.CreateModule)]
public abstract record Change(
    [property: JsonPropertyOrder(-2)] string Scope,
    [property: JsonPropertyOrder(-1)] string File,
    [property: JsonPropertyOrder(2)] DisplayText? Label)
{
    /// <summary>The longest label reference, in characters of JSON.</summary>
    private const int LabelLimit = 4096;

    /// <summary>A change's subject, for messages: the attribute or option it edits, or the file it creates.</summary>
    [JsonIgnore] public string Subject => this switch
    {
        PackageEdit package => package.Attr,
        OptionEdit option => option.Option,
        _ => File,
    };

    private static ManagerException Invalid(DisplayText why) => new(DisplayText.Of("stored_invalid_edit", ("reason", why)));

    /// <summary>
    /// Validates an edit read from <c>draft.json</c>, the page or the command line: a known <c>kind</c>, a known
    /// scope, and every member it needs as text within its length limit. Unknown members are dropped.
    /// </summary>
    /// <exception cref="ManagerException">The value is not an edit.</exception>
    public static Change Decode(JsonNode? node)
    {
        if (node is not JsonObject json) throw Invalid(DisplayText.Of("stored_invalid_edit_not_an_object"));
        string Required(string name, int limit)
        {
            if (json[name] is not JsonValue value || !value.TryGetValue<string>(out var text))
                throw Invalid(DisplayText.Of("stored_invalid_edit_missing", ("name", name)));
            if (text.Length > limit) throw Invalid(DisplayText.Of("stored_invalid_edit_too_long", ("name", name), ("limit", limit)));
            return text;
        }
        // A label is a text reference, or English text in edits stored before labels were messages.
        DisplayText? Label()
        {
            if (!json.ContainsKey("label")) return null;
            if (json["label"] is JsonObject reference)
            {
                var text = reference.ToJsonString();
                if (text.Length > LabelLimit) throw Invalid(DisplayText.Of("stored_invalid_edit_too_long", ("name", "label"), ("limit", LabelLimit)));
                try { return JsonSerializer.Deserialize(text, ManagerJson.Compact.DisplayText); }
                catch (JsonException) { throw Invalid(DisplayText.Of("stored_invalid_edit_label")); }
            }
            return DisplayText.Plain(Required("label", 120));
        }
        var kind = Required("kind", int.MaxValue);
        // The case's own member is checked before the shared ones.
        var own = kind switch
        {
            ChangeKinds.AddPackage or ChangeKinds.RemovePackage => Required("attr", 256),
            ChangeKinds.SetOption or ChangeKinds.UnsetOption => Required("option", 256),
            ChangeKinds.CreateModule => Required("importer", 256),
            _ => throw Invalid(DisplayText.Of("stored_invalid_edit_unknown_kind", ("kind", kind))),
        };
        var scope = Required("scope", int.MaxValue);
        if (scope is not (Targets.Home or Targets.System)) throw Invalid(DisplayText.Of("stored_invalid_edit_unknown_scope", ("scope", scope)));
        var file = Required("file", 256);
        var label = Label();
        return kind switch
        {
            ChangeKinds.AddPackage => new AddPackage(scope, file, own, label),
            ChangeKinds.RemovePackage => new RemovePackage(scope, file, own, label),
            ChangeKinds.SetOption => new SetOption(scope, file, own, json["value"]?.DeepClone(), label),
            ChangeKinds.UnsetOption => new UnsetOption(scope, file, own, label),
            _ => new CreateModule(scope, file, own, label),
        };
    }

    /// <summary>A list of edits as the draft holds it: at most 200, each as <see cref="Decode"/> accepts it.</summary>
    /// <exception cref="ManagerException">The value is not such a list.</exception>
    public static List<Change> DecodeList(JsonNode? node)
    {
        if (node is not JsonArray items) throw new ManagerException(DisplayText.Of("stored_invalid_edits_not_a_list"));
        if (items.Count > 200) throw new ManagerException(DisplayText.Of("stored_invalid_edits_too_many", ("limit", 200)));
        return items.Select(Decode).ToList();
    }

    /// <summary>Validates edits that arrive typed, such as from the page, as <see cref="DecodeList(JsonNode?)"/> does.</summary>
    /// <exception cref="ManagerException">An edit is not valid.</exception>
    public static List<Change> DecodeList(IReadOnlyList<Change> changes) => DecodeList(ToJson(changes));

    /// <summary>Edits in their stored JSON form.</summary>
    public static JsonArray ToJson(IEnumerable<Change> changes) =>
        JsonSerializer.SerializeToNode(changes.ToList(), ManagerJson.Compact.ListChange)!.AsArray();

    /// <summary>
    /// Identifies a change list, so a review can tell whether the draft moved on: a hash of its stored JSON,
    /// with numbers written in their shortest form.
    /// </summary>
    public static string Hash(IEnumerable<Change> changes) => Repository.HashText(JsJson.Stringify(ToJson(changes)));
}

/// <summary>An edit of a package list.</summary>
/// <param name="Attr">Package attribute, such as <c>pkgs.fzf</c>.</param>
public abstract record PackageEdit(string Scope, string File, string Attr, DisplayText? Label) : Change(Scope, File, Label);

/// <summary>An edit of an option's definition.</summary>
/// <param name="Option">Option path, such as <c>programs.git.enable</c>.</param>
public abstract record OptionEdit(string Scope, string File, string Option, DisplayText? Label) : Change(Scope, File, Label);

/// <summary>Adds a package to the file's package list, or starts one.</summary>
[RunicUnionCase(ChangeKinds.AddPackage)]
public sealed record AddPackage(string Scope, string File, string Attr, DisplayText? Label = null) : PackageEdit(Scope, File, Attr, Label);

/// <summary>Removes a package from the file's package lists.</summary>
[RunicUnionCase(ChangeKinds.RemovePackage)]
public sealed record RemovePackage(string Scope, string File, string Attr, DisplayText? Label = null) : PackageEdit(Scope, File, Attr, Label);

/// <summary>Defines an option in the file.</summary>
/// <param name="Value">The value, as JSON; null for JSON <c>null</c>.</param>
[RunicUnionCase(ChangeKinds.SetOption)]
public sealed record SetOption(string Scope, string File, string Option,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never), JsonPropertyOrder(1)] JsonNode? Value, DisplayText? Label = null)
    : OptionEdit(Scope, File, Option, Label)
{
    /// <summary>Whether the edits are equal, their values compared as JSON rather than as references.</summary>
    public bool Equals(SetOption? other) => base.Equals(other) && JsonNode.DeepEquals(Value, other!.Value);

    public override int GetHashCode() => base.GetHashCode();
}

/// <summary>Removes the file's definition of an option.</summary>
[RunicUnionCase(ChangeKinds.UnsetOption)]
public sealed record UnsetOption(string Scope, string File, string Option, DisplayText? Label = null) : OptionEdit(Scope, File, Option, Label);

/// <summary>Creates the file as an empty module, imported from another one.</summary>
/// <param name="Importer">The module that imports the created file.</param>
[RunicUnionCase(ChangeKinds.CreateModule)]
public sealed record CreateModule(string Scope, string File, string Importer, DisplayText? Label = null) : Change(Scope, File, Label);
