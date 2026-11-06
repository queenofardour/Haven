using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Haven.Core;

namespace Haven.Native.PatchCheck;

/// <summary>
/// Keeps the features that write game memory at Haven's own offsets (<see cref="PatchFeature"/>) off while the game
/// is newer than the version this Haven was checked on (verified-game-version.txt, set when a release has been
/// tried in game), unless a canary shows the field is still in place or the player turns them on for this game version
/// (Settings → Advanced).
/// </summary>
internal static class PatchGuard
{
    /// <summary>The game version this build was checked on.</summary>
    public static string Verified { get; } = ReadVerified();
    /// <summary>The running game's version (set at startup).</summary>
    public static string Running { get; private set; } = "";
    /// <summary>The game version the player turned paused features on for (Settings → Advanced), or empty.</summary>
    public static string AllowedFor { get; set; } = "";

    private static readonly HashSet<PatchFeature> canaries = [];
    private static readonly HashSet<PatchFeature> failed = [];
    private static readonly object gate = new();

    /// <summary>Called when a feature's canary fails: off for the session, whatever else says.</summary>
    public static Action<PatchFeature, string>? CanaryFailed;

    public static void Start(string running) => Running = running ?? "";

    public static bool Checked => PatchGate.Checked(Verified, Running);

    public static bool Allows(PatchFeature feature)
    {
        lock (gate)
            return !failed.Contains(feature) && PatchGate.Allows(Verified, Running, AllowedFor, canaries.Contains(feature));
    }

    /// <summary>A runtime check found the feature's field where Haven expects it.</summary>
    public static void CanaryPassed(PatchFeature feature) { lock (gate) canaries.Add(feature); }

    /// <summary>A runtime check found the feature's field holding something it never should: off for this session.</summary>
    /// <remarks>
    /// On the game version Haven was checked on, a failed canary only logs (the canary's assumption would be what's
    /// wrong, and the feature was tried there); on any other it switches the feature off, whatever Settings says.
    /// </remarks>
    public static void Canary(PatchFeature feature, string why)
    {
        bool first;
        lock (gate) first = Checked ? warned.Add(feature) : failed.Add(feature);
        if (!first) return;
        if (Checked) Warned?.Invoke(feature, why);
        else CanaryFailed?.Invoke(feature, why);
    }
    private static readonly HashSet<PatchFeature> warned = [];
    /// <summary>A canary failed on the checked game version: logged, nothing switched off.</summary>
    public static Action<PatchFeature, string>? Warned;

    public static IReadOnlyList<PatchFeature> Paused => Enum.GetValues<PatchFeature>().Where(f => !Allows(f)).ToList();

    public static IEnumerable<string> Lines()
    {
        yield return Checked
            ? $"Game {Running}: the version this Haven was checked on."
            : $"Game {Running}: newer than the version this Haven was checked on ({(Verified.Length == 0 ? "unknown" : Verified)}).";
        var paused = Paused;
        if (paused.Count > 0) yield return "  Paused until Haven is updated: " + string.Join(", ", paused.Select(f => PatchGate.Names[f])) + ".";
        else if (!Checked) yield return "  Nothing paused" + (PatchGate.Allows(Verified, Running, AllowedFor) ? " (turned on in Settings → Advanced)." : ".");
        lock (gate)
            foreach (var feature in failed) yield return $"  {PatchGate.Names[feature]}: off, its runtime check failed.";
    }

    private static string ReadVerified()
    {
        try
        {
            using var stream = typeof(PatchGuard).Assembly.GetManifestResourceStream("Haven.VerifiedGameVersion");
            if (stream == null) return "";
            using var reader = new StreamReader(stream);
            // The first line that isn't a comment.
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.Trim() is { Length: > 0 } text && !text.StartsWith('#')) return text;
        }
        catch { }
        return "";
    }
}
