using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Tohotopia;

internal static class SubscriptionReplayChecks
{
    internal static void Run()
    {
        const string key = "FSharp.ModelsDev.ProviderState";
        var message = new ChatMessage(ChatRole.Assistant, new AIContent[] {
            new FunctionCallContent("call_one", "search_game_rules", new Dictionary<string, object?> { ["query"] = "挖掘" }),
            new FunctionCallContent("call_two", "get_game_rule", new Dictionary<string, object?> { ["id"] = "rule-1" })
        }) { AdditionalProperties = new() { [key] = """{"provider":"openai-responses","model":"fixture","items":[{"type":"reasoning","id":"reasoning-1","encrypted_content":"opaque-fixture"}]}""" } };
        var method = typeof(SubscriptionClient).GetMethod("CompleteOpenAIReplay", BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, new object[] { new[] { message } });
        method.Invoke(null, new object[] { new[] { message } });
        using var state = JsonDocument.Parse((string)message.AdditionalProperties[key]!);
        var items = state.RootElement.GetProperty("items").EnumerateArray().ToArray();
        if (items.Length != 3 || items[0].GetProperty("encrypted_content").GetString() != "opaque-fixture" ||
            items[1].GetProperty("call_id").GetString() != "call_one" || items[2].GetProperty("call_id").GetString() != "call_two")
            throw new Exception("Reasoning replay must preserve opaque state and all tool call IDs without duplicates");
        Console.WriteLine("PASS reasoning tool replay preserves opaque state and call correlation without duplication");
    }
}
