using System.Collections.Generic;
using System;
using System.Text.RegularExpressions;

namespace TohotopiaShared;

public sealed record GameRule(
    string Id,
    string Category,
    string Name,
    string Description,
    int? Level = null,
    string? Relation = null,
    string? Source = null,
    string[]? RelatedIds = null,
    string[]? Aliases = null);

public sealed record GameRulesExport(string Language, List<GameRule> Entries, string? GameVersion = null);

public static class GameRuleText
{
    static readonly Regex Embedded = new(@"\*\*<(?:!\[\d+\])?(\d+)>\*\*", RegexOptions.CultureInvariant);
    public static string Resolve(string text, Func<uint, string> lookup)
    {
        var visiting = new HashSet<uint>();
        string Expand(string value, int depth) => Embedded.Replace(value, match =>
        {
            if (!uint.TryParse(match.Groups[1].Value, out uint id)) return "未知（无效本地化引用）";
            if (depth >= 8 || !visiting.Add(id)) return "未知（本地化引用 " + id + "）";
            string replacement = lookup(id);
            string result = string.IsNullOrWhiteSpace(replacement) ? "未知（本地化引用 " + id + "）" : Expand(replacement, depth + 1);
            visiting.Remove(id);
            return result;
        });
        return Expand(text, 0);
    }
}
