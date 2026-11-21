using System.Diagnostics;
using FSharp.ModelsDev.Catalog;
using Tohotopia;

static class StartupProfile
{
    internal static async Task Run()
    {
        var clock = Stopwatch.StartNew();
        void Report(string stage) { Console.WriteLine($"PROFILE {stage}: {clock.Elapsed.TotalMilliseconds:F1} ms"); clock.Restart(); }
        var snapshot = new SnapshotCatalog();
        Report("SnapshotCatalog constructor");
        Console.WriteLine($"Providers: {snapshot.ProviderIds.Count}");
        Report("offline provider index");
        Console.WriteLine($"OpenAI models: {snapshot.GetModels("openai").Count}");
        Report("offline OpenAI models");
        int count = snapshot.ProviderIds.Sum(id => snapshot.GetModels(id).Count);
        Console.WriteLine($"Models: {count}");
        Report("all offline models");
        string oldRoot = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-startup-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string cached = Path.Combine(TohotopiaShared.RuntimePaths.DefaultRoot, "models-dev.json");
            string copy = Path.Combine(root, "models-dev.json");
            if (!File.Exists(cached)) throw new FileNotFoundException("Local catalog required for comparison", cached);
            File.Copy(cached, copy);
            Files.Root = root;
            clock.Restart();
            Console.WriteLine($"API-key providers: {ProviderConnections.ApiKeyProviderIds.Count()}");
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
