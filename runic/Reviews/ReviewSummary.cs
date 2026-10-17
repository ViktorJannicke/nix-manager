using System.Globalization;
using NixManager.Daemon;
using NixManager.Domain;
using NixManager.Operations;

namespace NixManager.Reviews;

/// <summary>
/// Review summaries, sizes and time estimates (<c>src/summary.ts</c>). The tray's notifications use them in the
/// dashboard's language (<see cref="TrayText"/>); the dashboard's frontend keeps its own copy.
/// </summary>
public static class ReviewSummary
{
    /// <summary>Sizes in binary units, optionally with a sign (<c>bytes</c>), with the decimal separator of <paramref name="numbers"/>.</summary>
    public static string Bytes(double? value, bool signed = false, IFormatProvider? numbers = null)
    {
        if (value is not { } number || double.IsNaN(number)) return "—";
        var sign = signed ? (number > 0 ? "+" : number < 0 ? "−" : "±") : number < 0 ? "−" : "";
        var size = Math.Abs(number);
        string[] scale = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        while (size >= 1024 && index < scale.Length - 1)
        {
            size /= 1024;
            index++;
        }
        var digits = size >= 100 || index == 0 ? 0 : 1;
        return $"{sign}{size.ToString(digits == 0 ? "F0" : "F1", numbers ?? CultureInfo.InvariantCulture)} {scale[index]}";
    }

    /// <summary>A conjunction as <c>Intl.ListFormat("en-GB")</c> writes it: "a, b and c".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };

    private static string Plural(long count, string one, string many) =>
        (count == 1 ? one : many).Replace("{count}", count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>Up to three names, then how many more.</summary>
    private static string Names(IEnumerable<string> items, TrayText text)
    {
        var unique = items.Distinct(StringComparer.Ordinal).ToList();
        return unique.Count <= 3 ? text.List(unique) : text.List([.. unique.Take(2), text.Messages.tray_more(unique.Count - 2)]);
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>What applying a review does, in a few plain sentences (<c>reviewSummary</c>); English unless <paramref name="text"/> says otherwise.</summary>
    public static string Of(BuildReview review, TrayText? text = null)
    {
        text ??= TrayText.English;
        var m = text.Messages;
        var outputs = new[] { review.Home, review.System }.Where(output => output is not null).Select(output => output!).ToList();
        var parts = new List<string>();
        if (review.Edits is { } edits)
        {
            var packages = edits.Effects.Select(effect =>
            {
                var removedNames = effect.Removed.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
                var addedNames = effect.Added.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
                return (Added: effect.Added.Where(item => !removedNames.Contains(item.Name)), Removed: effect.Removed.Where(item => !addedNames.Contains(item.Name)));
            }).ToList();
            var added = packages.SelectMany(effect => effect.Added.Select(item => item.Name)).ToList();
            var removed = packages.SelectMany(effect => effect.Removed.Select(item => item.Name)).ToList();
            var settings = edits.Effects.Sum(effect => effect.Options.Count);
            if (added.Count > 0) parts.Add(m.tray_installs(Names(added, text)));
            if (removed.Count > 0) parts.Add(m.tray_removes(Names(removed, text)));
            if (settings > 0) parts.Add(m.tray_changes_settings(settings));
        }
        else if (outputs.Any(output => output.Compared))
        {
            var changes = outputs.Where(output => output.Compared).SelectMany(output => output.Changes).ToList();
            long changed = changes.Count(c => c.Kind == "changed"), added = changes.Count(c => c.Kind == "added"), removed = changes.Count(c => c.Kind == "removed");
            // Only the first count names what is counted: "updates 12 packages, adds 2 and removes 1".
            if (changed > 0) parts.Add(m.tray_updates_packages(changed));
            if (added > 0) parts.Add(parts.Count > 0 ? m.tray_adds(added) : m.tray_adds_packages(added));
            if (removed > 0) parts.Add(parts.Count > 0 ? m.tray_removes_count(removed) : m.tray_removes_packages(removed));
            if (review.Home is { Compared: false }) parts.Add(m.tray_home_first_time);
            if (review.System is { Compared: false }) parts.Add(m.tray_system_new);
        }
        else parts.Add(m.tray_everything_new);
        var sentences = new List<string> { parts.Count > 0 ? $"{Capitalize(text.List(parts))}." : m.tray_nothing_you_use };
        if (review.Inputs is { Changes.Count: > 0 } inputs)
            sentences.Insert(0, m.tray_sources(inputs.Changes.Count));
        var sizes = outputs.Where(output => output.ClosureSize is not null && output.CurrentClosureSize is not null).ToList();
        var delta = sizes.Sum(output => output.ClosureSize!.Value - output.CurrentClosureSize!.Value);
        if (sizes.Count > 0 && Math.Abs(delta) >= 1024 * 1024)
            sentences.Add(delta > 0 ? m.tray_needs_space(text.Bytes(delta)) : m.tray_frees_space(text.Bytes(-delta)));
        if (review.Downloaded is >= 1024 * 1024) sentences.Add(m.tray_downloaded(text.Bytes(review.Downloaded)));
        if (review.System?.Kernel is { After: { } after } kernel && kernel.Before != after) sentences.Add(m.tray_restart_kernel);
        return string.Join(" ", sentences);
    }

    private static bool SameKind(Request a, Request b) =>
        a.Action == b.Action && a.Scope == b.Scope && a.Update == b.Update && (a.Edits == true) == (b.Edits == true) && !a.DryRun;

    /// <summary>
    /// Typical seconds for <paramref name="request"/>: the median of the last five earlier runs of the same kind, if
    /// there were two (<c>usualSeconds</c>).
    /// </summary>
    public static double? UsualSeconds(IEnumerable<Operation> operations, Request request)
    {
        var seconds = operations
            .Where(operation => operation.Status == OperationStatuses.Succeeded && operation.FinishedAt is not null && SameKind(operation.Request, request))
            .OrderByDescending(operation => operation.StartedAt, StringComparer.Ordinal)
            .Take(5)
            .Select(operation => (Timestamps.Parse(operation.FinishedAt) - Timestamps.Parse(operation.StartedAt))?.TotalSeconds)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Order()
            .ToList();
        if (seconds.Count < 2) return null;
        var middle = seconds.Count / 2;
        return seconds.Count % 2 == 1 ? seconds[middle] : (seconds[middle - 1] + seconds[middle]) / 2;
    }

    private static string Roughly(double seconds)
    {
        var minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 1) return "less than a minute";
        if (minutes < 60) return Plural(minutes, "about {count} minute", "about {count} minutes");
        var hours = Math.Round(minutes / 6.0, MidpointRounding.AwayFromZero) / 10;
        return $"about {hours.ToString(CultureInfo.InvariantCulture)} {(hours == 1 ? "hour" : "hours")}";
    }

    /// <summary>A plain estimate of the time left, or null without earlier runs (<c>timeLeft</c>).</summary>
    public static string? TimeLeft(double? usual, double elapsed)
    {
        if (usual is not { } typical) return null;
        if (elapsed > typical * 1.2 + 30) return "Taking longer than usual";
        return Capitalize($"{Roughly(Math.Max(0, typical - elapsed))} left");
    }
}
