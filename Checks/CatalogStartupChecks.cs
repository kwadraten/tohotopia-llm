using System.Net;
using Tohotopia;

static class CatalogStartupChecks
{
    const string Cached = """{"xai":{"name":"Cached xAI","models":{"cached":{"name":"Cached model","reasoning":true}}}}""";
    const string Updated = """{"fixture-gateway":{"name":"Updated gateway","npm":"@ai-sdk/openai-compatible","api":"https://fixture.invalid/v1","env":["FIXTURE_API_KEY"],"models":{"updated":{"name":"Updated model"}}}}""";
    static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception(text);
        Console.WriteLine("PASS " + text);
    }
    internal static async Task Run()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-catalog-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Files.Root = root;
            string path = Path.Combine(root, "models-dev.json");
            File.WriteAllText(path, Cached);
            using var handler = new ControlledDownload();
            using var http = new HttpClient(handler);
            var fresh = new ModelCatalog(new OAuth(), http);
            Check((await fresh.Providers(default).WaitAsync(TimeSpan.FromSeconds(2))).Single().Name == "Cached xAI" && handler.Calls == 0,
                "fresh catalog loads locally without a network request");

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            var stale = new ModelCatalog(new OAuth(), http);
            using var request = new CancellationTokenSource();
            var providers = await stale.Providers(request.Token).WaitAsync(TimeSpan.FromSeconds(2));
            request.Cancel();
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var readers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => stale.Models("grok", default))).WaitAsync(TimeSpan.FromSeconds(2));
            Check(providers.Single().Name == "Cached xAI" && readers.All(models => models.Single().Id == "cached") && handler.Calls == 1,
                "stale catalog and concurrent readers return immediately while one download remains blocked");
            Check(!handler.RequestToken.IsCancellationRequested, "ending an HTTP request does not cancel the shared background refresh");
            handler.Reply.SetResult(new(HttpStatusCode.OK) { Content = new StringContent(Updated) });
            for (int i = 0; i < 200 && !(await stale.Models("fixture-gateway", default)).Any(); i++) await Task.Delay(10);
            Check((await stale.Models("fixture-gateway", default)).Single().Id == "updated" && File.ReadAllText(path) == Updated,
                "background refresh atomically persists the catalog and updates subsequent reads");
            Check((await stale.Providers(default)).Single().ConnectionSupported && ProviderConnections.ApiUrl("fixture-gateway") == "https://fixture.invalid/v1",
                "refreshed provider metadata reaches authentication and inference routing");
            Check(handler.Calls == 1 && !Directory.EnumerateFiles(root, "*.tmp").Any(), "successful refresh leaves no temporary files or repeated downloads");

            foreach (string? invalid in new string?[] { null, "{broken", "[]" })
            {
                File.Delete(path);
                if (invalid != null) File.WriteAllText(path, invalid);
                using var offlineHandler = new ControlledDownload();
                using var offlineHttp = new HttpClient(offlineHandler);
                var offline = new ModelCatalog(new OAuth(), offlineHttp);
                var fallback = await offline.Providers(default).WaitAsync(TimeSpan.FromSeconds(2));
                var models = await offline.Models("openai", default).WaitAsync(TimeSpan.FromSeconds(2));
                Check(fallback.Any(p => p.Id == "openai" && p.ConnectionSupported) && models.Count > 0,
                    $"{(invalid == null ? "missing" : "invalid")} cache uses embedded providers and text models without waiting for the network");
                Check(models.All(m => !m.Id.Contains("embedding", StringComparison.OrdinalIgnoreCase)), "offline fallback excludes embedding models");
                await offlineHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                offlineHandler.Reply.SetResult(new(HttpStatusCode.ServiceUnavailable));
                for (int i = 0; i < 5; i++) await offline.Providers(default);
                Check(offlineHandler.Calls == 1 && (invalid == null ? !File.Exists(path) : File.ReadAllText(path) == invalid),
                    "offline failure retains local state and backs off instead of downloading on every request");
            }
        }
        finally { Files.Root = prior; Directory.Delete(root, true); }
    }
    sealed class ControlledDownload : HttpMessageHandler
    {
        public int Calls;
        public CancellationToken RequestToken;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (request.RequestUri?.AbsoluteUri != "https://models.dev/api.json") throw new Exception("Unexpected catalog request");
            RequestToken = ct;
            Started.TrySetResult();
            return await Reply.Task.WaitAsync(ct);
        }
    }
}
