using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Haven.Core;

namespace HavenNative;

internal static unsafe class NativeEmoteOwnership
{
    public static bool IsOwned(UIState* ui, QuestManager* quests, uint link) =>
        EmoteOwnership.IsOwned(link, ui == null ? [] : ui->UnlockLinks, quests == null ? [] : quests->CompletedQuests);
}
