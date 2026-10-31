using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Tohotopia;

public sealed partial class SubscriptionClient
{
    static bool IsEventStream(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType == "text/event-stream";

    async IAsyncEnumerable<ChatResponseUpdate> ObserveUpdates(IAsyncEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in updates.WithCancellation(ct))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                    progressWriter?.Invoke("model_output_delta", text.Text);
                else if (config.Provider != "openai" && content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    progressWriter?.Invoke("reasoning_delta", reasoning.Text);
            }
            yield return update;
        }
    }

    // Observe only public summaries; encrypted reasoning/signatures are never logged.
    static void ObserveResponseEvent(string line, Action<string, string>? progress)
    {
        if (progress is null) return;
        string json = line.StartsWith("data:") ? line[5..].TrimStart() : line;
        if (!json.StartsWith('{')) return;
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        if (e.TryGetProperty("type", out var type) && type.GetString() is "response.reasoning_summary_text.delta" &&
            e.TryGetProperty("delta", out var delta) && delta.GetString() is { Length: > 0 } text)
            progress("reasoning_delta", text);
    }

    static async IAsyncEnumerable<JsonElement> ReadEvents(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                string json = data.ToString();
                data.Clear();
                if (json == "[DONE]") { yield return JsonSerializer.SerializeToElement(new { type = "done" }); yield break; }
                using var doc = JsonDocument.Parse(json);
                yield return doc.RootElement.Clone();
            }
            else if (line.StartsWith("data:"))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
        if (data.Length > 0)
        {
            if (data.ToString() == "[DONE]") yield return JsonSerializer.SerializeToElement(new { type = "done" });
            else { using var doc = JsonDocument.Parse(data.ToString()); yield return doc.RootElement.Clone(); }
        }
    }

    async Task<ChatMessage> ReadChatStream(Stream stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        bool completed = false;
        await foreach (var e in ReadEvents(stream, ct))
        {
            if (e.TryGetProperty("error", out _)) throw new InvalidOperationException("模型响应失败");
            if (e.TryGetProperty("type", out var type) && type.GetString() == "done") break;
            if (!e.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                if (finish.GetString() is not ("stop" or "tool_calls")) throw new InvalidOperationException("模型响应未完整结束：" + finish.GetString());
                completed = true;
            }
            if (!choice.TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            { text.Append(content.GetString()); progressWriter?.Invoke("model_output_delta", content.GetString()!); }
            if ((delta.TryGetProperty("reasoning_content", out var thought) || delta.TryGetProperty("reasoning", out thought)) && thought.ValueKind == JsonValueKind.String)
            { reasoning.Append(thought.GetString()); progressWriter?.Invoke("reasoning_delta", thought.GetString()!); }
            if (delta.TryGetProperty("tool_calls", out var toolCalls))
                foreach (var call in toolCalls.EnumerateArray())
                {
                    int index = call.GetProperty("index").GetInt32();
                    if (!calls.TryGetValue(index, out var value)) value = ("", "", new StringBuilder());
                    if (call.TryGetProperty("id", out var id)) value.Id += id.GetString();
                    if (call.TryGetProperty("function", out var function))
                    {
                        if (function.TryGetProperty("name", out var name)) value.Name += name.GetString();
                        if (function.TryGetProperty("arguments", out var args)) value.Arguments.Append(args.GetString());
                    }
                    calls[index] = value;
                }
        }
        if (!completed) throw new InvalidOperationException("模型响应流提前结束，未收到完成事件");
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (reasoning.Length > 0) message["reasoning_content"] = reasoning.ToString();
        message["tool_calls"] = new JsonArray(calls.Values.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.ToString() }
        }).ToArray());
        return ParseOpenAIChatMessage(JsonSerializer.SerializeToElement(message));
    }

    async Task<ChatMessage> ReadAnthropicStream(Stream stream, CancellationToken ct)
    {
        var blocks = new SortedDictionary<int, JsonObject>();
        var arguments = new Dictionary<int, StringBuilder>();
        bool completed = false;
        await foreach (var e in ReadEvents(stream, ct))
        {
            string type = e.GetProperty("type").GetString()!;
            if (type == "error") throw new InvalidOperationException("模型响应失败");
            if (type == "message_stop") { completed = true; break; }
            if (type == "message_delta" && e.GetProperty("delta").TryGetProperty("stop_reason", out var stop) && stop.GetString() is not ("end_turn" or "tool_use" or "stop_sequence"))
                throw new InvalidOperationException("模型响应未完整结束：" + stop.GetString());
            if (type == "content_block_start")
            {
                int index = e.GetProperty("index").GetInt32();
                blocks[index] = JsonNode.Parse(e.GetProperty("content_block").GetRawText())!.AsObject();
                arguments[index] = new StringBuilder();
            }
            if (type != "content_block_delta") continue;
            int i = e.GetProperty("index").GetInt32();
            var delta = e.GetProperty("delta");
            switch (delta.GetProperty("type").GetString())
            {
                case "text_delta": Append("text", "model_output_delta"); break;
                case "thinking_delta": Append("thinking", "reasoning_delta"); break;
                case "signature_delta": blocks[i]["signature"] = (blocks[i]["signature"]?.GetValue<string>() ?? "") + delta.GetProperty("signature").GetString(); break;
                case "input_json_delta": arguments[i].Append(delta.GetProperty("partial_json").GetString()); break;
            }
            void Append(string field, string stage)
            {
                string fragment = delta.GetProperty(field).GetString() ?? "";
                blocks[i][field] = (blocks[i][field]?.GetValue<string>() ?? "") + fragment;
                progressWriter?.Invoke(stage, fragment);
            }
        }
        if (!completed) throw new InvalidOperationException("模型响应流提前结束，未收到完成事件");
        foreach (var (i, json) in arguments)
            if (json.Length > 0) blocks[i]["input"] = JsonNode.Parse(json.ToString());
        return ParseAnthropicMessage(JsonSerializer.SerializeToElement(blocks.Values));
    }
}
