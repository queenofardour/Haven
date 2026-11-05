using FFXIVClientStructs.FFXIV.Client.UI;
using Haven.Core;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HavenNative;

internal static unsafe class ChatTypingInput
{
    private static bool CursorVisible(AtkResNode* node)
    {
        // Read visibility through the cursor's ancestors without invoking a game member function.
        if (node == null) return false;
        for (var depth = 0; node != null && depth < 64; depth++, node = node->ParentNode)
            if ((node->NodeFlags & NodeFlags.Visible) == 0) return false;
        return node == null;
    }
    public static TypingState Observe(AddonChatLog* addon, TypingActivity activity, long now)
    {
        if (addon == null || addon->TextInput == null) return activity.Observe(now, false, []);
        var input = addon->TextInput;
        var focused = addon->IsVisible && (input->IsActive || CursorVisible(input->CursorContainer));
        var draft = input->RawString.AsSpan();
        if (draft.IsEmpty && input->AtkTextNode != null) draft = input->AtkTextNode->NodeText.AsSpan();
        if (draft.Length > 8192) return activity.Observe(now, false, []);
        return activity.Observe(now, focused, draft);
    }
}
