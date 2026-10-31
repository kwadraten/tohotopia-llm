using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
namespace Settings;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        string root = Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_DATA") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "runtime"));
        string name = "Tohotopia.ModelBattle." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..24];
        using var mutex = new Mutex(true, "Local\\" + name, out bool first);
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
        ApplicationConfiguration.Initialize();
        using var stop = new CancellationTokenSource();
        using var window = new SettingsWindow();
        window.Shown += (_, _) => { _ = Listen(name, window, stop.Token); _ = CloseWhenOwnerExits(window, stop.Token); };
        try
        {
            Application.Run(window);
        }
        finally { stop.Cancel(); mutex.ReleaseMutex(); }
    }
    static async Task CloseWhenOwnerExits(Form window, CancellationToken ct)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_PID"), out int pid) ||
            !long.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_START"), out long started))
            return;
        try
        {
            using var owner = Process.GetProcessById(pid);
            if (owner.StartTime.ToUniversalTime().Ticks == started)
                await owner.WaitForExitAsync(ct);
        }
        catch (ArgumentException) { }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException) { }
        if (!ct.IsCancellationRequested && !window.IsDisposed)
            try { window.BeginInvoke((Action)(() => window.Close())); } catch (InvalidOperationException) { }
    }
    static async Task Listen(string name, Form window, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                var signal = new byte[1];
                if (await pipe.ReadAsync(signal, ct) == 0)
                    continue;
                window.BeginInvoke((Action)(() => { if (window.IsDisposed) return; window.Show(); if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal; window.BringToFront(); window.Activate(); }));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
