using System.Text.Json;
using Tohotopia;
using TohotopiaShared;

internal static class RulePromptChecks
{
    internal static void Run()
    {
        var localized = new Dictionary<uint, string> { [873] = "金矿", [1365] = "采矿场", [447] = "爬山", [1] = "**<2>**", [2] = "**<1>**" };
        string expanded = GameRuleText.Resolve("可发现**<873>**，需**<![1]447>**，可建造**<1365>**。", id => localized.GetValueOrDefault(id, ""));
        if (expanded != "可发现金矿，需爬山，可建造采矿场。" || !GameRuleText.Resolve("**<1>**", id => localized.GetValueOrDefault(id, "")).Contains("未知"))
            throw new Exception("Embedded localization references must expand safely without cycles");
        Console.WriteLine("PASS Chinese embedded references resolve names and cycles remain explicitly unknown");
        string directory = Path.Combine(Path.GetTempPath(), "rule-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var definitions = new GameRulesExport("ZH", new()
        {
            new("mod/00300006", "technology", "采矿", "可以建造采矿场", Aliases: new[] { "KanakoMining" }, RelatedIds: new[] { "effect:mod/00400017" }),
            new("mod/00400017", "technologyEffect", "建造采矿场", "将金矿建造为采矿场，提供2点城市发展度"),
            new("hero/10000027", "hero", "芙兰朵露 Lv.1", "英雄", Level: 1, RelatedIds: new[] { "task:hero/task:1" }),
            new("hero/task:1", "heroTask", "芙兰升级任务", "占领村庄获得60英雄经验")
        });
        File.WriteAllText(Path.Combine(directory, "game-rules.json"), JsonSerializer.Serialize(definitions, RoomFiles.Json));
        var formatter = new RulePrompt(directory);
        using var observation = JsonDocument.Parse("""{"grids":[{"id":1}],"players":[{"id":326,"technologies":["None","KanakoMining","missing"]}],"units":[{"name":"芙兰朵露 Lv.1"}]}""");
        var candidate = new Candidate("a0", "LearnTech", 0, 0, 1, 0, 0, 0, "Action : LearnTech\nTechDefinition : mod/00300006\nUnit : None\nCivicDefinition : 00000000-0000-0000-0000-000000000000/00000000\n; coinCost=5");
        string state = formatter.Observation(observation.RootElement, true), choices = formatter.Candidates(new[] { candidate });
        if (state.Contains("grids") || !state.Contains("采矿") || !state.Contains("未知科技名称")) throw new Exception("Observation must localize known technologies and preserve unknown semantics");
        if (!choices.Contains("采矿") || !choices.Contains("mod/00300006") || choices.Contains("None") || choices.Contains("00000000-0000-0000-0000-000000000000") || !choices.Contains("coinCost=5")) throw new Exception("Candidate names, stable IDs and costs must survive compact formatting");
        string relevant = formatter.Relevant(new[] { candidate }, observation.RootElement);
        if (!relevant.Contains("2点城市发展度") || !relevant.Contains("60英雄经验")) throw new Exception("Related technology effects and hero tasks must be included");
        Console.WriteLine("PASS compact prompts localize names, keep IDs and costs, remove duplicate grid JSON and include related rules");
    }
}
