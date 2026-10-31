using System.Text.Json.Nodes;
using Tohotopia;

internal static class OutputTokenChecks
{
    internal static void Run(string root)
    {
        const string body = """{"model":"fixture","max_tokens":4096,"max_completion_tokens":4096,"max_output_tokens":4096,"thinking":{"type":"adaptive","budget_tokens":1024},"generationConfig":{"maxOutputTokens":4096,"thinkingConfig":{"thinkingBudget":1024,"includeThoughts":true}},"reasoning":{"effort":"high"}}""";
        foreach (string effort in new[] { "default", "low", "high", "max" })
        foreach (string provider in new[] { "openai", "grok", "kimi", "moonshotai", "deepseek", "google", "new-provider" })
        {
            var result = JsonNode.Parse(OutputTokenPolicy.Apply(new() { Provider = provider, Reasoning = effort }, "openai-chat", body))!.AsObject();
            if (result.ContainsKey("max_tokens") || result.ContainsKey("max_completion_tokens") || result.ContainsKey("max_output_tokens") ||
                result["thinking"]!["budget_tokens"] != null || result["generationConfig"]!["maxOutputTokens"] != null ||
                result["generationConfig"]!["thinkingConfig"]!["thinkingBudget"] != null ||
                result["generationConfig"]!["thinkingConfig"]!["includeThoughts"]!.GetValue<bool>() != true)
                throw new Exception(provider + "/" + effort + " retained a local token cap or lost thought reporting");
        }
        if (OutputTokenPolicy.Apply(new() { Reasoning = "none" }, "openai-chat", body) != body)
            throw new Exception("Non-thinking request settings changed");
        Console.WriteLine("PASS all providers and default/explicit thinking modes remove local output and reasoning token caps; non-thinking settings are preserved");

        string catalog = Path.Combine(root, "models-dev.json");
        var prior = File.Exists(catalog) ? File.ReadAllBytes(catalog) : null;
        try
        {
            File.WriteAllText(catalog, """{"anthropic":{"models":{"fixture":{"limit":{"output":64000}}}},"custom-messages":{"models":{"fixture":{"limit":{"output":96000}}}}}""");
            foreach (var (provider, maximum) in new[] { ("anthropic", 64000), ("custom-messages", 96000) })
            {
                var config = new AgentConfig { Provider = provider, Model = "fixture", Reasoning = "high" };
                var result = JsonNode.Parse(OutputTokenPolicy.Apply(config, "anthropic-messages", body))!;
                if (result["max_tokens"]!.GetValue<int>() != maximum || result["thinking"]!["budget_tokens"] != null)
                    throw new Exception("Adaptive Messages did not use the published model maximum");
                result = JsonNode.Parse(OutputTokenPolicy.Apply(config, "anthropic-messages", body.Replace("adaptive", "enabled")))!;
                if (result["thinking"]!["budget_tokens"]!.GetValue<int>() != maximum - 1)
                    throw new Exception("Legacy Messages did not use the maximum protocol-valid thinking allowance");
            }
            Console.WriteLine("PASS direct and SDK Messages use catalog model maxima, including required legacy thinking budgets");
        }
        finally
        {
            if (prior is null) File.Delete(catalog); else File.WriteAllBytes(catalog, prior);
        }
    }
}
