using System.Diagnostics;
using TohotopiaShared;
namespace Tohotopia;

public sealed class ModUpdateService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string plugin = Path.GetFullPath(Path.Combine(Files.Root, ".."));
        string pending = Path.Combine(plugin, "Tohotopia.LLMBattle.dll.pending");
        string target = Path.Combine(plugin, "Tohotopia.LLMBattle.dll");
        string game = Path.GetFullPath(Path.Combine(plugin, "..", "..", "..", "TOHOTOPIA.exe"));
        bool notified = false;
        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(pending) && File.Exists(game))
                try
                {
                    bool running = false;
                    foreach (var process in Process.GetProcessesByName("TOHOTOPIA"))
                    {
                        using (process)
                        {
                            try
                            {
                                if (string.Equals(process.MainModule?.FileName, game, StringComparison.OrdinalIgnoreCase))
                                    running = true;
                            }
                            catch { running = true; }
                        }
                    }
                    if (running)
                    {
                        if (!notified)
                        {
                            notified = true;
                            AgentEvents.Write(Files.Root, new(DateTimeOffset.UtcNow, "host", "mod_update_pending", "新动作日志埋点已就绪；游戏退出后自动安装，下次启动生效", "warning"));
                        }
                    }
                    else
                    {
                        if (File.Exists(target))
                            File.Copy(target, target + ".bak", true);
                        File.Copy(pending, target + ".tmp", true);
                        File.Move(target + ".tmp", target, true);
                        File.Delete(pending);
                        AgentEvents.Write(Files.Root, new(DateTimeOffset.UtcNow, "host", "mod_updated", "动作日志插件已安装；下次游戏启动生效"));
                        notified = false;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
