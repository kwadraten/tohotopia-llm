using System.Text.Json;
using System.Text.Json.Nodes;
using FSharp.ModelsDev;

namespace Tohotopia;

// Apply at the wire boundary as SDK codecs can introduce their own defaults.
public static class OutputTokenPolicy
{
    public static string Apply(AgentConfig config, string protocol, string body)
    {
        if (config.Reasoning == "none") return body;
        var request = JsonNode.Parse(body)!.AsObject();
        foreach (string key in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens" })
            request.Remove(key);
        if (request["thinking"] is JsonObject thinking) thinking.Remove("budget_tokens");
        if (request["generationConfig"] is JsonObject generation)
        {
            generation.Remove("maxOutputTokens");
            if (generation["thinkingConfig"] is JsonObject thinkingConfig)
                thinkingConfig.Remove("thinkingBudget");
        }
        // Messages requires max_tokens. Use the model's published maximum,
        // rather than an application-defined output/thinking allowance.
        if (protocol == "anthropic-messages")
        {
            long maximum = ModelMaximum(config);
            request["max_tokens"] = maximum;
            // Older Messages models require an explicit thinking budget and
            // require it to be smaller than max_tokens. Adaptive models omit it.
            if (request["thinking"] is JsonObject legacy && legacy["type"]?.GetValue<string>() == "enabled")
                legacy["budget_tokens"] = maximum - 1;
        }
        return request.ToJsonString();
    }

    static long ModelMaximum(AgentConfig config)
    {
        string path = Path.Combine(Files.Root, "models-dev.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty(config.Provider, out var provider) &&
                provider.TryGetProperty("models", out var models) && models.TryGetProperty(config.Model, out var model) &&
                model.TryGetProperty("limit", out var limit) && limit.TryGetProperty("output", out var output) &&
                output.TryGetInt64(out long maximum) && maximum > 0)
                return maximum;
        }
        if (ProviderConnections.Catalog.TryGet(config.Provider, config.Model, out var info) && info.MaxOutputTokens is > 0)
            return info.MaxOutputTokens.Value;
        throw new InvalidOperationException("无法读取模型 " + config.Provider + "/" + config.Model + " 的输出上限；Messages 协议必须填写 max_tokens，请刷新模型目录。");
    }
}

internal sealed class OutputTokenTransport(AgentConfig config, string protocol, IChatTransport inner) : IChatTransport
{
    public Task<TransportResponse> Post(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken cancellationToken) =>
        inner.Post(url, headers, OutputTokenPolicy.Apply(config, protocol, body), cancellationToken);

    public IAsyncEnumerable<string> PostStream(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken cancellationToken) =>
        inner.PostStream(url, headers, OutputTokenPolicy.Apply(config, protocol, body), cancellationToken);
}
