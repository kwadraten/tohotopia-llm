using System.Diagnostics;
using Xunit;

// Runs the PowerShell test scripts under pwsh and asserts they pass.
[Collection("Loader")]
public class ScriptTests
{
    static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task RemoveBepInEx_script_behaviour() => await RunScript("RemoveBepInExTests.ps1");

    [Fact]
    public async Task Install_script_behaviour()
    {
        string loader = Environment.GetEnvironmentVariable("TOHOTOPIA_LOADER_DLL")
            ?? @"C:\Program Files (x86)\Steam\steamapps\workshop\content\3774440\3829555573\0.8.5d\TH1.Hotfix.dll";
        if (!File.Exists(loader)) Assert.Skip("No loader DLL available for the install fixture; set TOHOTOPIA_LOADER_DLL");
        await RunScript("InstallTests.ps1", "-LoaderDll", loader);
    }

    static async Task RunScript(string name, params string[] arguments)
    {
        string script = Path.Combine(Repo, "Tests", "ModTests", name);
        Assert.True(File.Exists(script), name + " exists next to the tests");
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        Assert.True(process.ExitCode == 0, name + " passed:" + Environment.NewLine + output);
    }
}
