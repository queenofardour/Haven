using System;
using Dalamud.Game.Config;
using Dalamud.Game.Text.Evaluator;
using Dalamud.Plugin.Services;
using HavenNative.Services;
using Lumina.Excel.Sheets;

namespace HavenNative;

/// <summary>
/// The emote log line ("You blow Aria a kiss.") inside a home. Outside Haven the server prints it for everyone
/// when the performer has the emote window's "Display log message" on; inside a home emotes never reach the server,
/// so Haven renders the line from the Emote sheet and sends it through the room chat as an emote line.
/// </summary>
internal sealed unsafe class EmoteLogService
{
    [Dalamud.IoC.PluginService] internal ISeStringEvaluator Evaluator { get; private set; } = null!;
    [Dalamud.IoC.PluginService] internal IGameConfig GameConfig { get; private set; } = null!;
    private readonly IDataManager data;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly PrivateRoomChatService chat;
    private readonly Func<bool> enabled;
    private ushort lastEmote;
    private long lastAt;

    public EmoteLogService(IDataManager data, IObjectTable objects, IPluginLog log, PrivateRoomChatService chat, Func<bool> enabled)
    { this.data = data; this.objects = objects; this.log = log; this.chat = chat; this.enabled = enabled; }

    /// <summary>The game's "Display log message" choice in the emote window.</summary>
    public bool GameShowsLogMessages => GameConfig != null && GameConfig.TryGet(UiConfigOption.EmoteTextType, out uint value) && value != 0;

    /// <summary>Called on the framework thread whenever the local player performs an emote inside a home.</summary>
    public void LocalEmoted(ushort emoteId)
    {
        try
        {
            var now = Environment.TickCount64;
            // The agent and manager executors can both report one trigger.
            if (emoteId == lastEmote && now - lastAt < 400) return;
            lastEmote = emoteId; lastAt = now;
            if (!enabled() || !GameShowsLogMessages) return;
            var self = objects.LocalPlayer;
            if (self == null || data.GetExcelSheet<Emote>().GetRowOrDefault(emoteId) is not { } emote) return;
            var target = EmoteTargets.LocalTarget((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)self.Address);
            var row = target != null ? emote.LogMessageTargeted : emote.LogMessageUntargeted;
            if (row.RowId == 0 || row.ValueNullable is not { } message) return;
            var macro = message.Text.ToMacroString();
            var name = self.Name.TextValue;
            var targetName = target == null ? "" : target->NameString;
            var mine = EmoteLogText.Render(Evaluator, macro, name, name, targetName);
            // Rendered for a bystander, then sent without the leading name: the room shows it after the sender's name.
            var theirs = EmoteLogText.Render(Evaluator, macro, "\u0001", name, targetName);
            if (mine == null || theirs == null) return;
            chat.EmoteLine(mine, EmoteLogText.WithoutName(theirs, name), emoteId, targetName);
        }
        catch (Exception ex) { log.Debug(ex, "[Haven] Emote log line skipped."); }
    }

    /// <summary>
    /// A received emote line, worded for this viewer: the game's text with this player as the viewer, so its target
    /// reads "you". Without the sender's leading name, as the room prints it after the name. Null keeps the text.
    /// </summary>
    public string? RenderFor(Haven.Core.RoomChatMessage message)
    {
        try
        {
            if (message.Emote is <= 0 or > ushort.MaxValue || message.Name.Length == 0) return null;
            if (data.GetExcelSheet<Emote>().GetRowOrDefault((uint)message.Emote) is not { } emote) return null;
            var row = message.Target.Length > 0 ? emote.LogMessageTargeted : emote.LogMessageUntargeted;
            if (row.RowId == 0 || row.ValueNullable is not { } text) return null;
            var viewer = objects.LocalPlayer?.Name.TextValue ?? "\u0001";
            var line = EmoteLogText.Render(Evaluator, text.Text.ToMacroString(), viewer, message.Name, message.Target);
            return line == null ? null : EmoteLogText.WithoutName(line, message.Name);
        }
        catch (Exception ex) { log.Debug(ex, "[Haven] Emote line kept as sent."); return null; }
    }
}

internal static class EmoteLogText
{
    /// <summary>
    /// Evaluates an emote LogMessage macro string. The game fills global parameters when it prints one (1 the viewer,
    /// 2 the performer, 3 the target, 7 and 8 non-player names); Haven passes the same values as local parameters.
    /// </summary>
    public static string? Render(ISeStringEvaluator evaluator, string macro, string viewer, string performer, string target)
    {
        var local = macro.Replace("gstr1", "lstr1").Replace("gstr2", "lstr2").Replace("gstr3", "lstr3")
            .Replace("gnum7", "lnum7").Replace("gnum8", "lnum8");
        Span<SeStringParameter> parameters =
        [
            new(viewer), new(performer), new(target), new(0u), new(0u), new(0u), new(0u), new(0u),
        ];
        var text = evaluator.EvaluateMacroString(local, parameters).ExtractText().Trim();
        return text.Length == 0 || text.Contains('<') || text.Contains('\u0001') || text.Length > 400 ? null : text;
    }

    /// <summary>"Zoe Chen blows a kiss." → "blows a kiss."; null when the line does not start with the name.</summary>
    public static string? WithoutName(string line, string name)
        => line.StartsWith(name + " ", StringComparison.Ordinal) ? line[(name.Length + 1)..] : null;
}
