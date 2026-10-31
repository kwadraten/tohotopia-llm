using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Tohotopia;

static class ProviderConnectionChecks
{
    public static async Task Run()
    {
        string previousRoot = Files.Root;
        Files.Root = Path.Combine(Path.GetTempPath(), "tohotopia-provider-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Files.Root);
        try
        {
            File.WriteAllText(Path.Combine(Files.Root, "models-dev.json"), """
                {
                  "deepseek":{"name":"DeepSeek","npm":"@ai-sdk/openai-compatible","api":"https://api.deepseek.com","env":["DEEPSEEK_API_KEY"],"models":{}},
                  "google":{"name":"Google","npm":"@ai-sdk/google","env":["GOOGLE_API_KEY","GEMINI_API_KEY"],"models":{}},
                  "groq":{"name":"Groq","npm":"@ai-sdk/groq","env":["GROQ_API_KEY"],"models":{}},
                  "mistral":{"name":"Mistral","npm":"@ai-sdk/mistral","env":["MISTRAL_API_KEY"],"models":{}},
                  "openrouter":{"name":"OpenRouter","npm":"@openrouter/ai-sdk-provider","api":"https://openrouter.ai/api/v1","env":["OPENROUTER_API_KEY"],"models":{}},
                  "fixture-key-gateway":{"name":"New catalog provider","npm":"@ai-sdk/openai-compatible","api":"https://gateway.example/v1","env":["NEW_SERVICE_API_KEY"],"models":{}},
                  "fixture-template":{"name":"Resource provider","npm":"@ai-sdk/openai-compatible","api":"https://api.example/${ACCOUNT}/v1","env":["SERVICE_API_KEY","ACCOUNT"],"models":{}},
                  "fixture-aws":{"name":"IAM service","npm":"@ai-sdk/amazon-bedrock","env":["AWS_ACCESS_KEY_ID","AWS_SECRET_ACCESS_KEY"],"models":{}},
                  "fixture-native-key":{"name":"Specialized protocol","npm":"@fixture/native","env":["NATIVE_API_KEY"],"models":{}}
                }
                """);
            var oauth = new OAuth();
            var providers = await new ModelCatalog(oauth).Providers(CancellationToken.None);
            foreach (string id in new[] { "deepseek", "google", "groq", "mistral", "openrouter", "fixture-key-gateway" })
            {
                var provider = providers.Single(p => p.Id == id);
                Require(provider.ConnectionSupported && provider.AuthMethods.Single().Type == "api", id + " exposes a working API-key connection");
                var endpoint = provider.AuthMethods.Single().Prompts!.Single(p => p.Key == "baseUrl");
                Require(endpoint.Required && endpoint.DefaultValue!.StartsWith("https://"), id + " supplies the provider API endpoint to its form");
            }
            Require(!providers.Single(p => p.Id == "fixture-aws").ConnectionSupported, "IAM provider is not mislabeled as subscription or direct API-key authentication");
            var specialized = providers.Single(p => p.Id == "fixture-native-key");
            Require(specialized.AuthMethods.Single().Type == "api" && !specialized.ConnectionSupported, "specialized key providers retain a fill-information entry without advertising unsupported inference");
            oauth.ConfigureApiKey("fixture-native-key", new("native-fixture-key"));
            Require(new OAuth().Status("fixture-native-key") is { Configured: true, Connected: false }, "specialized provider keys persist with an explicit inference limitation");
            Require(providers.Single(p => p.Id == "fixture-template").AuthMethods.Single().Prompts!.Single(p => p.Key == "baseUrl").DefaultValue == "", "resource URL templates require a concrete endpoint");
            bool templateRejected = false;
            try { oauth.ConfigureApiKey("fixture-template", new("fixture-key")); }
            catch (ArgumentException) { templateRejected = true; }
            Require(templateRejected, "unresolved endpoint templates cannot receive credentials");
            foreach (string id in new[] { "openai", "anthropic", "xai", "moonshotai" })
                Require(oauth.ConfigureApiKey(id, new("legacy-fixture-key")) is { Configured: true, Connected: true, AuthType: "api_key" }, id + " retains its existing key form without requiring a new endpoint field");

            foreach (var (id, model, expectedUrl, nativeGoogle) in new[]
            {
                ("deepseek", "deepseek-chat", "https://api.deepseek.com/chat/completions", false),
                ("google", "gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", true),
                ("fixture-key-gateway", "new-catalog-model", "https://proxy.example/custom/v1/chat/completions", false)
            })
            {
                string key = id + "-fixture-secret";
                string url = id == "fixture-key-gateway" ? "https://proxy.example/custom/v1" : "";
                oauth.ConfigureApiKey(id, new(key, url));
                var restored = new OAuth();
                Require(restored.Accounts().Any(a => a.Provider == id && a.Configured && a.AuthType == "api_key" && a.Connected), id + " restores saved credentials in provider management");
                Require(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Files.Root, id + ".token"))).Contains(key), id + " stores no plaintext key");
                using var handler = new ProviderTransport(expectedUrl, key, nativeGoogle);
                using var client = new SubscriptionClient(new() { Provider = id, Model = model }, restored, new HttpClient(handler));
                var reply = await client.GetResponseAsync([new(ChatRole.User, "fixture-state")], new ChatOptions
                {
                    MaxOutputTokens = 7,
                    Instructions = "fixture-rules",
                    Tools = [AIFunctionFactory.Create((string query) => query, "lookup_rule", "Read a rule")]
                });
                Require(reply.Text == "provider-ok" && handler.Calls == 1, id + " reaches its native API using the configured key and returns a model reply");
            }
        }
        finally { Files.Root = previousRoot; }
    }

    static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    sealed class ProviderTransport(string expectedUrl, string key, bool google) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Require(request.RequestUri!.AbsoluteUri == expectedUrl, "provider endpoint and protocol are correct");
            if (google)
                Require(request.Headers.TryGetValues("x-goog-api-key", out var keys) && keys.Single() == key && request.Headers.Authorization is null, "Gemini uses x-goog-api-key rather than Bearer authentication");
            else
                Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == key, "compatible provider uses its own Bearer API key");
            string body = await request.Content!.ReadAsStringAsync(ct);
            using var payload = JsonDocument.Parse(body);
            foreach (string cap in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens" })
                Require(!payload.RootElement.TryGetProperty(cap, out _), "SDK thinking request omits " + cap);
            if (payload.RootElement.TryGetProperty("generationConfig", out var generation))
                Require(!generation.TryGetProperty("maxOutputTokens", out _), "Gemini thinking request omits maxOutputTokens");
            Require(body.Contains("fixture-rules") && body.Contains("fixture-state") && body.Contains("lookup_rule") && !body.Contains(key), "instructions, observations and tools reach the provider without embedding credentials in prompts");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(google
                    ? """{"candidates":[{"content":{"role":"model","parts":[{"text":"provider-ok"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1}}"""
                    : """{"id":"fixture-reply","model":"fixture-model","choices":[{"message":{"role":"assistant","content":"provider-ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""")
            };
        }
    }
}
