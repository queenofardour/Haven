using System.Security.Cryptography;
using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// Which character each saved home belongs to, kept beside the homes rather than in them, so the home format and
/// shared visits are unchanged. A home not listed is unsorted: it was saved before homes belonged to characters, and
/// every character sees it until one keeps it. Characters are stored as a hash of their content ID, never the ID.
/// </summary>
public sealed class HomeOwners
{
    private readonly string path;
    private Dictionary<Guid, string> owners = [];

    public HomeOwners(string path)
    {
        this.path = Path.GetFullPath(path);
        if (!File.Exists(this.path)) return;
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllBytes(this.path));
            owners = read?.Where(x => x.Key != Guid.Empty && IsKey(x.Value)).ToDictionary(x => x.Key, x => x.Value) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { owners = []; }
    }

    /// <summary>The stored key for a character: the first 16 hex digits of SHA-256 of its content ID; empty when no
    /// character is logged in.</summary>
    public static string Key(ulong contentId)
    {
        if (contentId == 0) return "";
        Span<byte> id = stackalloc byte[8]; BitConverter.TryWriteBytes(id, contentId);
        return Convert.ToHexString(SHA256.HashData(id))[..16];
    }

    // A home is shown to a character that owns it, or to anyone while it is unsorted.
    public bool Visible(Guid home, string character) => !owners.TryGetValue(home, out var owner) || character.Length > 0 && owner == character;
    public bool Unsorted(Guid home) => !owners.ContainsKey(home);

    public void Assign(Guid home, string character)
    {
        if (home == Guid.Empty || !IsKey(character) || owners.GetValueOrDefault(home) == character) return;
        owners[home] = character; Save();
    }

    public void Forget(Guid home) { if (owners.Remove(home)) Save(); }

    private static bool IsKey(string? key) => key is { Length: 16 } && key.All(Uri.IsHexDigit);

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(owners)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
