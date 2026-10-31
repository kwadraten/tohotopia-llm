using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
namespace TohotopiaMod;

internal sealed class SettingsLauncher
{
    bool launched, wasLobbyOpen; float nextAttempt;
    public void Poll(bool lobbyOpen)
    {
        if (!launched || lobbyOpen && !wasLobbyOpen)
            Open();
        wasLobbyOpen = lobbyOpen;
    }
    public void Open()
    {
        if (Time.realtimeSinceStartup < nextAttempt)
            return;
        nextAttempt = Time.realtimeSinceStartup + 2;
        try
        {
            string exe = Path.Combine(Battle.InstallDirectory, "Settings", "Settings.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("模型对局窗口未安装", exe);
            var start = new ProcessStartInfo(exe, "--show") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe), WindowStyle = ProcessWindowStyle.Normal };
            start.Environment["TOHOTOPIA_LLM_DATA"] = Path.Combine(Battle.InstallDirectory, "runtime");
            using var game = Process.GetCurrentProcess();
            start.Environment["TOHOTOPIA_LLM_OWNER_PID"] = game.Id.ToString();
            start.Environment["TOHOTOPIA_LLM_OWNER_START"] = game.StartTime.ToUniversalTime().Ticks.ToString();
            Process.Start(start)?.Dispose();
            launched = true;
            Battle.Log.LogInfo("Opening model battle settings window");
        }
        catch (Exception e) { Battle.Log.LogError("无法打开模型对局窗口：" + e.Message); }
    }
}
