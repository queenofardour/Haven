using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Haven.Core;

namespace Haven.Native.PatchCheck;

/// <summary>
/// What Haven found of the game's code this session: every signature it looked for and every hook it made from one,
/// whether it worked, and which part of Haven asked. Filled by <see cref="RecordingSigScanner"/> and
/// <see cref="RecordingInterop"/>, which Haven's parts receive instead of Dalamud's own services, so nothing has to
/// report itself. Read by /haven selfcheck and the startup summary.
/// </summary>
internal static class PatchReport
{
    internal sealed record Entry(string Owner, string Method, string Kind, string Pattern, bool Found, string? Error);

    private static readonly object gate = new();
    private static readonly List<Entry> entries = [];

    public static IReadOnlyList<Entry> Entries { get { lock (gate) return entries.ToList(); } }

    public static void Record(string kind, string pattern, bool found, string? error)
    {
        var (owner, method) = Caller();
        lock (gate)
        {
            // A part that looks again (a retry, a second instance) keeps one line per pattern: the latest answer.
            entries.RemoveAll(e => e.Owner == owner && e.Pattern == pattern && e.Kind == kind);
            entries.Add(new Entry(owner, method, kind, pattern, found, error));
        }
    }

    /// <summary>The part of Haven that asked: the first frame outside the recorders, Dalamud and the runtime.</summary>
    private static (string Owner, string Method) Caller()
    {
        foreach (var frame in new StackTrace(2, false).GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            while (type?.DeclaringType != null) type = type.DeclaringType; // lambdas and local functions
            if (type == null || type == typeof(PatchReport) || type == typeof(RecordingSigScanner) || type == typeof(RecordingInterop)) continue;
            var space = type.Namespace ?? "";
            if (space.StartsWith("System", StringComparison.Ordinal) || space.StartsWith("Dalamud", StringComparison.Ordinal)) continue;
            return (type.Name, Clean(method!.Name));
        }
        return ("unknown", "");
    }

    // "<Initialize>b__12_0" → "Initialize".
    private static string Clean(string name) => name.StartsWith('<') && name.IndexOf('>') is var end and > 1 ? name[1..end] : name;

    /// <summary>The feature a lookup belongs to, as the player knows it.</summary>
    public static PatchParts.Part PartOf(Entry entry) => PatchParts.Of(entry.Owner, entry.Method);

    /// <summary>The summary: how many lookups were found, then what's missing, grouped by feature.</summary>
    public static IEnumerable<string> Lines()
    {
        var all = Entries;
        var missing = all.Where(e => !e.Found).ToList();
        yield return $"{all.Count - missing.Count} of {all.Count} code lookups found.";
        foreach (var group in missing.GroupBy(PartOf).OrderByDescending(g => g.Key.Critical).ThenBy(g => g.Key.Name))
            yield return $"  Missing for {group.Key.Name}{(group.Key.Critical ? " (needed to enter homes)" : "")}: "
                + string.Join("; ", group.Select(e => $"{e.Owner}.{e.Method} [{e.Kind}] {e.Pattern}"));
    }
}
