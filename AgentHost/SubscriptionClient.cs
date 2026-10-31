using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using FSharp.ModelsDev.MEAi;
using FSharp.ModelsDev.Providers;
namespace Tohotopia;

public sealed partial class SubscriptionClient : IChatClient
{
    readonly AgentConfig config; readonly OAuth oauth; readonly HttpClient http; readonly IChatClient? modelsDevClient; readonly Action<string>? reasoningSummaryWriter; readonly Action<string, string>? progressWriter;
    public SubscriptionClient(AgentConfig config, OAuth oauth, HttpClient? client = null, Action<string>? reasoningSummaryWriter = null, Action<string, string>? progressWriter = null)
    {
        this.config = config with
        {
            Provider = ModelCatalog.Driver(config.Provider)
        };
        this.oauth = oauth;
        http = client ?? new HttpClient() { Timeout = Timeout.InfiniteTimeSpan };
        this.reasoningSummaryWriter = reasoningSummaryWriter;
        this.progressWriter = progressWriter;
        if (this.config.Provider == "openai" && !string.IsNullOrWhiteSpace(this.config.Model))
        {
            var providerOptions = new FSharp.ModelsDev.ProviderOptions { ProtocolId = "openai-responses" };
            var catalog = new FSharp.ModelsDev.Catalog.SnapshotCatalog();
            bool modelSupportsReasoning = catalog.TryGet("openai", this.config.Model, out var modelInfo) && modelInfo.SupportsReasoning;
            bool supportsConciseSummary = SupportsConciseReasoningSummary(this.config.Model);
            bool reasoningEnabled = this.config.Reasoning != "none" && (modelSupportsReasoning || supportsConciseSummary || this.config.Reasoning != "default");
            bool requestReasoningSummary = reasoningEnabled && supportsConciseSummary;
            var providerFactory = new ProviderFactory(
                catalog,
                new OAuthBearerCredentials(oauth, "openai"),
                null,
                new ReasoningSummaryTransport(new OutputTokenTransport(this.config, "openai-responses", new FSharp.ModelsDev.HttpChatTransport(http)), requestReasoningSummary, progressWriter));
            modelsDevClient = providerFactory.CreateClient("openai", "tohotopia", this.config.Model, providerOptions).AsIChatClient();
        }
        else if (this.config.Provider is not ("anthropic" or "grok" or "kimi" or "moonshotai")
            && !string.IsNullOrWhiteSpace(this.config.Model)
            && ProviderConnections.TryApiDefinition(this.config.Provider, out var definition))
        {
            var providerOptions = new FSharp.ModelsDev.ProviderOptions
            {
                ProtocolId = definition.ProtocolId,
                BaseUrl = oauth.ApiBaseUrl(this.config.Provider)
            };
            var factory = new ProviderFactory(ProviderConnections.Catalog,
                new OAuthBearerCredentials(oauth, this.config.Provider, definition.ProtocolId),
                null, new OutputTokenTransport(this.config, definition.ProtocolId, new FSharp.ModelsDev.HttpChatTransport(http)));
            modelsDevClient = factory.CreateClient(this.config.Provider, "tohotopia", this.config.Model, providerOptions).AsIChatClient();
        }
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { modelsDevClient?.Dispose(); http.Dispose(); }
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            list.Insert(0, new ChatMessage(ChatRole.System, options.Instructions));
        // The Agent Framework may call this client repeatedly while a tool call is
        // being resolved. Keep that loop bounded even if a provider ignores the
        // single-call hint below. The last user message starts this decision.
        var lastUser = list.FindLastIndex(m => m.Role == ChatRole.User);
        int toolCalls = list.Skip(Math.Max(0, lastUser + 1)).Sum(m => m.Contents?.OfType<FunctionCallContent>().Count() ?? 0);
        int remainingToolCalls = Math.Max(0, 4 - toolCalls);
        var tools = remainingToolCalls == 0 ? Array.Empty<AIFunction>() : (options?.Tools ?? Array.Empty<AITool>()).OfType<AIFunction>().ToArray();
        if (remainingToolCalls == 0)
            list.Insert(0, new ChatMessage(ChatRole.System, "规则查询次数已达上限。现在依据已查资料返回最终行动 JSON，未查明的规则明确标为未知。"));
        if (modelsDevClient is not null)
        {
            if (config.Provider == "openai") CompleteOpenAIReplay(list);
            var sdkOptions = options?.Clone() ?? new ChatOptions();
            if (config.Reasoning != "none") sdkOptions.MaxOutputTokens = null;
            sdkOptions.ModelId = config.Model;
            sdkOptions.Tools = tools;
            if (config.Reasoning != "default")
            {
                sdkOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                sdkOptions.AdditionalProperties["FSharp.ModelsDev.ReasoningEffort"] = config.Reasoning;
            }
            var response = progressWriter is null
                ? await modelsDevClient.GetResponseAsync(list, sdkOptions, cancellationToken)
                : await ObserveUpdates(modelsDevClient.GetStreamingResponseAsync(list, sdkOptions, cancellationToken), cancellationToken).ToChatResponseAsync(cancellationToken);
            string reasoningSummary = ReadOpenAIReasoningSummary(response);
            if (reasoningSummary.Length > 0 && progressWriter is null)
                reasoningSummaryWriter?.Invoke(reasoningSummary);
            var sdkAssistant = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (sdkAssistant is null)
                return response;
            if (remainingToolCalls == 0 && sdkAssistant.Contents.Any(c => c is FunctionCallContent))
                throw new InvalidOperationException("模型在规则查询禁用后仍返回工具调用，未生成行动决策");
            var capped = CapFunctionCalls(sdkAssistant, remainingToolCalls);
            return ReferenceEquals(capped, sdkAssistant) ? response : new ChatResponse(capped);
        }
        string? text;
        ChatMessage? assistant = null;
        if (config.Provider == "openai")
            assistant = await OpenAI(list, tools, options, cancellationToken);
        else if (config.Provider == "anthropic")
            assistant = await Anthropic(list, tools, options, cancellationToken);
        else if (config.Provider is "grok" or "kimi" or "moonshotai")
        {
            var body = new Dictionary<string, object> { { "model", config.Model }, { "stream", progressWriter is not null }, { "messages", OpenAIChatMessages(list) }, { "max_tokens", 4096 } };
            AddOpenAIChatTools(body, tools, options);
            if (config.Provider == "grok" && config.Reasoning != "default")
                body["reasoning_effort"] = config.Reasoning;
            if (config.Provider == "kimi")
                body["thinking"] = new
                {
                    type = config.Reasoning == "none" ? "disabled" : "enabled"
                };
            else if (config.Provider == "moonshotai" && config.Model.Contains("k2.6", StringComparison.OrdinalIgnoreCase))
                body["thinking"] = new { type = config.Reasoning == "none" ? "disabled" : "enabled" };
            else if (config.Provider == "moonshotai" && config.Model.Contains("k3", StringComparison.OrdinalIgnoreCase) && config.Reasoning != "default")
                body["reasoning_effort"] = config.Reasoning;
            string endpoint = config.Provider switch
            {
                "grok" => "https://api.x.ai/v1/chat/completions",
                "kimi" => "https://api.kimi.com/coding/v1/chat/completions",
                "moonshotai" => (await oauth.Credentials("moonshotai", cancellationToken)).BaseUrl.TrimEnd('/') + "/chat/completions",
                _ => throw new ArgumentException("未知提供商")
            };
            using var response = await Send(endpoint, body, cancellationToken, progressWriter is not null);
            if (IsEventStream(response))
                assistant = await ReadChatStream(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken);
            else
            {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            assistant = ParseOpenAIChatMessage(doc.RootElement.GetProperty("choices")[0].GetProperty("message"));
            }
        }
        else
            throw new ArgumentException("未知提供商");
        if (assistant is not null)
        {
            if (remainingToolCalls == 0 && assistant.Contents.Any(c => c is FunctionCallContent))
                throw new InvalidOperationException("模型在规则查询禁用后仍返回工具调用，未生成行动决策");
            assistant = CapFunctionCalls(assistant, remainingToolCalls);
        }
        text = assistant?.Text;
        return new ChatResponse(assistant ?? new ChatMessage(ChatRole.Assistant, text ?? ""));
    }
    sealed class ReasoningSummaryTransport(FSharp.ModelsDev.IChatTransport inner, bool enabled, Action<string, string>? progress) : FSharp.ModelsDev.IChatTransport
    {
        public async Task<FSharp.ModelsDev.TransportResponse> Post(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken cancellationToken)
        {
            string? bodyWithSummary = enabled ? AddSummaryRequest(body) : null;
            if (bodyWithSummary is null)
                return await inner.Post(url, headers, body, cancellationToken);
            try
            {
                return await inner.Post(url, headers, bodyWithSummary, cancellationToken);
            }
            catch (FSharp.ModelsDev.AiException e) when (e.StatusCode == (int)HttpStatusCode.BadRequest)
            {
                // Some older reasoning models do not accept the summary option.
                return await inner.Post(url, headers, body, cancellationToken);
            }
        }
        public async IAsyncEnumerable<string> PostStream(string url, IReadOnlyDictionary<string, string> headers, string body, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string? bodyWithSummary = enabled ? AddSummaryRequest(body) : null;
            if (bodyWithSummary is null)
            {
                await foreach (string line in CompatibleResponseEvents(inner.PostStream(url, headers, body, cancellationToken), cancellationToken))
                    { ObserveResponseEvent(line, progress); yield return line; }
                yield break;
            }
            IAsyncEnumerator<string>? enumerator = CompatibleResponseEvents(inner.PostStream(url, headers, bodyWithSummary, cancellationToken), cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                bool retryWithoutSummary = false;
                bool hasFirstLine = false;
                try
                {
                    hasFirstLine = await enumerator.MoveNextAsync();
                }
                catch (FSharp.ModelsDev.AiException e) when (e.StatusCode == (int)HttpStatusCode.BadRequest)
                {
                    retryWithoutSummary = true;
                }
                if (retryWithoutSummary)
                {
                    await enumerator.DisposeAsync();
                    enumerator = null;
                    await foreach (string line in CompatibleResponseEvents(inner.PostStream(url, headers, body, cancellationToken), cancellationToken))
                        { ObserveResponseEvent(line, progress); yield return line; }
                    yield break;
                }
                if (hasFirstLine)
                    { ObserveResponseEvent(enumerator.Current, progress); yield return enumerator.Current; }
                while (await enumerator.MoveNextAsync())
                    { ObserveResponseEvent(enumerator.Current, progress); yield return enumerator.Current; }
            }
            finally
            {
                if (enumerator is not null)
                    await enumerator.DisposeAsync();
            }
        }
        static string? AddSummaryRequest(string body)
        {
            try
            {
                if (JsonNode.Parse(body) is not JsonObject request)
                    return null;
                if (request["reasoning"] is JsonObject reasoning)
                    reasoning["summary"] ??= JsonValue.Create("concise");
                else
                    request["reasoning"] = new JsonObject { ["summary"] = "concise" };
                return request.ToJsonString();
            }
            catch (JsonException) { return null; }
        }
    }
    static string ReadOpenAIReasoningSummary(ChatResponse response)
    {
        const string providerStateKey = "FSharp.ModelsDev.ProviderState";
        var summaries = new List<string>();
        foreach (var message in response.Messages)
        {
            if (message.AdditionalProperties is null || !message.AdditionalProperties.TryGetValue(providerStateKey, out object? state))
                continue;
            string? json = state switch
            {
                string s => s,
                JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString(),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(json))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type) || type.GetString() != "reasoning" ||
                        !item.TryGetProperty("summary", out var parts) || parts.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("type", out var partType) && partType.GetString() == "summary_text" &&
                            part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()))
                            summaries.Add(text.GetString()!);
                }
            }
            catch (JsonException) { }
        }
        string result = string.Join("\n\n", summaries);
        const int maxCharacters = 2000;
        return result.Length <= maxCharacters ? result : result[..maxCharacters] + "\n…[摘要已截断]";
    }
    static bool SupportsConciseReasoningSummary(string model)
    {
        string id = model.ToLowerInvariant();
        if (id == "computer-use-preview")
            return true;
        if (!id.StartsWith("gpt-", StringComparison.Ordinal))
            return false;
        string majorText = id[4..].Split('.', '-')[0];
        return int.TryParse(majorText, out int major) && major >= 5;
    }
    sealed class OAuthBearerCredentials(OAuth oauth, string provider, string protocol = "") : IRefreshableCredentialProvider
    {
        int refreshAfterUnauthorized;
        public bool CanRefresh => protocol.Length == 0;
        public void Invalidate(string accountId) => Interlocked.Exchange(ref refreshAfterUnauthorized, 1);
        public async Task<IReadOnlyDictionary<string, string>> GetHeaders(string accountId, CancellationToken cancellationToken)
        {
            var token = await oauth.Credentials(provider, cancellationToken, Interlocked.Exchange(ref refreshAfterUnauthorized, 0) != 0);
            if (protocol == "gemini")
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-goog-api-key"] = token.Access };
            if (protocol == "anthropic-messages")
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-api-key"] = token.Access };
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token.Access };
        }
    }
    static ChatMessage CapFunctionCalls(ChatMessage message, int maxCalls)
    {
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        if (calls.Length <= maxCalls) return message;
        var keep = calls.Take(maxCalls).ToHashSet();
        var contents = message.Contents.Where(c => c is not FunctionCallContent call || keep.Contains(call)).ToList();
        if (maxCalls == 0)
            contents.Add(new TextContent("规则查询次数已达上限；请依据已查资料作答，未查明的规则明确标为未知。"));
        return new ChatMessage(ChatRole.Assistant, contents);
    }
    public static object OpenAIBody(AgentConfig config, IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction>? tools = null, ChatOptions? options = null)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = config.Model,
            ["instructions"] = string.Join("\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)),
            ["input"] = OpenAIResponsesInput(messages.Where(m => m.Role != ChatRole.System)),
            ["tools"] = OpenAIResponseTools(tools ?? Array.Empty<AIFunction>()),
            ["tool_choice"] = tools is { Count: > 0 } ? "auto" : "none",
            ["parallel_tool_calls"] = options?.AllowMultipleToolCalls ?? false,
            ["store"] = false,
            ["stream"] = true
        };
        if (config.Reasoning != "default")
        {
            body["reasoning"] = new
            {
                effort = config.Reasoning
            };
            if (config.Reasoning != "none")
                body["include"] = new[] { "reasoning.encrypted_content" };
        }
        return body;
    }
    async Task<ChatMessage> OpenAI(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> tools, ChatOptions? options, CancellationToken ct)
    {
        using var response = await Send("https://api.openai.com/v1/responses", OpenAIBody(config, messages, tools, options), ct, true);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ReadResponseMessage(stream, ct, progressWriter);
    }
    async Task<ChatMessage> Anthropic(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> tools, ChatOptions? options, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { { "model", config.Model }, { "system", string.Join("\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)) }, { "messages", AnthropicMessages(messages.Where(m => m.Role != ChatRole.System)) }, { "max_tokens", 8192 }, { "stream", progressWriter is not null }, { "tools", AnthropicTools(tools) } };
        if (tools.Count > 0)
            body["tool_choice"] = options?.AllowMultipleToolCalls == false ? new { type = "auto", disable_parallel_tool_use = true } : new { type = "auto" };
        if (config.Reasoning is not "none" and not "default")
        {
            body["thinking"] = new
            {
                type = "adaptive"
            };
            body["output_config"] = new
            {
                effort = config.Reasoning
            };
        }
        using var response = await Send("https://api.anthropic.com/v1/messages", body, ct, progressWriter is not null);
        if (IsEventStream(response))
            return await ReadAnthropicStream(await response.Content.ReadAsStreamAsync(ct), ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return ParseAnthropicMessage(doc.RootElement.GetProperty("content"));
    }
    async Task<HttpResponseMessage> Send(string endpoint, object body, CancellationToken ct, bool sse = false)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var token = await oauth.Credentials(config.Provider, ct, attempt > 0);
            string protocol = config.Provider == "anthropic" ? "anthropic-messages" : "openai-chat";
            string payload = OutputTokenPolicy.Apply(config, protocol, JsonSerializer.Serialize(body));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Access);
            request.Headers.UserAgent.ParseAdd("TohotopiaLLM/0.2");
            // Direct ChatGPT plan tokens use the public Responses contract.
            // Codex originator/version/account headers select a different route.
            if (config.Provider == "anthropic")
            {
                request.Headers.Add("anthropic-version", "2023-06-01");
                if (token.AuthType == "api_key" && !string.IsNullOrWhiteSpace(token.WorkspaceId))
                    request.Headers.Add("anthropic-workspace-id", token.WorkspaceId);
                else if (token.AuthType != "api_key")
                    request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            }
            if (sse)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                string detail = "";
                try
                {
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var root = error.RootElement;
                    if (root.TryGetProperty("error", out var e))
                    {
                        if (e.ValueKind == JsonValueKind.String)
                            detail = e.GetString() ?? "";
                        else if (e.TryGetProperty("message", out var m))
                            detail = m.GetString() ?? "";
                    }
                    else if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                        detail = d.GetString() ?? "";
                }
                catch (JsonException) { }
                if (token.Access.Length > 0)
                    detail = detail.Replace(token.Access, "[redacted]");
                if (token.Refresh.Length > 0)
                    detail = detail.Replace(token.Refresh, "[redacted]");
                response.Dispose();
                throw new InvalidOperationException($"{config.Provider} 模型请求被服务拒绝：HTTP {code}。" + detail);
            }
            return response;
        }
        throw new InvalidOperationException("授权刷新后仍无法调用模型");
    }
    public static async Task<string> ReadResponses(Stream stream, CancellationToken ct) => (await ReadResponseMessage(stream, ct)).Text;
    public static async Task<ChatMessage> ReadResponseMessage(Stream stream, CancellationToken ct, Action<string, string>? progress = null)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        var data = new StringBuilder();
        var delta = new StringBuilder();
        bool completed = false;
        ChatMessage? result = null;
        var completedItems = new SortedDictionary<int, JsonElement>();
        void Event()
        {
            if (data.Length == 0)
                return;
            string json = data.ToString();
            data.Clear();
            if (json == "[DONE]")
                return;
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            string type = e.GetProperty("type").GetString()!;
            ObserveResponseEvent(json, progress);
            if (type == "response.output_text.delta") progress?.Invoke("model_output_delta", e.GetProperty("delta").GetString() ?? "");
            if (type == "response.output_text.delta")
                delta.Append(e.GetProperty("delta").GetString());
            if (type == "response.output_item.done" && e.TryGetProperty("item", out var item))
                completedItems[e.GetProperty("output_index").GetInt32()] = item.Clone();
            if (type is "error" or "response.failed" or "response.incomplete")
                throw new InvalidOperationException("模型响应失败或未完成");
            if (type == "response.completed")
            {
                completed = true;
                if (e.TryGetProperty("response", out var response) && response.TryGetProperty("output", out var output))
                    result = ParseOpenAIResponseOutput(output);
            }
        }
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length == 0)
            {
                Event();
                if (completed)
                    break;
            }
            else if (line.StartsWith("data:"))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
        Event();
        if (!completed)
            throw new InvalidOperationException("模型响应流提前结束，未收到完成事件");
        // Some subscription streams omit the final output snapshot. The item.done
        // events still contain the authoritative completed function calls/messages.
        if ((result is null || !result.Contents.Any()) && completedItems.Count > 0)
            result = ParseOpenAIResponseOutput(JsonSerializer.SerializeToElement(completedItems.Values));
        if (result is not null && (result.Text.Length > 0 || result.Contents.Any(c => c is FunctionCallContent)))
            return result;
        return new ChatMessage(ChatRole.Assistant, result?.Text.Length > 0 ? result.Text : delta.ToString());
    }

    static object[] OpenAIResponseTools(IReadOnlyList<AIFunction> tools) => tools.Select(t => (object)new { type = "function", name = t.Name, description = t.Description, parameters = t.JsonSchema }).ToArray();
    static object[] AnthropicTools(IReadOnlyList<AIFunction> tools) => tools.Select(t => (object)new { name = t.Name, description = t.Description, input_schema = t.JsonSchema }).ToArray();
    static void AddOpenAIChatTools(Dictionary<string, object> body, IReadOnlyList<AIFunction> tools, ChatOptions? options)
    {
        if (tools.Count == 0) return;
        body["tools"] = tools.Select(t => (object)new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.JsonSchema } }).ToArray();
        body["tool_choice"] = "auto";
        body["parallel_tool_calls"] = options?.AllowMultipleToolCalls ?? false;
    }
    static object[] OpenAIResponsesInput(IEnumerable<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages)
        {
            var content = new List<object>();
            foreach (var c in m.Contents ?? Array.Empty<AIContent>())
            {
                if (c is FunctionCallContent call)
                    result.Add(new { type = "function_call", call_id = call.CallId, name = call.Name, arguments = JsonSerializer.Serialize(call.Arguments) });
                else if (c is FunctionResultContent fr)
                    result.Add(new { type = "function_call_output", call_id = fr.CallId, output = ResultText(fr.Result) });
            }
            if (!string.IsNullOrEmpty(m.Text))
                content.Add(new { type = m.Role == ChatRole.Assistant ? "output_text" : "input_text", text = m.Text });
            if (content.Count > 0)
                result.Add(new { type = "message", role = m.Role == ChatRole.Assistant ? "assistant" : "user", content });
        }
        return result.ToArray();
    }
    static object[] OpenAIChatMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages)
        {
            var calls = (m.Contents ?? Array.Empty<AIContent>()).OfType<FunctionCallContent>().ToArray();
            var results = (m.Contents ?? Array.Empty<AIContent>()).OfType<FunctionResultContent>().ToArray();
            if (m.Role == ChatRole.Assistant && calls.Length > 0)
            {
                result.Add(new { role = "assistant", content = string.IsNullOrEmpty(m.Text) ? null : m.Text, tool_calls = calls.Select(c => new { id = c.CallId, type = "function", function = new { name = c.Name, arguments = JsonSerializer.Serialize(c.Arguments) } }).ToArray() });
            }
            else if (results.Length > 0)
                foreach (var r in results) result.Add(new { role = "tool", tool_call_id = r.CallId, content = ResultText(r.Result) });
            else if (!string.IsNullOrEmpty(m.Text))
                result.Add(new { role = m.Role.Value, content = m.Text });
        }
        return result.ToArray();
    }
    static object[] AnthropicMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages)
        {
            var content = new List<object>();
            if (!string.IsNullOrEmpty(m.Text)) content.Add(new { type = "text", text = m.Text });
            foreach (var c in m.Contents ?? Array.Empty<AIContent>())
            {
                if (c is FunctionCallContent call) content.Add(new { type = "tool_use", id = call.CallId, name = call.Name, input = call.Arguments });
                if (c is FunctionResultContent fr) content.Add(new { type = "tool_result", tool_use_id = fr.CallId, content = ResultText(fr.Result) });
            }
            if (content.Count > 0) result.Add(new { role = m.Role == ChatRole.Assistant ? "assistant" : "user", content });
        }
        return result.ToArray();
    }
    static string ResultText(object? result) => result switch
    {
        string s => s,
        JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        _ => JsonSerializer.Serialize(result)
    };
    static ChatMessage ParseOpenAIChatMessage(JsonElement m)
    {
        var content = new List<AIContent>();
        if (m.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(text.GetString()))
            content.Add(new TextContent(text.GetString()!));
        if (m.TryGetProperty("tool_calls", out var calls))
            foreach (var call in calls.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(fn.GetProperty("arguments").GetString() ?? "{}") ?? new();
                content.Add(new FunctionCallContent(call.GetProperty("id").GetString()!, fn.GetProperty("name").GetString()!, args));
            }
        return new ChatMessage(ChatRole.Assistant, content);
    }
    static ChatMessage ParseAnthropicMessage(JsonElement blocks)
    {
        var content = new List<AIContent>();
        foreach (var b in blocks.EnumerateArray())
        {
            if (b.GetProperty("type").GetString() == "text") content.Add(new TextContent(b.GetProperty("text").GetString() ?? ""));
            else if (b.GetProperty("type").GetString() == "tool_use")
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(b.GetProperty("input").GetRawText()) ?? new();
                content.Add(new FunctionCallContent(b.GetProperty("id").GetString()!, b.GetProperty("name").GetString()!, args));
            }
        }
        return new ChatMessage(ChatRole.Assistant, content);
    }
    static ChatMessage ParseOpenAIResponseOutput(JsonElement output)
    {
        var content = new List<AIContent>();
        foreach (var item in output.EnumerateArray())
        {
            string type = item.GetProperty("type").GetString() ?? "";
            if (type == "message")
            {
                foreach (var block in item.GetProperty("content").EnumerateArray())
                    if (block.GetProperty("type").GetString() == "output_text") content.Add(new TextContent(block.GetProperty("text").GetString() ?? ""));
            }
            else if (type == "function_call")
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetProperty("arguments").GetString() ?? "{}") ?? new();
                content.Add(new FunctionCallContent(item.GetProperty("call_id").GetString()!, item.GetProperty("name").GetString()!, args));
            }
        }
        return new ChatMessage(ChatRole.Assistant, content);
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var r = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, r.Text);
    }
}


