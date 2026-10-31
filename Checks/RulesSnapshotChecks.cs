using System.Text.Json;
using Tohotopia;
using TohotopiaShared;

internal static class RulesSnapshotChecks
{
    internal static void Run(string path)
    {
        var snapshot = JsonSerializer.Deserialize<GameRulesExport>(File.ReadAllText(path), RoomFiles.Json)!;
        if (snapshot.Entries.GroupBy(e => (e.Category, e.Id, e.Level)).Any(g => g.Count() > 1))
            throw new Exception("Duplicate exported rule identity");
        foreach (string category in new[] { "technology", "skill", "unit", "hero", "action", "terrain", "resource", "building", "wiki" })
        {
            var rows = snapshot.Entries.Where(e => e.Category == category).ToArray();
            if (rows.Length == 0 || rows.All(e => string.IsNullOrWhiteSpace(e.Name)))
                throw new Exception("Missing localized category: " + category);
            Console.WriteLine($"PASS snapshot {category}: {rows.Length} rows, {rows.Count(e => !string.IsNullOrWhiteSpace(e.Name))} names");
        }
        var store = GameRuleStore.Load(path);
        if (snapshot.Entries.Any(e => System.Text.RegularExpressions.Regex.IsMatch(e.Name + e.Description, @"\*\*<(?:!\[\d+\])?\d+>\*\*")))
            throw new Exception("Unexpanded embedded localization key in exported rules");
        string exact = store.Get("433bfac6-989d-40ea-933a-ca1d090346aa/00300001");
        if (exact.Contains("没有ID") || exact.Contains("名称未解析")) throw new Exception("Real candidate technology ID failed lookup");
        foreach (var (query, category) in new[] { ("挖掘", "action"), ("山脉", "terrain"), ("采矿", "technology") })
        {
            string result = store.Search(query, category);
            if (result.Contains("未在本地规则资料中找到")) throw new Exception("Real terminology not searchable: " + query);
            Console.WriteLine($"PASS actual game rules query {category}/{query}");
        }
        Console.WriteLine("PASS actual candidate compound ID resolves to localized rule");
    }
}
