using System.Text;
using System.Text.Json;
using TohotopiaShared;

namespace Tohotopia;

/// <summary>Read-only search over the locally exported, Chinese game-rule snapshot.</summary>
public sealed class GameRuleStore
{
    const int MaxResults = 6;
    const int MaxResultChars = 7000;
    readonly GameRulesExport? export;
    readonly string? unavailable;

    GameRuleStore(GameRulesExport? export, string? unavailable)
    {
        this.export = export;
        this.unavailable = unavailable;
    }

    public static GameRuleStore Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(null, "本局尚未导出本地规则资料。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = JsonSerializer.Deserialize<GameRulesExport>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return data?.Entries is { Count: > 0 }
                ? new(data, null)
                : new(null, "本地规则资料为空或格式无效。");
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(null, "本地规则资料无法读取。");
        }
    }

    public string Search(string query, string? category = null, int limit = MaxResults)
    {
        if (export is null) return unavailable! + "请将相关规则明确标记为未知，不要根据名称猜测。";
        if (string.IsNullOrWhiteSpace(query)) return "查询词为空。请提供中文名称、复合ID或关键词。";
        limit = Math.Clamp(limit, 1, MaxResults);
        string notice = "";
        if (!string.IsNullOrWhiteSpace(category) && !export.Entries.Any(r => Eq(r.Category, category)))
        {
            notice = $"类别“{Short(category, 80)}”不在资料中，已跨类别搜索。可用类别：{string.Join(", ", export.Entries.Select(r => r.Category).Distinct())}。\n";
            category = null;
        }
        var terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = export.Entries
            .Where(r => string.IsNullOrWhiteSpace(category) || Eq(r.Category, category))
            .Select(r => (Rule: r, Score: Score(r, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Rule.Name, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();
        if (hits.Count == 0)
            return notice + $"未在本地规则资料中找到“{Short(query, 120)}”" + (string.IsNullOrWhiteSpace(category) ? "。" : $"（类别 {Short(category, 80)}）；可尝试省略类别。。") + "结论：未知；不能据此推断规则不存在。";

        bool more = hits.Count > limit;
        var sb = new StringBuilder(notice + $"本地规则资料（语言={export.Language}），匹配 {hits.Count - (more ? 1 : 0)} 项{(more ? "，还有更多结果" : "")}：\n");
        foreach (var (rule, _) in hits.Take(limit))
        {
            AppendRule(sb, rule);
            if (sb.Length >= MaxResultChars) { sb.Length = MaxResultChars; sb.Append("\n[结果已截断]"); break; }
        }
        if (hits.Count > 1 && hits[0].Score == hits[1].Score)
            sb.Append("\n名称/关键词匹配有歧义；请用复合ID或等级再次查询，不要任选一个当作确定规则。");
        return sb.ToString();
    }

    public string Get(string id, int? level = null)
    {
        if (export is null) return unavailable! + "请将具体规则明确标记为未知，不要根据名称猜测。";
        if (string.IsNullOrWhiteSpace(id)) return "规则ID为空，请使用 search_game_rules 找到复合ID。";
        var exactId = export.Entries.Where(r => string.Equals(r.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exactId.Length == 0)
            exactId = export.Entries.Where(r => r.Aliases?.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase) == true).ToArray();
        var matches = exactId.Where(r => level is null || r.Level == level).ToArray();
        if (matches.Length == 0)
        {
            if (exactId.Length == 0) return $"本地规则资料没有ID“{Short(id, 120)}”。结论：未知；请勿猜测。";
            string levels = string.Join(", ", exactId.Select(r => r.Level?.ToString() ?? "未注明").Distinct());
            return $"ID“{Short(id, 120)}”没有等级 {level} 的条目。资料中可见等级：{levels}。该等级规则未知。";
        }
        if (matches.Length > 1)
            return $"ID“{Short(id, 120)}”对应多个规则等级（{string.Join(", ", matches.Select(r => r.Level?.ToString() ?? "未注明"))}），请补充 level。";
        var sb = new StringBuilder("本地规则资料：\n");
        AppendRule(sb, matches[0]);
        if (sb.Length > MaxResultChars) { sb.Length = MaxResultChars; sb.Append("\n[结果已截断]"); }
        return sb.ToString();
    }

    static int Score(GameRule r, string[] terms)
    {
        int score = 0;
        string fields = string.Join("\n", r.Id, r.Name, r.Category, r.Description, r.Relation, r.RelatedIds is null ? "" : string.Join(" ", r.RelatedIds), r.Aliases is null ? "" : string.Join(" ", r.Aliases));
        foreach (var term in terms)
        {
            if (r.Id.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 12;
            if (r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (r.Aliases?.Any(alias => alias.Contains(term, StringComparison.OrdinalIgnoreCase)) == true) score += 8;
            if (r.Category.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 3;
            if (r.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 2;
            if (score == 0 && fields.Contains(term, StringComparison.OrdinalIgnoreCase)) score++;
        }
        return score;
    }

    static void AppendRule(StringBuilder sb, GameRule r)
    {
        bool unresolved = r.Source?.Contains("unresolvedLocalization", StringComparison.OrdinalIgnoreCase) == true;
        sb.Append("- 类别：").Append(Short(r.Category, 80)).Append("；名称：").Append(unresolved ? "本地化名称未解析" : Short(r.Name, 180));
        if (!string.IsNullOrWhiteSpace(r.Id)) sb.Append("；复合ID：").Append(Short(r.Id, 180));
        if (r.Level is not null) sb.Append(r.Category == "technology" ? "；费用等级：" : "；等级：").Append(r.Level.Value);
        sb.Append("\n  定义/效果：").Append(string.IsNullOrWhiteSpace(r.Description) ? "本地化定义缺失，未知。" : Short(r.Description, 1600));
        if (!string.IsNullOrWhiteSpace(r.Relation)) sb.Append("\n  关系：").Append(Short(r.Relation, 300));
        if (r.RelatedIds is { Length: > 0 }) sb.Append("\n  相关ID：").Append(string.Join(", ", r.RelatedIds.Take(20).Select(x => Short(x, 120))));
        if (!string.IsNullOrWhiteSpace(r.Source)) sb.Append("\n  来源类型：").Append(Short(r.Source, 120));
        sb.AppendLine();
    }

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static string Short(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
