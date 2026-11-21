using System.Text.Json.Nodes;
using Tohotopia;
using Xunit;

public class OutputTokenTests
{
    [Fact]
    public void Thinking_requests_drop_local_token_caps()
    {
        const string body = """{"model":"fixture","max_tokens":4096,"max_completion_tokens":4096,"max_output_tokens":4096,"thinking":{"type":"adaptive","budget_tokens":1024},"generationConfig":{"maxOutputTokens":4096,"thinkingConfig":{"thinkingBudget":1024,"includeThoughts":true}},"reasoning":{"effort":"high"}}""";
        foreach (string effort in new[] { "default", "low", "high", "max" })
        foreach (string provider in new[] { "openai", "grok", "kimi", "moonshotai", "deepseek", "google", "new-provider" })
        {
            var result = JsonNode.Parse(OutputTokenPolicy.Apply(new() { Provider = provider, Reasoning = effort }, "openai-chat", body))!.AsObject();
            Assert.False(result.ContainsKey("max_tokens") || result.ContainsKey("max_completion_tokens") || result.ContainsKey("max_output_tokens") ||
                result["thinking"]!["budget_tokens"] != null || result["generationConfig"]!["maxOutputTokens"] != null ||
                result["generationConfig"]!["thinkingConfig"]!["thinkingBudget"] != null ||
                result["generationConfig"]!["thinkingConfig"]!["includeThoughts"]!.GetValue<bool>() != true,
                provider + "/" + effort + " retains no local token cap and keeps thought reporting");
        }
        Assert.Equal(body, OutputTokenPolicy.Apply(new() { Reasoning = "none" }, "openai-chat", body));
    }

    [Fact]
    public void Anthropic_messages_use_catalog_model_maxima()
    {
        string prior = Files.Root;
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-tokens-" + Guid.NewGuid().ToString("N"));
        Files.Root = root;
        Directory.CreateDirectory(root);
        string catalog = Path.Combine(root, "models-dev.json");
        const string body = """{"model":"fixture","max_tokens":4096,"max_completion_tokens":4096,"max_output_tokens":4096,"thinking":{"type":"adaptive","budget_tokens":1024},"generationConfig":{"maxOutputTokens":4096,"thinkingConfig":{"thinkingBudget":1024,"includeThoughts":true}},"reasoning":{"effort":"high"}}""";
        try
        {
            File.WriteAllText(catalog, """{"anthropic":{"models":{"fixture":{"limit":{"output":64000}}}},"custom-messages":{"models":{"fixture":{"limit":{"output":96000}}}}}""");
            foreach (var (provider, maximum) in new[] { ("anthropic", 64000), ("custom-messages", 96000) })
            {
                var config = new AgentConfig { Provider = provider, Model = "fixture", Reasoning = "high" };
                var result = JsonNode.Parse(OutputTokenPolicy.Apply(config, "anthropic-messages", body))!;
                Assert.True(result["max_tokens"]!.GetValue<int>() == maximum && result["thinking"]!["budget_tokens"] == null,
                    provider + " adaptive Messages uses the published model maximum");
                result = JsonNode.Parse(OutputTokenPolicy.Apply(config, "anthropic-messages", body.Replace("adaptive", "enabled")))!;
                Assert.True(result["thinking"]!["budget_tokens"]!.GetValue<int>() == maximum - 1,
                    provider + " legacy Messages uses the maximum protocol-valid thinking allowance");
            }
        }
        finally
        {
            Files.Root = prior;
            Directory.Delete(root, true);
        }
    }
}
