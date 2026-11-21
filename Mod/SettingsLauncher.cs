using System;
using System.Diagnostics;
using System.IO;
namespace TohotopiaMod;

internal sealed class SettingsLauncher
{
    float nextAttempt;
    // The settings app is a standalone tool the player starts from the
    // desktop shortcut; F8 remains as an in-game convenience and the mod
    // itself stays headless (no automatic launch).
    public void Open()
    {
        if (GameRuntime.BatchMode) return;
        if ((float)GameRuntime.Now < nextAttempt)
            return;
        nextAttempt = (float)GameRuntime.Now + 2;
        try
        {
            string exe = Path.Combine(Battle.InstallDirectory, "Settings", "TH1LLMBattlePanel.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("模型对局窗口未安装", exe);
            using var game = Process.GetCurrentProcess();
            WriteLaunchRequest(Battle.DataDirectory, game.Id);
            // file:// URI on the executable itself: shell shortcuts stored in
            // LocalLow trigger a Windows security prompt, the direct URI does not.
            GameReflection.InvokeStaticMethod(GameReflection.FindMethod("UnityEngine.Application", "OpenURL", 1),
                new object[] { new Uri(exe).AbsoluteUri });
            Battle.Log.LogInfo("Opening model battle settings window");
        }
        catch (Exception e) { Battle.Log.LogError("无法打开模型对局窗口：" + e.Message); }
    }

    // The URI scheme cannot carry command line arguments, so the owning
    // process id travels through a small file the settings app reads and
    // deletes on startup.
    static void WriteLaunchRequest(string dataDirectory, int ownerPid)
    {
        string request = Path.Combine(dataDirectory, "settings-launch.json");
        string temporary = request + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string body = "{\"ownerPid\":" + ownerPid +
            ",\"createdUtc\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
        try
        {
            File.WriteAllText(temporary, body);
            if (File.Exists(request)) File.Replace(temporary, request, null);
            else File.Move(temporary, request);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
