using System.Text.Json;
using System.Text.Json.Serialization;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Nix;
using NixManager.Operations;

namespace NixManager.Software;

/// <summary>A documented option, as <c>optionAttrSetToDocList</c> describes it (<c>OptionDoc</c>).</summary>
public sealed record OptionDoc
{
    /// <summary>Option path.</summary>
    [JsonPropertyName("n")] public required string N { get; init; }
    /// <summary>Type description, such as "boolean" or "null or string".</summary>
    [JsonPropertyName("t"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? T { get; init; }
    [JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? D { get; init; }
    /// <summary>Default as Nix text.</summary>
    [JsonPropertyName("df"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Df { get; init; }
    /// <summary>Read-only.</summary>
    [JsonPropertyName("r")] public bool R { get; init; }
    /// <summary>Declaring files.</summary>
    [JsonPropertyName("decl")] public IReadOnlyList<string> Decl { get; init; } = [];
}

/// <summary>Documented options of a configuration.</summary>
public static class OptionDocs
{
    public const string Expression = """
        c:
        let
          lib = c.pkgs.lib;
          visible = builtins.filter (o: (o.visible or true) && !(o.internal or false)) (lib.optionAttrSetToDocList c.options);
        in map (o: {
          n = o.name;
          t = o.type or null;
          d = if builtins.isString (o.description or null) then o.description else null;
          df = if o ? default then (o.default.text or (builtins.toJSON o.default)) else null;
          r = o.readOnly or false;
          decl = map toString (o.declarations or [ ]);
        }) visible
        """;

    /// <summary>Documented options of a configuration, as <c>optionAttrSetToDocList</c> describes them.</summary>
    public static async Task<List<OptionDoc>> ReadAsync(Config config, string host, string scope, Func<Command, Task<CommandResult>> run)
    {
        var result = await run(new Command
        {
            Label = DisplayText.Of("stored_read_option_documentation"),
            Argv = ["nix", "eval", "--json", $"{config.Flake}#{Evaluation.ConfigurationAttr(config, host, scope)}", "--apply", Expression],
            // NixOS documents tens of thousands of options: more than the usual capture.
            Quiet = true,
            FullOutput = true,
        }).ConfigureAwait(false);
        try
        {
            return ManagerJson.Deserialize<List<OptionDoc>>(result.Stdout) ?? throw new JsonException("expected a list");
        }
        catch (JsonException error)
        {
            throw new ManagerException(DisplayText.Of("stored_option_documentation_invalid", ("reason", error.Message)));
        }
    }

    /// <summary>Documented options matching a query, best matches first (<c>Store.searchOptions</c>).</summary>
    public static List<OptionDoc> Search(IReadOnlyList<OptionDoc> docs, string query)
    {
        var terms = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];
        var prefix = query.Trim();
        return docs
            .Select(doc =>
            {
                var name = doc.N.ToLowerInvariant();
                var text = $"{name} {doc.D?.ToLowerInvariant() ?? ""}";
                if (doc.R || !terms.All(term => text.Contains(term, StringComparison.Ordinal))) return (Doc: doc, Score: (int?)null);
                var score = (doc.N.StartsWith($"{prefix}.", StringComparison.Ordinal) || doc.N == prefix ? 100 : 0)
                    + terms.Count(term => name.Contains(term, StringComparison.Ordinal)) * 20
                    - doc.N.Split('.').Length;
                return (Doc: doc, Score: (int?)score);
            })
            .Where(item => item.Score is not null)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Doc.N, Comparer<string>.Create(JsText.LocaleCompare))
            .Take(120)
            .Select(item => item.Doc)
            .ToList();
    }
}

/// <summary>Cached results keyed by the checkout state they were computed from (<c>Memo</c>); failures are not cached.</summary>
public sealed class Memo<T>
{
    private readonly Dictionary<string, (string Key, Task<T> Value)> _entries = [];
    private readonly Lock _lock = new();

    public Task<T> Get(string slot, string key, Func<Task<T>> compute)
    {
        Task<T> value;
        lock (_lock)
        {
            if (_entries.TryGetValue(slot, out var hit) && hit.Key == key) return hit.Value;
            value = compute();
            _entries[slot] = (key, value);
        }
        // Failures are not cached. The entry goes as the task fails (at once for one that failed already), so no later
        // Get returns it.
        _ = value.ContinueWith(_ =>
        {
            lock (_lock)
                if (_entries.TryGetValue(slot, out var entry) && entry.Value == value) _entries.Remove(slot);
        }, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return value;
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
