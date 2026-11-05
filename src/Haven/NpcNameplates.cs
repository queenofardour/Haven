using Dalamud.Game.Gui.NamePlate;
using Dalamud.Plugin.Services;

namespace Haven;

/// <summary>
/// The game's own nameplate over each NPC: its name as the home has it, never with a level (as players'
/// plates; the level shows on the target bars, NpcTargetBars). The game keeps a nameplate's text until something asks for it again, so a renamed NPC kept
/// its old name overhead; a name change asks for every nameplate to be drawn again.
/// </summary>
internal sealed class NpcNameplates : IDisposable
{
    private readonly INamePlateGui plates;
    private readonly Func<int, string?> nameAt;

    public NpcNameplates(INamePlateGui plates, Func<int, string?> nameAt)
    {
        this.plates = plates;
        this.nameAt = nameAt;
        plates.OnNamePlateUpdate += Update;
    }

    /// <summary>An NPC's name changed (or one appeared): draws the nameplates again.</summary>
    public void Refresh() => plates.RequestRedraw();

    private void Update(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        foreach (var handler in handlers)
        {
            if (handler.GameObject is not { } shown || nameAt(shown.ObjectIndex) is not { } name) continue;
            handler.Name = name;
            handler.RemoveLevelPrefix();
        }
    }

    public void Dispose() => plates.OnNamePlateUpdate -= Update;
}
