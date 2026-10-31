using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TohotopiaShared;

namespace Tohotopia;

// Converts wire identifiers into names without inventing rules. Original IDs
// remain available for exact lookup and the game validates the final candidate.
public sealed class RulePrompt
{
    static readonly JsonSerializerOptions Json = new(Files.Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    readonly List<GameRule> entries;
    public RulePrompt(string directory)
    {
        entries = new();
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "game-rules.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            entries = JsonSerializer.Deserialize<GameRulesExport>(stream, RoomFiles.Json)?.Entries ?? new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
    }
    GameRule? Find(string id, string? category = null) => entries.FirstOrDefault(r =>
        (category == null || r.Category == category) && (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase) || r.Aliases?.Contains(id, StringComparer.OrdinalIgnoreCase) == true));
    static IEnumerable<string> References(Candidate candidate)
    {
        foreach (Match match in Regex.Matches(candidate.Description ?? "", @"(?:ActionDefinition|TechDefinition|UnitDefinition|Skill)\s*:\s*([^\r\n;]+)"))
        {
            string value = match.Groups[1].Value.Trim();
            if (!value.StartsWith("00000000-0000-0000-0000-000000000000", StringComparison.Ordinal)) yield return value;
        }
    }
    public string Observation(JsonElement source, bool omitGrids)
    {
        var observation = JsonNode.Parse(source.GetRawText())!.AsObject();
        if (omitGrids) observation.Remove("grids");
        if (observation["players"] is JsonArray players)
            foreach (var player in players.OfType<JsonObject>())
                if (player["technologies"] is JsonArray technologies)
                {
                    var names = new JsonArray();
                    foreach (var tech in technologies)
                    {
                        string code = tech?.GetValue<string>() ?? "";
                        if (code == "None") continue;
                        var rule = Find(code, "technology");
                        names.Add(rule is null ? new JsonObject { ["code"] = code, ["name"] = "未知科技名称" }
                            : new JsonObject { ["id"] = rule.Id, ["name"] = rule.Name });
                    }
                    player["technologies"] = names;
                }
        return observation.ToJsonString(Json);
    }
    public string Candidates(IEnumerable<Candidate> candidates) => JsonSerializer.Serialize(candidates.Select(c =>
    {
        var references = References(c).Distinct().ToArray();
        var rules = references.Select(id => Find(id)).Where(r => r != null).ToArray();
        string description = string.Join("; ", (c.Description ?? "").Split(new[] { '\n', ';' }).Select(s => s.Trim())
            .Where(s => s.Length > 0 && !Regex.IsMatch(s, @":\s*(None|00000000-0000-0000-0000-000000000000[^;]*|AllClear)\s*$"))
            .Where(s => !Regex.IsMatch(s, @"^(ActionDefinition|UnitDefinition|TechDefinition|Tech|Skill)\s*:")));
        string verb = c.Kind.Split(' ')[0] switch
        {
            "LearnTech" => "学习科技", "UnitMove" => "移动", "UnitAttack" => "攻击", "Build" => "建造",
            "EndTurn" => "结束回合", "SendPublicMessage" => "公共消息", "SendTeamMessage" => "队内消息", _ => c.Kind
        };
        return new { c.Id, name = verb + (rules.Length > 0 ? "「" + string.Join("、", rules.Select(r => r!.Name).Distinct()) + "」" : ""), c.Kind,
            c.UnitId, c.CityId, c.GridId, c.TargetUnitId, c.TargetGridId, c.TargetPlayerId,
            ruleIds = references, description };
    }), Json);
    public string Relevant(IEnumerable<Candidate> candidates, JsonElement observation)
    {
        var selected = candidates.SelectMany(References).Select(id => Find(id)).Where(r => r != null).Cast<GameRule>().DistinctBy(r => r.Id).Take(8).ToList();
        if (observation.TryGetProperty("units", out var units))
            foreach (var unit in units.EnumerateArray())
                if (unit.TryGetProperty("name", out var name))
                {
                    var rule = unit.TryGetProperty("definitionId", out var definition) ? Find(definition.GetString() ?? "") : null;
                    rule ??= entries.FirstOrDefault(r => r.Name == name.GetString() && r.Category is "unit" or "hero");
                    if (rule != null && selected.All(r => r.Id != rule.Id) && selected.Count < 12) selected.Add(rule);
                }
        foreach (var id in selected.SelectMany(r => r.RelatedIds ?? Array.Empty<string>()).ToArray())
        {
            var stripped = Regex.Replace(id, @"^(requires|effect|skill|task|hero|group\d+|upgrade(?::UpgradeFromUnitDefId|:UpgradeToUnitDefId)?):", "");
            var rule = Find(stripped);
            if (rule != null && selected.All(r => r.Id != rule.Id) && selected.Count < 12) selected.Add(rule);
        }
        string text = string.Join("\n", selected.Select(r => $"{r.Id} {r.Name}{(r.Level is not null && r.Category is "hero" or "unit" ? " Lv." + r.Level : "")}: {r.Description}"));
        return text.Length <= 4000 ? text : text[..4000] + "\n其余说明请使用规则查询工具。";
    }
}
