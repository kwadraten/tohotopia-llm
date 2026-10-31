using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Tohotopia;

internal static class StreamingChecks
{
    internal static async Task Run(string provider, OAuth oauth)
    {
        var wire = new StreamingWire(provider);
        var events = new List<(string Stage, string Text)>();
        bool live = false;
        using var client = new SubscriptionClient(new AgentConfig { Provider = provider, Model = provider == "anthropic" ? "claude-sonnet-4-6" : "fixture-model", Reasoning = "high" }, oauth,
            new HttpClient(wire), progressWriter: (stage, text) =>
            {
                events.Add((stage, text));
                live |= wire.Input.BytesRead < wire.Input.Size;
                if (stage == "reasoning_delta") wire.Input.Release.TrySetResult();
            });
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "state") });
        if (response.Text != "answer" || !live || string.Concat(events.Where(e => e.Stage == "reasoning_delta").Select(e => e.Text)) != "plan" ||
            string.Concat(events.Where(e => e.Stage == "model_output_delta").Select(e => e.Text)) != "answer")
            throw new Exception(provider + " did not stream reasoning and output before completion: " + string.Join(" | ", events.Select(e => e.Stage + ":" + e.Text)) + " reply=" + response.Text + " live=" + live);
        Console.WriteLine("PASS " + provider + " logs reasoning and output while network response is still in progress");

        wire.Payload = provider switch
        {
            "openai" => Sse(new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc1", call_id = "call1", name = "lookup", arguments = "" } }) +
                Sse(new { type = "response.function_call_arguments.delta", item_id = "fc1", output_index = 0, delta = "{\"query\":" }) +
                Sse(new { type = "response.function_call_arguments.delta", item_id = "fc1", output_index = 0, delta = "\"war\"}" }) +
                Sse(new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc1", call_id = "call1", name = "lookup", arguments = "{\"query\":\"war\"}" } }) +
                Sse(new { type = "response.completed", response = new { id = "r2", status = "completed", output = new[] { new { type = "function_call", id = "fc1", call_id = "call1", name = "lookup", arguments = "{\"query\":\"war\"}" } } } }),
            "anthropic" => Sse(new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "call1", name = "lookup", input = new { } } }) +
                Sse(new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "{\"query\":" } }) +
                Sse(new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "\"war\"}" } }) +
                Sse(new { type = "message_delta", delta = new { stop_reason = "tool_use" } }) + Sse(new { type = "message_stop" }),
            _ => Sse(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "call1", function = new { name = "lookup", arguments = "{\"query\":" } } } } } } }) +
                Sse(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"war\"}" } } } }, finish_reason = "tool_calls" } } }) + "data: [DONE]\n\n"
        };
        var toolResponse = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "state") }, new ChatOptions
        { Tools = new[] { AIFunctionFactory.Create((string query) => query, "lookup") } });
        var call = toolResponse.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        if (call.Name != "lookup" || call.CallId != "call1" || call.Arguments?["query"]?.ToString() != "war")
            throw new Exception(provider + " lost streamed tool arguments");
        Console.WriteLine("PASS " + provider + " preserves fragmented tool calls in streaming responses");

        wire.Payload = provider switch
        {
            "openai" => Sse(new { type = "response.reasoning_summary_text.delta", delta = "partial" }),
            "anthropic" => Sse(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }),
            _ => Sse(new { choices = new[] { new { delta = new { content = "partial" } } } })
        };
        try { await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "state") }); }
        catch (Exception e) when (e is InvalidOperationException or FSharp.ModelsDev.AiException)
        { Console.WriteLine("PASS " + provider + " rejects truncated streaming decisions"); return; }
        throw new Exception(provider + " accepted truncated stream");
    }

    static string Sse(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    sealed class SmallReads(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        internal int BytesRead;
        internal int Size = Encoding.UTF8.GetByteCount(text);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly int boundary = text.Contains("plan") ? Encoding.UTF8.GetByteCount(text[..(text.IndexOf("\n\n", text.IndexOf("plan", StringComparison.Ordinal), StringComparison.Ordinal) + 2)]) : int.MaxValue;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (BytesRead >= boundary) await Release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            int limit = BytesRead < boundary ? Math.Min(19, boundary - BytesRead) : 19;
            int count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, limit)], ct);
            BytesRead += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
    sealed class StreamingWire : HttpMessageHandler
    {
        internal string Payload;
        internal SmallReads Input = new("");
        internal StreamingWire(string provider)
        {
            Payload = provider switch
            {
                "openai" => Sse(new { type = "response.created", response = new { id = "r1", status = "in_progress", output = Array.Empty<object>() } }) +
                    Sse(new { type = "response.metadata", metadata = new { } }) +
                    Sse(new { type = "response.reasoning_summary_part.added", item_id = "thought", output_index = 0, summary_index = 0, part = new { type = "summary_text", text = "" } }) +
                    Sse(new { type = "response.reasoning_summary_text.delta", delta = "plan", item_id = "thought", output_index = 0, summary_index = 0 }) +
                    Sse(new { type = "response.reasoning_summary_text.done", text = "plan", item_id = "thought", output_index = 0, summary_index = 0 }) +
                    Sse(new { type = "response.reasoning_summary_part.done", item_id = "thought", output_index = 0, summary_index = 0, part = new { type = "summary_text", text = "plan" } }) +
                    Sse(new { type = "response.output_text.delta", delta = "ans", item_id = "m1", output_index = 1, content_index = 0 }) +
                    Sse(new { type = "response.output_text.delta", delta = "wer", item_id = "m1", output_index = 1, content_index = 0 }) +
                    Sse(new { type = "response.completed", response = new { id = "r1", status = "completed", output = new[] { new { type = "message", id = "m1", role = "assistant", content = new[] { new { type = "output_text", text = "answer" } } } } } }),
                "anthropic" => Sse(new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } }) +
                    Sse(new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "plan" } }) +
                    Sse(new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } }) +
                    Sse(new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "ans" } }) +
                    Sse(new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "wer" } }) +
                    Sse(new { type = "message_delta", delta = new { stop_reason = "end_turn" } }) + Sse(new { type = "message_stop" }),
                _ => Sse(new { choices = new[] { new { delta = new { reasoning_content = "plan" } } } }) +
                    Sse(new { choices = new[] { new { delta = new { content = "ans" } } } }) +
                    Sse(new { choices = new[] { new { delta = new { content = "wer" }, finish_reason = "stop" } } }) + "data: [DONE]\n\n"
            };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (!body.RootElement.GetProperty("stream").GetBoolean()) throw new Exception("Expected streaming request");
            foreach (string key in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens" })
                if (body.RootElement.TryGetProperty(key, out var cap) &&
                    !(request.RequestUri!.Host == "api.anthropic.com" && key == "max_tokens" && cap.GetInt64() > 8192))
                    throw new Exception("Thinking request retained a local output cap: " + key);
            if (request.RequestUri!.Host == "api.kimi.com" &&
                (body.RootElement.TryGetProperty("max_tokens", out _) || body.RootElement.TryGetProperty("max_completion_tokens", out _) ||
                 body.RootElement.GetProperty("thinking").GetProperty("type").GetString() != "enabled"))
                throw new Exception("Kimi thinking must use the service output allowance instead of the 4096 action-text cap");
            Input = new SmallReads(Payload);
            var content = new StreamContent(Input);
            content.Headers.ContentType = new("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}

