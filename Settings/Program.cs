using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using TKCode.Arc;

namespace Settings;

static class Program
{
    static string? pipeName;
    static CancellationToken sessionToken;

    [STAThread]
    static void Main(string[] args)
    {
        ApplyLaunchArguments(args);
        string root = TohotopiaShared.RuntimePaths.PrepareRoot(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_DATA"));
        ResolveOwnerStartTime();
        string name = InstanceName(root);
        using var mutex = new Mutex(true, OperatingSystem.IsWindows() ? "Local\\" + name : name, out bool first);
        if (!first)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.Connect(2500);
                pipe.WriteByte(1);
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            return;
        }
        using var stop = new CancellationTokenSource();
        pipeName = name;
        sessionToken = stop.Token;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { stop.Cancel(); pipeName = null; mutex.ReleaseMutex(); }
    }

    public static AppBuilder BuildAvaloniaApp() => UseArcTheme(AppBuilder.Configure<App>().UsePlatformDetect()).LogToTrace();

    internal static AppBuilder UseArcTheme(AppBuilder app) => app.UseArc(arc => arc.UseArcControls().UseArcRibbon().WithTheme(t => t
        .Appearance(ArcAppearance.System).Accent(ArcAccentPreset.Blue)
        .Token("BodyFontFamily", Ui.Fonts).Token("DisplayFontFamily", Ui.Fonts)
        .Token("TypeXS", "11").Token("TypeSM", "13").Token("TypeBase", "14")
        .Token("ControlSizeSM", "26").Token("ControlSizeMD", "32").Token("ControlSizeLG", "38")
        .Token("RadiusControl", "10").Token("RadiusPanel", "14")
        .Token("Checkbox", "CopyMargin", "1,6.2,0,0")
        .Token("NumberField", "StepSizeSM", "18").Token("NumberField", "StepSizeMD", "24").Token("NumberField", "StepSizeLG", "30")));

    internal static void ApplyLaunchArguments(string[] args)
    {
        string? root = null;
        int? ownerPid = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-root")
            {
                if (++i == args.Length) throw new ArgumentException("Missing --data-root value");
                root = Path.GetFullPath(args[i]);
            }
            else if (args[i] == "--owner-pid")
            {
                if (++i == args.Length || !int.TryParse(args[i], out int pid) || pid <= 0)
                    throw new ArgumentException("Invalid --owner-pid value");
                ownerPid = pid;
            }
        }
        if (root != null) Environment.SetEnvironmentVariable("TOHOTOPIA_LLM_DATA", root);
        if (ownerPid.HasValue)
        {
            Environment.SetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_PID", ownerPid.Value.ToString());
            Environment.SetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_START", null);
        }
    }

    internal static string InstanceName(string root)
    {
        string path = Path.GetFullPath(root);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return "Tohotopia.ModelBattle." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
    }

    internal static void Attach(SettingsWindow window)
    {
        if (pipeName is not { } name) return;
        bool started = false;
        window.Opened += (_, _) =>
        {
            if (started) return;
            started = true;
            _ = Listen(name, window, sessionToken);
            _ = CloseWhenOwnerExits(window, sessionToken);
        };
    }

    static void ResolveOwnerStartTime()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_START")) ||
            !int.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_PID"), out int pid)) return;
        try
        {
            using var owner = Process.GetProcessById(pid);
            Environment.SetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_START", owner.StartTime.ToUniversalTime().Ticks.ToString());
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    static async Task CloseWhenOwnerExits(SettingsWindow window, CancellationToken ct)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_PID"), out int pid) ||
            !long.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_START"), out long started)) return;
        try
        {
            using var owner = Process.GetProcessById(pid);
            if (owner.StartTime.ToUniversalTime().Ticks == started)
                while (!owner.HasExited) await Task.Delay(250, ct);
        }
        catch (ArgumentException) { }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException) { }
        if (!ct.IsCancellationRequested) Dispatcher.UIThread.Post(window.Exit);
    }

    static async Task Listen(string name, SettingsWindow window, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                var signal = new byte[1];
                if (await pipe.ReadAsync(signal, ct) == 0) continue;
                Dispatcher.UIThread.Post(window.Present);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
