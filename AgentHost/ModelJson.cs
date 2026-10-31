using System.Text.Json;

namespace Tohotopia;

// Repair only unambiguous wrappers and trailing commas; never invent game data.
internal static class ModelJson
{
    static readonly JsonSerializerOptions RepairOptions = new(Files.Json) { AllowTrailingCommas = true };
    internal static T Parse<T>(string output) where T : class
    {
        string text = output.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int first = text.IndexOf('\n'), last = text.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && last > first) text = text[(first + 1)..last].Trim();
        }
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start >= 0 && end >= start) text = text[start..(end + 1)];
        return JsonSerializer.Deserialize<T>(text, RepairOptions) ?? throw new JsonException("模型没有返回 JSON 对象");
    }

    internal static async Task<T> Generate<T>(Func<string?, Task<string>> generate, Action<string> retry, CancellationToken ct) where T : class
    {
        string? feedback = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            string output = await generate(feedback);
            try { return Parse<T>(output); }
            catch (JsonException e)
            {
                if (attempt == 2) throw new InvalidOperationException("模型 JSON 修复和重新生成失败", e);
                retry($"模型返回无效 JSON，自动修复失败，正在重新生成（{attempt + 1}/2）");
                feedback = "上一次回复不是有效 JSON（" + e.Message + "）。请根据原始请求重新生成完整答案，只返回一个符合要求的 JSON 对象；不要使用 Markdown。";
            }
        }
        throw new InvalidOperationException("模型 JSON 生成失败");
    }
}
