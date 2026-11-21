using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Tohotopia;
using Xunit;

public class ProviderConnectionTests
{
    [Fact]
    public async Task Provider_auth_methods_and_native_transport()
    {
        string previousRoot = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-provider-test-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "models-dev.json"), """
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
                Assert.True(provider.ConnectionSupported && provider.AuthMethods.Single().Type == "api", id + " exposes a working API-key connection");
                var endpoint = provider.AuthMethods.Single().Prompts!.Single(p => p.Key == "baseUrl");
                Assert.True(endpoint.Required && endpoint.DefaultValue!.StartsWith("https://"), id + " supplies the provider API endpoint to its form");
            }
            Assert.True(!providers.Single(p => p.Id == "fixture-aws").ConnectionSupported, "IAM provider is not mislabeled as subscription or direct API-key authentication");
            var specialized = providers.Single(p => p.Id == "fixture-native-key");
            Assert.True(specialized.AuthMethods.Single().Type == "api" && !specialized.ConnectionSupported, "specialized key providers retain a fill-information entry without advertising unsupported inference");
            oauth.ConfigureApiKey("fixture-native-key", new("native-fixture-key"));
            Assert.True(new OAuth().Status("fixture-native-key") is { Configured: true, Connected: false }, "specialized provider keys persist with an explicit inference limitation");
            Assert.True(providers.Single(p => p.Id == "fixture-template").AuthMethods.Single().Prompts!.Single(p => p.Key == "baseUrl").DefaultValue == "", "resource URL templates require a concrete endpoint");
            Assert.Throws<ArgumentException>(() => oauth.ConfigureApiKey("fixture-template", new("fixture-key")));
            foreach (string id in new[] { "openai", "anthropic", "xai", "moonshotai" })
                Assert.True(oauth.ConfigureApiKey(id, new("legacy-fixture-key")) is { Configured: true, Connected: true, AuthType: "api_key" }, id + " retains its existing key form without requiring a new endpoint field");

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
                Assert.True(restored.Accounts().Any(a => a.Provider == id && a.Configured && a.AuthType == "api_key" && a.Connected), id + " restores saved credentials in provider management");
                Assert.True(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(CredentialPaths.File(root, id + ".token"))).Contains(key), id + " stores no plaintext key");
                using var handler = new ProviderTransport(expectedUrl, key, nativeGoogle);
                using var client = new SubscriptionClient(new() { Provider = id, Model = model }, restored, new HttpClient(handler));
                var reply = await client.GetResponseAsync([new(ChatRole.User, "fixture-state")], new ChatOptions
                {
                    MaxOutputTokens = 7,
                    Instructions = "fixture-rules",
                    Tools = [AIFunctionFactory.Create((string query) => query, "lookup_rule", "Read a rule")]
                });
                Assert.True(reply.Text == "provider-ok" && handler.Calls == 1, id + " reaches its native API using the configured key and returns a model reply");
            }
        }
        finally { Files.Root = previousRoot; Directory.Delete(root, true); }
    }

    sealed class ProviderTransport(string expectedUrl, string key, bool google) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.True(request.RequestUri!.AbsoluteUri == expectedUrl, "provider endpoint and protocol are correct");
            if (google)
                Assert.True(request.Headers.TryGetValues("x-goog-api-key", out var keys) && keys.Single() == key && request.Headers.Authorization is null, "Gemini uses x-goog-api-key rather than Bearer authentication");
            else
                Assert.True(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == key, "compatible provider uses its own Bearer API key");
            string body = await request.Content!.ReadAsStringAsync(ct);
            using var payload = JsonDocument.Parse(body);
            foreach (string cap in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens" })
                Assert.True(!payload.RootElement.TryGetProperty(cap, out _), "SDK thinking request omits " + cap);
            if (payload.RootElement.TryGetProperty("generationConfig", out var generation))
                Assert.True(!generation.TryGetProperty("maxOutputTokens", out _), "Gemini thinking request omits maxOutputTokens");
            Assert.True(body.Contains("fixture-rules") && body.Contains("fixture-state") && body.Contains("lookup_rule") && !body.Contains(key), "instructions, observations and tools reach the provider without embedding credentials in prompts");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(google
                    ? """{"candidates":[{"content":{"role":"model","parts":[{"text":"provider-ok"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1}}"""
                    : """{"id":"fixture-reply","model":"fixture-model","choices":[{"message":{"role":"assistant","content":"provider-ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""")
            };
        }
    }
}
