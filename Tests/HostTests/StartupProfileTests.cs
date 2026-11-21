using System.Diagnostics;
using FSharp.ModelsDev.Catalog;
using Tohotopia;
using Xunit;

// Manual profiling harness; opt in with TOHOTOPIA_STARTUP_PROFILE=1. Reports
// timings on the test output rather than asserting hard limits.
public class StartupProfileTests
{
    [Fact]
    public async Task Catalog_startup_profile()
    {
        if (Environment.GetEnvironmentVariable("TOHOTOPIA_STARTUP_PROFILE") != "1")
            Assert.Skip("Set TOHOTOPIA_STARTUP_PROFILE=1 to profile catalog startup");
        var clock = Stopwatch.StartNew();
        void Report(string stage) { Console.WriteLine($"PROFILE {stage}: {clock.Elapsed.TotalMilliseconds:F1} ms"); clock.Restart(); }
        var snapshot = new SnapshotCatalog();
        Report("SnapshotCatalog constructor");
        Report("offline provider index");
        int count = snapshot.ProviderIds.Sum(id => snapshot.GetModels(id).Count);
        Report("all offline models");
        string oldRoot = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-startup-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string cached = Path.Combine(TohotopiaShared.RuntimePaths.DefaultRoot, "models-dev.json");
            string copy = Path.Combine(root, "models-dev.json");
            Assert.True(File.Exists(cached), "local catalog required for comparison");
            File.Copy(cached, copy);
            Files.Root = root;
            clock.Restart();
            Report("application provider definitions");
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow);
            await new ModelCatalog(new OAuth()).Providers(CancellationToken.None);
            Report("fresh cached catalog / providers");
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddDays(-2));
            using var refreshClient = new HttpClient();
            await new ModelCatalog(new OAuth(), refreshClient).Providers(CancellationToken.None);
            Report("stale cached catalog / providers (background refresh)");
        }
        finally { Files.Root = oldRoot; Directory.Delete(root, true); }
    }
}
