using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using Xunit;

static class LifecycleFlow
{
    internal static async Task Run(string executable, bool throughShortcut = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-settings-启动 空格-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "bridge.token"), "fixture-token");
        // A plain powershell process blocked on ReadLine acts as the owning
        // game process; closing its stdin simulates the game exiting.
        var ownerStart = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        ownerStart.ArgumentList.Add("-NoProfile");
        ownerStart.ArgumentList.Add("-Command");
        ownerStart.ArgumentList.Add("[Console]::In.ReadLine()");
        using var owner = Process.Start(ownerStart)!;
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["TOHOTOPIA_LLM_DATA"] = root;
        start.Environment["TOHOTOPIA_LLM_OWNER_PID"] = owner.Id.ToString();
        start.Environment["TOHOTOPIA_LLM_OWNER_START"] = owner.StartTime.ToUniversalTime().Ticks.ToString();
        if (throughShortcut)
        {
            string link = TohotopiaMod.SettingsLaunchLink.Write(executable, root, owner.Id);
            start = new ProcessStartInfo(link) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        }
        using var app = Process.Start(start)!;
        var output = throughShortcut ? Task.FromResult("") : app.StandardOutput.ReadToEndAsync();
        var errors = throughShortcut ? Task.FromResult("") : app.StandardError.ReadToEndAsync();
        try
        {
            await Task.Delay(1500);
            Assert.True(!app.HasExited, "NativeAOT Settings exits during startup: " + await errors);
            Assert.True(!app.Modules.Cast<ProcessModule>().Any(module => module.ModuleName is "coreclr.dll" or "clrjit.dll"), "NativeAOT process loads no CoreCLR or JIT");
            if (!throughShortcut) start.ArgumentList.Add("--show");
            using var duplicate = Process.Start(start)!;
            await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(duplicate.ExitCode == 0 && !app.HasExited, "repeated --show activation exits without creating a second instance");
            string legacy = "Tohotopia.ModelBattle." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..24];
            Assert.True(!OperatingSystem.IsWindows() || Settings.Program.InstanceName(root) == legacy, "existing Windows single-instance mutex and pipe identity is preserved");
            await owner.StandardInput.WriteLineAsync();
            await owner.StandardInput.FlushAsync();
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(app.ExitCode == 0, "settings exits cleanly when its owning game process exits: " + await errors);
        }
        finally
        {
            if (!app.HasExited) app.Kill();
            if (!owner.HasExited) owner.Kill();
            await app.WaitForExitAsync();
            await owner.WaitForExitAsync();
            await output;
            await errors;
            Directory.Delete(root, true);
        }
    }
}
