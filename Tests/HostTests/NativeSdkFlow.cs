using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tohotopia;
using TohotopiaShared;

// Exercise the SDK inside the real host, including its NativeAOT executable.
// Everything remains on loopback, with an isolated catalog and dummy API key.
static class NativeSdkFlow
{
    internal static async Task Run(HttpClient host, string root)
    {
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        int port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        string url = "http://127.0.0.1:" + port + "/";
        listener.Prefixes.Add(url);
        listener.Start();
        const string provider = "fixture-aot-sdk", model = "fixture-model", requestId = "sdk-aot-decision";
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "models-dev.json")))!.AsObject();
        catalog[provider] = new JsonObject
        {
            ["name"] = "NativeAOT fixture", ["npm"] = "@ai-sdk/openai-compatible", ["api"] = url + "v1",
            ["env"] = new JsonArray("FIXTURE_API_KEY"), ["models"] = new JsonObject()
        };
        File.WriteAllText(Path.Combine(root, "models-dev.json"), catalog.ToJsonString());
        RoomFiles.Write(root, "game-rules.json", new GameRulesExport("ZH", [new("war", "technology", "战争", "本地规则测试")]));
        using var key = await host.PostAsJsonAsync("/auth/" + provider + "/api-key", new ApiKeyRequest("dummy-key", url + "v1"), HostJson.Type<ApiKeyRequest>());
        key.EnsureSuccessStatusCode();
        using var config = await host.PutAsJsonAsync("/settings", new AgentConfig { Provider = provider, Model = model, TimeoutSeconds = 15 }, HostJson.Type<AgentConfig>());
        config.EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var bodies = new List<JsonElement>();
        Task server = Serve();
        try
        {
            using var observation = JsonDocument.Parse("""{"selfId":7,"grids":[]}""");
            var request = new DecisionRequest(requestId, 1, 7, 1, 0, observation.RootElement,
                [new("move", "UnitMove", 1, 0, 2, 0, 0, 0, "移动"), new("end_turn", "EndTurn", 0, 0, 0, 0, 0, 0, "结束回合")], "规则", SessionId: "sdk-aot");
            using var response = await host.PostAsJsonAsync("/decision", request, HostJson.Type<DecisionRequest>(), timeout.Token);
            string wire = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) throw new Exception("SDK decision failed: " + wire);
            var decision = JsonSerializer.Deserialize(wire, HostJson.Type<Decision>())!;
            await server;
            if (decision.RequestId != requestId || decision.Steps.Single().CandidateId != "end_turn" || bodies.Count != 2)
                throw new Exception("SDK did not complete the tool/decision round trip");
            var toolResult = bodies[1].GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
            string result = toolResult.GetProperty("content").GetString()!;
            if (result.StartsWith('"')) result = JsonSerializer.Deserialize(result, HostJson.Type<string>(HostJson.Plain))!;
            if (toolResult.GetProperty("tool_call_id").GetString() != "native-call" || !result.Contains("本地规则测试"))
                throw new Exception("SDK lost the correlated local tool result: " + toolResult.GetRawText());
            Console.WriteLine("PASS real host SDK streams a tool call, executes local rules and returns the final decision without JSON reflection");
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await server; }
            catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }

        async Task Serve()
        {
            for (int call = 0; call < 2; call++)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var doc = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: timeout.Token);
                bodies.Add(doc.RootElement.Clone());
                if (context.Request.Headers["Authorization"] != "Bearer dummy-key" || !doc.RootElement.GetProperty("stream").GetBoolean())
                    throw new Exception("SDK fixture received unexpected authentication or non-streaming request");
                var delta = call == 0
                    ? new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject
                        { ["index"] = 0, ["id"] = "native-call", ["type"] = "function", ["function"] = new JsonObject
                            { ["name"] = "search_game_rules", ["arguments"] = "{\"query\":\"战争\"}" } }) }
                    : new JsonObject { ["content"] = JsonSerializer.Serialize(new Decision(requestId, Actions: [new("end_turn")]), HostJson.Type<Decision>()) };
                string payload = Frame(delta, null) + Frame(new JsonObject(), call == 0 ? "tool_calls" : "stop") + "data: [DONE]\n\n";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, timeout.Token);
                context.Response.Close();
            }
        }
        static string Frame(JsonObject delta, string? finish) => "data: " + new JsonObject
        {
            ["id"] = "fixture", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
        }.ToJsonString() + "\n\n";
    }
}
