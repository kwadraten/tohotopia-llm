using Xunit;

// Exercises the published NativeAOT panel executable end to end. Set
// TOHOTOPIA_PANEL_EXE to a built TH1LLMBattlePanel.exe to enable.
public class LifecycleTests
{
    static string Executable()
    {
        string? exe = Environment.GetEnvironmentVariable("TOHOTOPIA_PANEL_EXE");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) Assert.Skip("Set TOHOTOPIA_PANEL_EXE to a built TH1LLMBattlePanel.exe");
        return exe;
    }

    [Fact]
    public Task Panel_lifecycle() => LifecycleFlow.Run(Executable());

    [Fact]
    public Task Panel_lifecycle_through_desktop_shortcut() => LifecycleFlow.Run(Executable(), throughShortcut: true);
}
