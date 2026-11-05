using Haven.Core.Odyssey;

namespace Haven.Games;

/// <summary>
/// The Odyssey's files in the plugin's configuration folder: the run in progress (odyssey-run.json, written after every
/// screen change) and the records (odyssey-records.json). Writes are atomic: a temporary file, then a move over the
/// old one, so a crash mid-write leaves the last good save. The plugin constructs this and the director's Changed
/// event drives it; a damaged file is reported and treated as absent.
/// </summary>
public sealed class OdysseySaves(string directory, Action<string>? log = null)
{
    public string RunPath => Path.Combine(directory, "odyssey-run.json");
    public string RecordsPath => Path.Combine(directory, "odyssey-records.json");

    /// <summary>The saved run, or null when there is none (or it can't be read).</summary>
    public OdysseyRun? LoadRun() => Load(RunPath, OdysseyJson.Load);
    public OdysseyRecords? LoadRecords() => Load(RecordsPath, OdysseyJson.LoadRecords);

    private T? Load<T>(string path, Func<string, T> parse) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return parse(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            log?.Invoke($"[ODYSSEY] Couldn't read {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    public void SaveRun(OdysseyRun run) => Write(RunPath, OdysseyJson.Save(run));
    public void SaveRecords(OdysseyRecords records) => Write(RecordsPath, OdysseyJson.Save(records));
    /// <summary>Forgets the saved run (after a finished run's summary is closed).</summary>
    public void DeleteRun()
    {
        try { if (File.Exists(RunPath)) File.Delete(RunPath); }
        catch (Exception e) { log?.Invoke($"[ODYSSEY] Couldn't delete the run file: {e.Message}"); }
    }

    private void Write(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            log?.Invoke($"[ODYSSEY] Couldn't save {Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>
    /// Loads the director from the files (resuming a saved run mid-duel deals it again) and wires its Changed event to
    /// the files: the run after every change, the records once the run is over.
    /// </summary>
    public Odyssey Open(IReadOnlyList<Core.TriadCard> cards, Func<int, IReadOnlyList<Core.TriadCard>, Core.ITriadBrain> brains)
    {
        Odyssey odyssey;
        try { odyssey = new Odyssey(cards, brains, LoadRecords(), LoadRun()); odyssey.Resume(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A saved run the director can't take up (content changed since it was saved, or a damaged file) would break
            // the Board every time it's asked about: it's kept beside the save, and the Odyssey opens without it.
            log?.Invoke($"[ODYSSEY] Couldn't take up the saved run ({e.Message}); it's kept as odyssey-run.json.bad.");
            try { if (File.Exists(RunPath)) File.Move(RunPath, RunPath + ".bad", overwrite: true); } catch (IOException) { }
            odyssey = new Odyssey(cards, brains, LoadRecords(), null);
        }
        odyssey.Changed += () =>
        {
            if (odyssey.Run is { } run) SaveRun(run);
            if (odyssey.Run is { Over: true }) SaveRecords(odyssey.Records);
        };
        // A binder Resume mended (MendBinder) is saved as it now stands.
        if (odyssey.Run is { Over: false } open) SaveRun(open);
        return odyssey;
    }
}
