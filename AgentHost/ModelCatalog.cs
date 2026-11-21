using System.Text.Json;
using FSharp.ModelsDev.Catalog;
namespace Tohotopia;

public sealed class ModelCatalog(OAuth oauth, HttpClient? client = null)
{
    readonly HttpClient http = client ?? new() { Timeout = TimeSpan.FromSeconds(30) };
    JsonElement data; bool initialized; DateTime nextRefresh;
    Task? refresh;
    readonly SemaphoreSlim gate = new(1, 1);
    static string Source(string p) => p switch { "grok" => "xai", "kimi" => "kimi-code-plan-global", _ => p };
    public static string Driver(string p) => p switch { "xai" => "grok", "kimi-code-plan-global" => "kimi", _ => p };
    public async Task<List<CatalogProvider>> Providers(CancellationToken ct)
    {
        var current = await Load(ct);
        var providers = current.ValueKind == JsonValueKind.Object
            ? current.EnumerateObject().Select(p => (Id: p.Name, Name: p.Value.TryGetProperty("name", out var n) ? n.GetString() ?? p.Name : p.Name))
            : SnapshotCatalog.Default.ProviderIds.Select(id => (Id: id, Name: SnapshotCatalog.Default.TryGetProvider(id, out var p) ? p.Name : id));
        return providers.Select(p =>
        {
            var methods = OAuth.AuthMethods(p.Id);
            return new CatalogProvider(p.Id, p.Name, OAuth.SupportsInference(p.Id), methods);
        }).OrderByDescending(p => p.ConnectionSupported).ThenBy(p => p.Name).ToList();
    }
    async Task<JsonElement> Load(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            string path = Path.Combine(Files.Root, "models-dev.json");
            if (!initialized)
            {
                try
                {
                    data = ReadCatalog(await File.ReadAllTextAsync(path, ct));
                    nextRefresh = File.GetLastWriteTimeUtc(path).AddDays(1);
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { }
                initialized = true;
            }
            if (DateTime.UtcNow >= nextRefresh && (refresh == null || refresh.IsCompleted))
            {
                // A slow or unreachable models.dev must never delay local catalog reads.
                // Back off after failure and share one refresh across concurrent requests.
                nextRefresh = DateTime.UtcNow.AddMinutes(5);
                refresh = Task.Run(() => Refresh(path));
            }
            return data;
        }
        finally { gate.Release(); }
    }
    internal static JsonElement ReadCatalog(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("模型目录格式错误");
        return doc.RootElement.Clone();
    }
    async Task Refresh(string path)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string raw = await http.GetStringAsync("https://models.dev/api.json", timeout.Token);
            var updated = ReadCatalog(raw);
            await File.WriteAllTextAsync(temporary, raw, timeout.Token);
            await gate.WaitAsync(timeout.Token);
            try
            {
                File.Move(temporary, path, true);
                data = updated;
                nextRefresh = DateTime.UtcNow.AddDays(1);
            }
            finally { gate.Release(); }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
        {
            Console.Error.WriteLine("模型目录后台更新失败，继续使用本地目录：" + e.Message);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
        }
    }
    public static List<CatalogModel> ParseModels(JsonElement info)
    {
        var result = new List<CatalogModel>();
        if (!info.TryGetProperty("models", out var models))
            return result;
        foreach (var item in models.EnumerateObject())
        {
            var m = item.Value;
            if (m.TryGetProperty("family", out var family) && (family.GetString() ?? "").Contains("embedding", StringComparison.OrdinalIgnoreCase))
                continue;
            if (m.TryGetProperty("modalities", out var modalities) && modalities.TryGetProperty("output", out var output) && (!output.EnumerateArray().Any(x => x.GetString() == "text") || output.EnumerateArray().Any(x => x.GetString() != "text")))
                continue;
            bool reason = m.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.True;
            var efforts = new List<string>();
            if (m.TryGetProperty("reasoning_options", out var options))
            foreach (var option in options.EnumerateArray())
            if (option.TryGetProperty("type", out var type) && type.GetString() == "effort" && option.TryGetProperty("values", out var values))
            foreach (var value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                efforts.Add(value.GetString()!);
            result.Add(new(item.Name, m.TryGetProperty("name", out var name) ? name.GetString() ?? item.Name : item.Name, reason, efforts.Distinct().ToArray()));
        }
        return result.OrderBy(m => m.Id).ToList();
    }
    public async Task<List<CatalogModel>> Models(string provider, CancellationToken ct)
    {
        var current = await Load(ct);
        string p = Source(provider);
        var result = current.ValueKind == JsonValueKind.Object
            ? (current.TryGetProperty(p, out var info) ? ParseModels(info) : new List<CatalogModel>())
            : SnapshotCatalog.Default.GetModels(p)
                .Where(m => !(m.Family ?? "").Contains("embedding", StringComparison.OrdinalIgnoreCase)
                    && (m.OutputModalities.Count == 0 || m.OutputModalities.All(output => output == "text")))
                .Select(m => new CatalogModel(m.ModelId, m.DisplayName, m.SupportsReasoning, m.ReasoningEfforts.ToArray()))
                .OrderBy(m => m.Id).ToList();
        if (Driver(provider) == "openai" && oauth.CanInfer("openai"))
        {
            using var response = await OpenAIModels(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("无法读取 ChatGPT 订阅模型列表：HTTP " + (int)response.StatusCode);
            using var live = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var available = new List<CatalogModel>();
            foreach (var m in (live.RootElement.TryGetProperty("models", out var list) ? list : live.RootElement.GetProperty("data")).EnumerateArray())
            {
                if (m.TryGetProperty("supported_in_api", out var api) && api.ValueKind == JsonValueKind.False)
                    continue;
                if (m.TryGetProperty("visibility", out var visibility) && visibility.GetString() != "list")
                    continue;
                string id = m.TryGetProperty("slug", out var slug) ? slug.GetString()! : m.GetProperty("id").GetString()!;
                string[] levels = m.TryGetProperty("supported_reasoning_levels", out var levelsJson) ? levelsJson.EnumerateArray().Select(x => x.GetProperty("effort").GetString()!).ToArray() : Array.Empty<string>();
                var metadata = result.FirstOrDefault(x => x.Id == id);
                if (levels.Length == 0 && metadata != null)
                    levels = metadata.Efforts;
                available.Add(new(id, m.TryGetProperty("display_name", out var name) ? name.GetString() ?? id : id, levels.Length > 0 || metadata?.Reasoning == true, levels));
            }
            return available;
        }
        return result;
    }
    async Task<HttpResponseMessage> OpenAIModels(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var token = await oauth.Credentials("openai", ct, attempt > 0);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            request.Headers.Authorization = new("Bearer", token.Access);
            var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new InvalidOperationException("刷新授权后仍无法读取 ChatGPT 模型列表");
    }
}


