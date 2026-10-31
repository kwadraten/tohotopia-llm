using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Tohotopia;
using TohotopiaShared;

internal static class SubscriptionToolChecks
{
    internal static async Task Run()
    {
        void Check(bool value, string description)
        {
            if (!value) throw new Exception(description);
            Console.WriteLine("PASS " + description);
        }

        var providers = new[] { "openai", "kimi", "grok", "anthropic" };
        var prior = new Dictionary<string, byte[]?>();
        try
        {
            string rulesPath = Path.Combine(Files.Root, "fixture-game-rules.json");
            File.WriteAllText(rulesPath, JsonSerializer.Serialize(new GameRulesExport("ZH", new()
            {
                new("tech-1", "technology", "边境防卫", "增加城防", Relation: "前置", Source: "TechInfo", RelatedIds: new[] { "tech-0" }, Aliases: new[] { "边防" }),
                new("hero-1", "hero", "守护者", "等级一效果", Level: 1, Source: "SkillInfo"),
                new("hero-1", "hero", "守护者", "等级二效果", Level: 2, Source: "SkillInfo"),
                new("bad-1", "unit", "原始key", "", Source: "UnitTypeInfo unresolvedLocalization")
            })));
            var store = GameRuleStore.Load(rulesPath);
            Check(store.Search("边防", "technology").Contains("前置", StringComparison.Ordinal), "local rule search matches aliases and returns source relations");
            Check(store.Search("边防", "nonexistent").Contains("边境防卫", StringComparison.Ordinal) && store.Search("边防", "nonexistent").Contains("已跨类别搜索", StringComparison.Ordinal), "unsupported category falls back to searchable data with explicit notice");
            Check(store.Get("边防").Contains("tech-1", StringComparison.Ordinal), "rule lookup resolves a unique alias");
            Check(store.Get("hero-1").Contains("请补充 level", StringComparison.Ordinal), "local rule lookup reports level ambiguity");
            Check(store.Get("bad-1").Contains("本地化定义缺失，未知", StringComparison.Ordinal) && !store.Get("bad-1").Contains("原始key", StringComparison.Ordinal), "unresolved localization keys are never treated as definitions");

            foreach (string provider in providers)
            {
                string tokenPath = Path.Combine(Files.Root, provider + ".token");
                prior[tokenPath] = File.Exists(tokenPath) ? File.ReadAllBytes(tokenPath) : null;
                var token = new Token("fixture-access", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1), "fixture-account", "fixture-client", "chatgpt.tokens.use.direct");
                File.WriteAllBytes(tokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(token)), null, DataProtectionScope.CurrentUser));

