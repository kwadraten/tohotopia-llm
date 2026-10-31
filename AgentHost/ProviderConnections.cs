using System.Text.Json;
using FSharp.ModelsDev.Catalog;

namespace Tohotopia;

// Credentials and inference routing both come from catalog metadata. A provider
// can accept a saved key before its specialized inference protocol is supported.
public sealed class ProviderConnections : IModelCatalog
{
    static readonly SnapshotCatalog snapshot = new();
    static readonly object gate = new();
    static string loadedPath = "";
    static DateTime loadedStamp;
    static Dictionary<string, ProviderDefinition> definitions = new(StringComparer.Ordinal);
    public static ProviderConnections Catalog { get; } = new();

    static string Protocol(string npm) => npm switch
    {
        "@ai-sdk/google" => "gemini",
        "@ai-sdk/anthropic" => "anthropic-messages",
        "@ai-sdk/openai" or "@ai-sdk/openai-compatible" or "@openrouter/ai-sdk-provider"
            or "@ai-sdk/deepinfra" or "@ai-sdk/groq" or "@ai-sdk/mistral"
            or "@ai-sdk/cerebras" or "@ai-sdk/perplexity" or "@ai-sdk/togetherai"
            or "@ai-sdk/xai" => "openai-chat",
        _ => "unknown"
    };

    static string DefaultUrl(string provider) => provider switch
    {
        "google" => "https://generativelanguage.googleapis.com/v1beta",
        "groq" => "https://api.groq.com/openai/v1",
        "mistral" => "https://api.mistral.ai/v1",
        "cerebras" => "https://api.cerebras.ai/v1",
        "perplexity" => "https://api.perplexity.ai",
        "deepinfra" => "https://api.deepinfra.com/v1/openai",
        "togetherai" => "https://api.together.xyz/v1",
        _ => ""
    };

    static Dictionary<string, ProviderDefinition> Definitions()
    {
        lock (gate)
        {
            string path = Path.Combine(Files.Root, "models-dev.json");
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path == loadedPath && stamp == loadedStamp && definitions.Count > 0) return definitions;
            var next = new Dictionary<string, ProviderDefinition>(StringComparer.Ordinal);
            foreach (string id in snapshot.ProviderIds)
                if (snapshot.TryGetProvider(id, out var definition)) next[id] = definition;
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var item in doc.RootElement.EnumerateObject())
                {
                    var value = item.Value;
                    string Read(string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
                    string[] env = value.TryGetProperty("env", out var variables) && variables.ValueKind == JsonValueKind.Array
                        ? variables.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [];
                    next[item.Name] = new(item.Name, Read("name"), Read("api"), Protocol(Read("npm")), env, Read("doc"));
                }
            }
            definitions = next;
            loadedPath = path;
            loadedStamp = stamp;
            return next;
        }
    }

    public static bool TryKeyDefinition(string provider, out ProviderDefinition definition)
    {
        if (!Definitions().TryGetValue(provider, out definition!)) return false;
        // GitHub tokens need a Copilot session-token exchange, not direct API-key authentication.
        if (provider is "github-copilot" or "github-copilot-enterprise" or "openai-codex") return false;
        return definition.EnvVars.Any(e => e.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)
                || e.Equals("API_KEY", StringComparison.OrdinalIgnoreCase)
                || e.EndsWith("_API_TOKEN", StringComparison.OrdinalIgnoreCase)
                || e.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase)
                || e.EndsWith("_PAT", StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryApiDefinition(string provider, out ProviderDefinition definition) => TryKeyDefinition(provider, out definition)
        && definition.ProtocolId is "openai-chat" or "openai-compatible" or "anthropic-messages" or "gemini";

    public static string ApiUrl(string provider)
    {
        if (!TryKeyDefinition(provider, out var definition)) return "";
        return string.IsNullOrWhiteSpace(definition.BaseUrl) ? DefaultUrl(provider) : definition.BaseUrl;
    }

    public static ProviderAuthMethod[] AuthMethods(string provider)
    {
        if (!TryKeyDefinition(provider, out _)) return [];
        string url = ApiUrl(provider);
        bool template = url.Contains('{') || url.Contains('}');
        return [new("api", "填写 API Key", [
            new("secret", "apiKey", "API Key", Required: true),
            new("text", "baseUrl", "API 根地址", Placeholder: template ? url : "https://…", Required: TryApiDefinition(provider, out _), DefaultValue: template ? "" : url)
        ])];
    }

    public static IEnumerable<string> ApiKeyProviderIds => Definitions().Keys.Where(id => TryKeyDefinition(id, out _)).ToArray();
    public string SnapshotVersion => snapshot.SnapshotVersion;
    public IReadOnlyList<string> ProviderIds => Definitions().Keys.ToArray();
    public bool TryGetProvider(string providerId, out ProviderDefinition provider) => Definitions().TryGetValue(providerId, out provider!);
    public bool TryGet(string providerId, string modelId, out ModelInfo model) => snapshot.TryGet(providerId, modelId, out model);
    public IReadOnlyList<ModelInfo> GetModels(string providerId) => snapshot.GetModels(providerId);
}
