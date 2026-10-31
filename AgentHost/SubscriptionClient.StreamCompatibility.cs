using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Tohotopia;

public sealed partial class SubscriptionClient
{
    static void CompleteOpenAIReplay(IEnumerable<ChatMessage> messages)
    {
        const string key = "FSharp.ModelsDev.ProviderState";
        foreach (var message in messages.Where(m => m.Role == ChatRole.Assistant))
        {
            if (message.AdditionalProperties?.TryGetValue(key, out var value) != true) continue;
            string? json = value is string text ? text : value is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (json is null || JsonNode.Parse(json) is not JsonObject state || state["items"] is not JsonArray items) continue;
            // SDK streaming state may contain reasoning only. Its serializer
            // prefers that state over message contents, so replay missing calls
            // alongside the original opaque reasoning to retain call correlation.
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                if (items.OfType<JsonObject>().Any(i => i["type"]?.GetValue<string>() == "function_call" && i["call_id"]?.GetValue<string>() == call.CallId)) continue;
                items.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call.CallId, ["name"] = call.Name, ["arguments"] = JsonSerializer.Serialize(call.Arguments) });
            }
            message.AdditionalProperties![key] = state.ToJsonString();
        }
    }

    // These lifecycle/metadata frames carry no text or function arguments.
    // Keep all content, completion and failure frames for the SDK to validate.
    static async IAsyncEnumerable<string> CompatibleResponseEvents(IAsyncEnumerable<string> lines,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var frame = new List<string>();
        await foreach (var line in lines.WithCancellation(ct))
        {
            frame.Add(line);
            if (line.Length != 0) continue;
            if (!SkipMetadataFrame(frame)) foreach (var item in frame) yield return item;
            frame.Clear();
        }
        if (!SkipMetadataFrame(frame)) foreach (var item in frame) yield return item;
    }

    static bool SkipMetadataFrame(List<string> frame)
    {
        string data = string.Join("\n", frame.Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart()));
        if (!data.StartsWith('{')) return false;
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("type", out var type) && type.GetString() is
                "response.metadata" or "response.reasoning_summary_part.added" or
                "response.reasoning_summary_part.done" or "response.reasoning_summary_text.done";
        }
        catch (JsonException) { return false; }
    }
}