                var transport = new ToolWireTransport(provider);
                var oauth = new OAuth(new HttpClient(new NoOAuthRequestHandler()));
                await StreamingChecks.Run(provider, oauth);
                using var client = new SubscriptionClient(new AgentConfig { Provider = provider, Model = "fixture-model", Reasoning = "none" }, oauth, new HttpClient(transport));
                var localTool = AIFunctionFactory.Create((string query) => "本地规则结果:" + query, "search_game_rules", "仅查询本地规则资料。", Files.Json);
                var agent = new ChatClientAgent(client, name: "ToolFixture", tools: new AITool[] { localTool });
                var session = await agent.CreateSessionAsync();
                var reply = await agent.RunAsync("查询本地规则，并以工具结果作答。", session);
                Check(reply.Text.Contains("本地规则结果:战争", StringComparison.Ordinal), $"{provider} agent invokes and returns the local rule tool");
                Check(transport.Bodies.Count == 2, $"{provider} tool loop stays to one lookup and one final response");
                string first = transport.Bodies[0], second = transport.Bodies[1];
                if (provider == "kimi")
                {
                    using var nonThinkingBody = JsonDocument.Parse(first);
                    Check(nonThinkingBody.RootElement.GetProperty("max_tokens").GetInt32() == 4096 &&
                        nonThinkingBody.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled",
                        "Kimi non-thinking requests retain the action-text cap");
                }
                Check(first.Contains("search_game_rules", StringComparison.Ordinal), $"{provider} request declares the function tool");
                Check(HasToolResult(provider, second), $"{provider} follow-up preserves call id and function result");
                if (provider == "anthropic")
                    Check(transport.SawAnthropicHeaders, "Anthropic tool requests retain OAuth version headers");
                if (provider == "openai")
                {
                    int invocations = 0;
                    var repeated = new ToolWireTransport(provider, alwaysCall: true);
                    using var repeatedClient = new SubscriptionClient(new AgentConfig { Provider = provider, Model = "fixture-model", Reasoning = "none" }, oauth, new HttpClient(repeated));
                    var repeatedTool = AIFunctionFactory.Create((string query) => { invocations++; return "本地规则结果:" + query; }, "search_game_rules", "仅查询本地规则资料。", Files.Json);
                    var repeatedAgent = new ChatClientAgent(repeatedClient, name: "ToolLimitFixture", tools: new AITool[] { repeatedTool });
                    var repeatedSession = await repeatedAgent.CreateSessionAsync();
                    var limitedReply = await repeatedAgent.RunAsync("查询规则。", repeatedSession);
                    Check(invocations == 4 && repeated.Bodies.Count == 5 && limitedReply.Text == "最终行动JSON", "four lookups are followed by a final model response with tools disabled");
                    using var finalBody = JsonDocument.Parse(repeated.Bodies[^1]);
                    Check(!finalBody.RootElement.TryGetProperty("tools", out var finalTools) || finalTools.GetArrayLength() == 0, "lookup budget disables tools instead of substituting a non-JSON decision");
                }
            }
        }
        finally
        {
            foreach (var (path, bytes) in prior)
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
        }
    }

    static bool HasToolResult(string provider, string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement messages = doc.RootElement.GetProperty(provider == "openai" ? "input" : "messages");
        if (provider == "openai")
            return messages.EnumerateArray().Any(x => x.TryGetProperty("type", out var t) && t.GetString() == "function_call_output" && x.GetProperty("call_id").GetString() == "call_fixture" && MatchesResult(x.GetProperty("output").GetString()));
        if (provider == "anthropic")
            return messages.EnumerateArray().SelectMany(x => x.GetProperty("content").EnumerateArray()).Any(x => x.GetProperty("type").GetString() == "tool_result" && x.GetProperty("tool_use_id").GetString() == "call_fixture" && x.GetProperty("content").GetString() == "本地规则结果:战争");
        return messages.EnumerateArray().Any(x => x.TryGetProperty("role", out var role) && role.GetString() == "tool" && x.GetProperty("tool_call_id").GetString() == "call_fixture" && x.GetProperty("content").GetString() == "本地规则结果:战争");
    }
    static bool MatchesResult(string? value)
    {
        if (value == "本地规则结果:战争") return true;
        if (value is null) return false;
        try { return JsonSerializer.Deserialize<string>(value) == "本地规则结果:战争"; }
        catch (JsonException) { return false; }
    }

    sealed class NoOAuthRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fixture unexpectedly attempted an OAuth network request");
    }

    sealed class ToolWireTransport(string provider, bool alwaysCall = false) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public bool SawAnthropicHeaders { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (provider == "anthropic")
                SawAnthropicHeaders = request.Headers.Contains("anthropic-version") && request.Headers.Contains("anthropic-beta");
            if (alwaysCall)
            {
                using var sent = JsonDocument.Parse(body);
                if (!sent.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"最终行动JSON\"}]}]}", Encoding.UTF8, "application/json") };
                string call = JsonSerializer.Serialize(new
                {
                    output = new[] { new { type = "function_call", call_id = "call_" + Bodies.Count, name = "search_game_rules", arguments = JsonSerializer.Serialize(new { query = "战争" }) } }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(call, Encoding.UTF8, "application/json")
                };
            }
            bool first = Bodies.Count == 1;
            string payload = provider switch
            {
                "openai" => first
                    ? "{\"output\":[{\"type\":\"function_call\",\"call_id\":\"call_fixture\",\"name\":\"search_game_rules\",\"arguments\":\"{\\\"query\\\":\\\"战争\\\"}\"}]}"
                    : "{\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"本地规则结果:战争\"}]}]}",
                "anthropic" => first
                    ? "{\"content\":[{\"type\":\"tool_use\",\"id\":\"call_fixture\",\"name\":\"search_game_rules\",\"input\":{\"query\":\"战争\"}}]}"
                    : "{\"content\":[{\"type\":\"text\",\"text\":\"本地规则结果:战争\"}]}",
                _ => first
                    ? "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call_fixture\",\"type\":\"function\",\"function\":{\"name\":\"search_game_rules\",\"arguments\":\"{\\\"query\\\":\\\"战争\\\"}\"}}]}}]}"
                    : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"本地规则结果:战争\"}}]}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }
}
