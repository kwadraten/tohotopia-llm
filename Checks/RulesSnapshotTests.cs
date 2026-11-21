using System.Text.Json;
using Tohotopia;
using TohotopiaShared;
using Xunit;

public class RulesSnapshotTests
{
    // Point TOHOTOPIA_RULES_SNAPSHOT at an exported game-rules.json to audit
    // the real localized snapshot.
    [Fact]
    public void Real_rule_snapshot_is_complete_and_searchable()
    {
        string? path = Environment.GetEnvironmentVariable("TOHOTOPIA_RULES_SNAPSHOT");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Skip("Set TOHOTOPIA_RULES_SNAPSHOT to an exported game-rules.json");
        var snapshot = JsonSerializer.Deserialize<GameRulesExport>(File.ReadAllText(path), RoomFiles.Json)!;
        Assert.True(!snapshot.Entries.GroupBy(e => (e.Category, e.Id, e.Level)).Any(g => g.Count() > 1), "duplicate exported rule identity");
        foreach (string category in new[] { "technology", "skill", "unit", "hero", "action", "terrain", "resource", "building", "wiki" })
        {
            var rows = snapshot.Entries.Where(e => e.Category == category).ToArray();
            Assert.True(rows.Length > 0 && rows.Any(e => !string.IsNullOrWhiteSpace(e.Name)), "localized category exists: " + category);
        }
        var store = GameRuleStore.Load(path);
        Assert.True(!snapshot.Entries.Any(e => System.Text.RegularExpressions.Regex.IsMatch(e.Name + e.Description, @"\*\*<(?:!\[\d+\])?\d+>\*\*")),
            "no unexpanded embedded localization key in exported rules");
        string exact = store.Get("433bfac6-989d-40ea-933a-ca1d090346aa/00300001");
        Assert.True(!exact.Contains("没有ID") && !exact.Contains("名称未解析"), "real candidate technology ID resolves");
        foreach (var (query, category) in new[] { ("挖掘", "action"), ("山脉", "terrain"), ("采矿", "technology") })
        {
            string result = store.Search(query, category);
            Assert.True(!result.Contains("未在本地规则资料中找到"), "real terminology is searchable: " + query);
        }
    }
}
