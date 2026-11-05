// The production transport is compiled unchanged into the headless test harness.
// Only Dalamud's logging boundary is substituted; no game/native API is loaded.
namespace Dalamud.Plugin.Services;

public interface IPluginLog
{
    void Information(string message, params object[] values);
    void Warning(string message, params object[] values);
    void Warning(Exception? exception, string message, params object[] values);
    void Error(string message, params object[] values);
    void Error(Exception? exception, string message, params object[] values);
    void Debug(string message, params object[] values);
}

public sealed class ClientTestLog : IPluginLog
{
    public void Information(string message, params object[] values) { }
    public void Warning(string message, params object[] values) { }
    public void Warning(Exception? exception, string message, params object[] values) { }
    public void Error(string message, params object[] values) { }
    public void Error(Exception? exception, string message, params object[] values) { }
    public void Debug(string message, params object[] values) { }
}
