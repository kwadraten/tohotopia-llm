using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Tohotopia;
using TohotopiaShared;
using Xunit;

public class SourceJsonTests
{
    [Fact]
    public async Task Generated_contracts_without_reflection()
    {
        // TOHOTOPIA_NATIVE_HOST may point at a published AgentHost.exe; without
        // it the managed assembly is hosted through dotnet exec instead.
        string? nativeHost = Environment.GetEnvironmentVariable("TOHOTOPIA_NATIVE_HOST");
        Assert.True(!JsonSerializer.IsReflectionEnabledByDefault, "JSON tests run with reflection serialization disabled");
        string previousRoot = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-source-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Files.Root = root;
        try
        {
            Files.Save(new AgentConfig { ExtraRules = "中文规则", TimeoutSeconds = 45 });
            Assert.True(Files.Config.ExtraRules == "中文规则" && Files.Config.TimeoutSeconds == 45, "source-generated configuration survives restart");
            var legacyConfig = JsonSerializer.Deserialize("""{"PROVIDER":"grok","timeoutSeconds":"60"}""", HostJson.Type<AgentConfig>());
            Assert.True(legacyConfig is { Provider: "grok", TimeoutSeconds: 60 }, "web contract retains case-insensitive names and quoted numeric input");
            RoomFiles.Write(root, "room.json", new RoomConfig { AIs = [new() { Model = "fixture" }] });
            Assert.True(RoomFiles.Read<RoomConfig>(root, "room.json")?.AIs[0].Model == "fixture", "shared room contracts use generated metadata");
            AgentEvents.Write(root, new(DateTimeOffset.UtcNow, "host", "check", "中文\n多行"));
            using (var log = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(root, "logs"))[0])))
                Assert.True(log.RootElement.GetProperty("message").GetString() == "中文\n多行", "JSONL generated contract preserves multiline text");

            // Token files historically use PascalCase, while API and room files use camelCase.
            var token = JsonSerializer.Deserialize("""{"Access":"fixture","Refresh":"refresh","Expires":"2030-01-01T00:00:00Z"}""", HostJson.Type<Token>(HostJson.Plain))!;
            using (var saved = JsonDocument.Parse(JsonSerializer.Serialize(token, HostJson.Type<Token>(HostJson.Plain))))
                Assert.True(token.Access == "fixture" && token.AuthType == "subscription" && saved.RootElement.TryGetProperty("Access", out _), "token contract reads legacy files and preserves PascalCase and optional defaults");

            var schemaTool = AIFunctionFactory.Create((string query, int? level = null) => query, "lookup", "fixture", Files.Json);
            var messages = new ChatMessage[]
            {
                new(ChatRole.User, "state"),
                new(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>
                {
                    ["query"] = "战争", ["level"] = 2, ["nested"] = new Dictionary<string, object?> { ["items"] = new object?[] { true, null, 3L } }
                })]),
                new(ChatRole.Tool, [new FunctionResultContent("call-1", "结果")])
            };
            var body = SubscriptionClient.OpenAIBody(new AgentConfig { Reasoning = "high" }, messages, [schemaTool]);
            var arguments = JsonNode.Parse(body["input"]![1]!["arguments"]!.GetValue<string>())!;
            Assert.True(arguments["query"]!.GetValue<string>() == "战争" && arguments["nested"]!["items"]![0]!.GetValue<bool>()
                && body["tools"]![0]!["parameters"]!["type"]!.GetValue<string>() == "object"
                && body["input"]![2]!["output"]!.GetValue<string>() == "结果", "generated tool contracts preserve schemas, nested arguments and correlated results");
            Assert.Throws<NotSupportedException>(() => HostJson.SerializeValue(new { UnsupportedContract = true }));

            await ModelReplies();
            await Streaming(root);
            await HttpEndpoints(root, nativeHost);
        }
        finally
        {
            Files.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    static async Task ModelReplies()
    {
        using var observationDoc = JsonDocument.Parse("""{"selfId":7,"grids":[]}""");
        var observation = observationDoc.RootElement.Clone();
        var request = new DecisionRequest("strict-decision", 1, 7, 1, 0, observation,
            [new("move", "UnitMove", 1, 0, 2, 0, 0, 0, "移动"), new("end_turn", "EndTurn", 0, 0, 0, 0, 0, 0, "结束回合")], "规则", AiSlot: 1, SessionId: "strict");
        string reply = JsonSerializer.Serialize(new Decision(request.RequestId, Actions: [new("end_turn")]), HostJson.Type<Decision>()).TrimEnd();
        // Exercise the real model-reply repair path, including cloned options.
        var director = new Director(new OAuth(), (_, _) => new ReplyClient("```json\n" + reply[..^1] + ",}\n```"));
        var decision = await director.Decide(request, CancellationToken.None);
        Assert.True(decision.Steps.Single().CandidateId == "end_turn", "decision contracts and trailing-comma reply repair work with reflection disabled");
        var communication = new CommunicationRequest("strict-communication", "strict", 1, 7, 1, 1, observation, ["team"], "规则");
        string communicationReply = JsonSerializer.Serialize(new AmbientReply(communication.RequestId, "team", "需要支援"), HostJson.Type<AmbientReply>());
        var communicator = new Director(new OAuth(), (_, _) => new ReplyClient(communicationReply));
        var result = await communicator.Communicate(communication, CancellationToken.None);
        Assert.True(result.Channel == "team" && result.Message == "需要支援", "communication contracts and agent tools work with reflection disabled");
    }

    static async Task Streaming(string root)
    {
        File.WriteAllText(Path.Combine(root, "models-dev.json"), """{"anthropic":{"models":{"fixture":{"limit":{"output":64000}}}}}""");
        using var oauthHttp = new HttpClient(new FixtureHandler(""));
        var oauth = new OAuth(oauthHttp);
        foreach (string provider in new[] { "grok", "anthropic", "openai" })
        {
            oauth.ConfigureApiKey(provider, new ApiKeyRequest("fixture-key"));
            string events = provider == "grok"
                ? """
                  data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"lookup","arguments":"{\"query\":\"战争\"}"}}]},"finish_reason":null}]}

                  data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

                  data: [DONE]


                  """
                : """
                  data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call-1","name":"lookup","input":{}}}

                  data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\":\"战争\"}"}}

                  data: {"type":"message_stop"}


                  """;
            if (provider == "openai") events = """
                data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"lookup","arguments":""}}

                data: {"type":"response.function_call_arguments.delta","output_index":0,"item_id":"fc-1","delta":"{\"query\":\"战争\"}"}

                data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"lookup","arguments":"{\"query\":\"战争\"}"}}

                data: {"type":"response.completed","response":{"id":"fixture","status":"completed","output":[{"type":"function_call","id":"fc-1","call_id":"call-1","name":"lookup","arguments":"{\"query\":\"战争\"}"}]}}


                """;
            using var client = new SubscriptionClient(new AgentConfig { Provider = provider, Model = "fixture" }, oauth,
                new HttpClient(new FixtureHandler(events)), progressWriter: (_, _) => { });
            var tool = AIFunctionFactory.Create((string query) => query, "lookup", "fixture", Files.Json);
            var response = await client.GetResponseAsync([new(ChatRole.User, "fixture")], new ChatOptions { Tools = [tool] });
            var call = response.Messages.Single().Contents.OfType<FunctionCallContent>().Single();
            Assert.True(call.CallId == "call-1" && call.Arguments!["query"]?.ToString() == "战争",
                provider + " streamed tool calls use generated contracts with reflection disabled");
        }
        const string responseEvents = """
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","call_id":"response-call","name":"lookup","arguments":"{\"query\":\"战争\"}"}}

            data: {"type":"response.completed","response":{"status":"completed","output":[]}}


            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(responseEvents));
        var responseMessage = await SubscriptionClient.ReadResponseMessage(stream, CancellationToken.None);
        Assert.True(responseMessage.Contents.OfType<FunctionCallContent>().Single().CallId == "response-call", "Responses item.done fallback uses generated array and argument contracts");
    }

    static async Task HttpEndpoints(string root, string? nativeHost)
    {
        // This launches the real entry point, so minimal-API binding and response
        // serialization are tested rather than a second copy of the route setup.
        var start = new ProcessStartInfo(nativeHost ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string hostRoot = Path.Combine(root, "http");
        Directory.CreateDirectory(hostRoot);
        if (nativeHost is null)
        {
            string assembly = typeof(AgentConfig).Assembly.Location;
            var runtimeConfig = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(assembly, ".runtimeconfig.json")))!;
            runtimeConfig["runtimeOptions"]!["configProperties"]!["System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault"] = false;
            string configPath = Path.Combine(hostRoot, "strict.runtimeconfig.json");
            File.WriteAllText(configPath, runtimeConfig.ToJsonString());
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(configPath);
            start.ArgumentList.Add(assembly);
        }
        File.WriteAllText(Path.Combine(hostRoot, "bridge.token"), "fixture-token");
        File.WriteAllText(Path.Combine(hostRoot, "models-dev.json"), """{"xai":{"name":"xAI","models":{"fixture":{"name":"Fixture","reasoning":false}}}}""");
        start.Environment["TOHOTOPIA_LLM_DATA"] = hostRoot;
        start.Environment.Remove("TOHOTOPIA_LLM_OWNER_PID");
        start.Environment.Remove("TOHOTOPIA_LLM_OWNER_START");
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        start.Environment["TOHOTOPIA_LLM_PORT"] = port.ToString();
        using var host = Process.Start(start)!;
        var stdout = host.StandardOutput.ReadToEndAsync();
        var stderr = host.StandardError.ReadToEndAsync();
        bool testsPassed = false;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-token");
            bool ready = false;
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 60 && !host.HasExited && !startup.IsCancellationRequested; i++)
            {
                try { using var health = await http.GetAsync("/health", startup.Token); ready = health.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) { break; }
                if (ready) break;
                await Task.Delay(100);
            }
            Assert.True(ready, "real host starts with generated HTTP contracts");
            var healthBody = await http.GetFromJsonAsync("/health", HostJson.Type<HealthResponse>());
            Assert.True(healthBody is { Ready: true, Framework: "Microsoft Agent Framework" }, "health response uses named generated contract");
            using var save = await http.PutAsJsonAsync("/settings", new AgentConfig { ExtraRules = "HTTP 中文规则" }, HostJson.Type<AgentConfig>());
            var settings = await http.GetFromJsonAsync("/settings", HostJson.Type<AgentConfig>());
            Assert.True(save.IsSuccessStatusCode && settings?.ExtraRules == "HTTP 中文规则", "real settings endpoint binds and writes source-generated contracts");
            using var invalid = await http.PutAsJsonAsync("/settings", new AgentConfig { TimeoutSeconds = 1 }, HostJson.Type<AgentConfig>());
            Assert.True(invalid.StatusCode == HttpStatusCode.BadRequest, "invalid settings retain HTTP 400 validation");
            var accounts = await http.GetFromJsonAsync("/auth/accounts", HostJson.Type<AccountStatus[]>());
            var providers = await http.GetFromJsonAsync("/catalog/providers", HostJson.Type<List<CatalogProvider>>());
            var models = await http.GetFromJsonAsync("/catalog/grok/models", HostJson.Type<List<CatalogModel>>());
            Assert.True(accounts is { Length: > 0 } && providers?.Single().Id == "xai" && models?.Single().Id == "fixture", "account and catalog HTTP collections serialize without reflection");
            using var failure = await http.PostAsJsonAsync("/auth/missing/api-key", new ApiKeyRequest("fixture"), HostJson.Type<ApiKeyRequest>());
            using var problem = JsonDocument.Parse(await failure.Content.ReadAsStringAsync());
            Assert.True(failure.StatusCode == HttpStatusCode.UnprocessableEntity && problem.RootElement.GetProperty("status").GetInt32() == 422, "HTTP errors retain generated ProblemDetails responses");
            await CredentialEndpointFlow.Run(http, hostRoot);
            await NativeSdkFlow.Run(http, hostRoot);
            testsPassed = true;
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
            string output = await stdout + await stderr;
            if (!testsPassed) throw new Exception("HTTP endpoint tests failed; host output:" + Environment.NewLine + output);
        }
    }

    sealed class FixtureHandler(string events) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
            });
    }

    sealed class ReplyClient(string reply) : IChatClient
    {
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new(ChatRole.Assistant, reply);
        }
    }
}
