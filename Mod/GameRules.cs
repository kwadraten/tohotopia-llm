using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TohotopiaMod.Json;
using TohotopiaShared;
using R = TohotopiaMod.GameReflection;

namespace TohotopiaMod;

/// <summary>Incremental, read-only catalog export for the AgentHost.</summary>
internal static class GameRules
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    static readonly List<GameRule> Entries = new();
    static readonly string[] Assets = { "Tech", "TechAtom", "Skill", "Unit", "Hero", "Action", "Terrain", "Resource", "NaturalWonder", "Wiki", "GridContent" };
    static readonly Dictionary<uint, string> Chinese = new();
    static object _languageDictionary;
    static string? _directory;
    static string? _invalidatedDirectory;
    static string? _language;
    static string? _version;
    static object _table;
    static int _assetIndex;
    static int _rowIndex;
    static object _rows;
    static int _rowCount;
    static readonly HashSet<string> HeroIds = new(StringComparer.Ordinal);
    static readonly HashSet<string> HeroGiantTypes = new(StringComparer.Ordinal);
    static bool _heroIdsLoaded;
    static object _heroRows;
    static int _heroIndex;
    static int _heroCount;
    static bool _done;
    static string? _lastError;
    static string? _lastMatchJson;
    static string? _lastMatchPath;
    static object _multilingualManager;
    static int? _zhValue;

    internal static void RequestRefresh()
    {
        InvalidateRuleCatalog(_directory);
        _invalidatedDirectory = _directory;
        _table = null; _done = false; _assetIndex = 0; _rowIndex = 0; _rows = null; _rowCount = 0; _heroRows = null; _heroIndex = 0; _heroCount = 0;
        _heroIdsLoaded = false; _lastMatchJson = null; _lastMatchPath = null; _multilingualManager = null; _zhValue = null;
        _languageDictionary = null; Chinese.Clear(); Entries.Clear();
    }

    // Call from the Unity update loop with a small budget. A missing/unready Table is a no-op.
    internal static bool ExportStep(string runtimeDirectory, int itemBudget = 12, int timeBudgetMs = 2)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(runtimeDirectory)) return false;
            if (_invalidatedDirectory != runtimeDirectory)
            {
                InvalidateRuleCatalog(runtimeDirectory);
                _invalidatedDirectory = runtimeDirectory;
                _directory = runtimeDirectory;
                _done = false; _assetIndex = 0; _rowIndex = 0; _rows = null; _rowCount = 0; Entries.Clear();
                _heroIdsLoaded = false; _heroRows = null; _heroIndex = 0; _heroCount = 0;
            }
            object table = StaticProperty("Table", "Instance");
            if (table == null)
            {
                if (_table != null) { InvalidateRuleCatalog(runtimeDirectory); _table = null; _done = false; Entries.Clear(); _assetIndex = 0; _rows = null; }
                return false;
            }
            string language = CurrentLanguage();
            string version = GameRuntime.Version;
            if (_table != table || _directory != runtimeDirectory || _language != language || _version != version)
            {
                if (_table != null || _version != null) InvalidateRuleCatalog(runtimeDirectory);
                _table = table; _directory = runtimeDirectory; _language = language; _version = version;
                _assetIndex = 0; _rowIndex = 0; _rows = null; _rowCount = 0; _done = false; Entries.Clear();
                HeroIds.Clear(); HeroGiantTypes.Clear(); _heroIdsLoaded = false; _heroRows = null; _heroIndex = 0; _heroCount = 0;
                _languageDictionary = null; Chinese.Clear();
            }
            int maxItems = Math.Max(1, Math.Min(itemBudget, 64));
            double maxMilliseconds = Math.Max(1, Math.Min(timeBudgetMs, 16));
            int processed = 0;
            if (_languageDictionary == null)
            {
                _multilingualManager = Native.Static("Logic.Multilingual.MultilingualManager", "Instance");
                if (_multilingualManager == null) return false;
                var multilingual = Native.Get<object>(_multilingualManager, "_multilingualData");
                if (multilingual == null) return false;
                _languageDictionary = Native.Get<object>(multilingual, "_itemDict");
                if (_languageDictionary == null)
                {
                    Native.UIntCall(multilingual, "hnx", 0);
                    _languageDictionary = Native.Get<object>(multilingual, "_itemDict");
                    if (_languageDictionary == null) return false;
                }
            }
            if (!_heroIdsLoaded)
            {
                if (_heroRows == null)
                {
                    object heroData = Native.Get<object>(_table, "HeroDataAssets");
                    if (heroData == null) return false;
                    _heroRows = Native.Get<object>(heroData, "HeroInfoList");
                    if (_heroRows == null) return false;
                    _heroCount = Native.Value<int>(_heroRows, "get_Count");
                }
                while (_heroIndex < _heroCount && processed < maxItems && timer.Elapsed.TotalMilliseconds < maxMilliseconds)
                {
                    object hero = ListItem(_heroRows, _heroIndex);
                    HeroIds.Add(FieldString(hero, "UnitDefId"));
                    string giantType = Scalar(hero, "GiantType");
                    if (!string.IsNullOrWhiteSpace(giantType) && giantType != "None") HeroGiantTypes.Add(giantType);
                    _heroIndex++; processed++;
                }
                if (_heroIndex < _heroCount) return false;
                _heroIdsLoaded = true;
            }
            if (_done) return true;
            while (processed < maxItems && timer.Elapsed.TotalMilliseconds < maxMilliseconds && _assetIndex < Assets.Length)
            {
                if (_rows == null)
                {
                    string kind = Assets[_assetIndex];
                    string field = kind switch { "Tech" => "TechList", "TechAtom" => "TechAtomList", "Skill" => "SkillInfoList", "Unit" => "UnitTypeList", "Hero" => "HeroInfoList", "Action" => "ActionList", "Terrain" => "TerrainInfoList", "Resource" => "ResourceInfoList", "NaturalWonder" => "NaturalWonderInfoList", "Wiki" => "Items", _ => "<Definitions>k__BackingField" };
                    string asset = kind switch { "Tech" or "TechAtom" => "TechDataAssets", "Skill" => "SkillDataAssets", "Unit" => "UnitTypeDataAssets", "Hero" => "HeroDataAssets", "Action" => "ActionDataAssets", "NaturalWonder" => "NaturalWonderDataAssets", "Wiki" => "WikiData", _ => "GridAndResourceDataAssets" };
                    object data = kind == "GridContent" ? GridCatalog() : Native.Get<object>(_table, asset);
                    if (data == null) return false; // Wait for Table's asset initialization; do not publish a partial catalog.
                    if (kind == "Terrain")
                        foreach (string surface in new[] { "Mountain", "Vegetation" })
                        {
                            var info = Native.Get<object>(data, surface + "Info");
                            string name = Localized(info, "ResourceName"), desc = Localized(info, "ResourceDesc");
                            Entries.Add(new GameRule("terrain:" + surface, "terrain", name, desc, Source: SourceFor(surface + "Info", name, desc), Aliases: new[] { surface }));
                        }
                    _rows = Native.Get<object>(data, field);
                    if (_rows == null) return false;
                    _rowIndex = 0; _rowCount = Native.Value<int>(_rows, "get_Count");
                }
                if (_rowIndex >= _rowCount) { _rows = null; _rowCount = 0; _assetIndex++; continue; }
                object row = ListItem(_rows, _rowIndex);
                AddItem(Assets[_assetIndex], row);
                _rowIndex++; processed++;
            }
            if (_assetIndex >= Assets.Length)
            {
                if (Entries.Any(e => string.IsNullOrWhiteSpace(e.Id)) || Entries.GroupBy(e => (e.Category, e.Id, e.Level)).Any(g => g.Count() > 1))
                    throw new InvalidOperationException("规则导出含空 ID 或重复 ID，拒绝发布。");
                if (Entries.Where(e => e.Category == "technology").All(e => string.IsNullOrWhiteSpace(e.Name)))
                    throw new InvalidOperationException("中文规则未解析，拒绝发布。");
                WriteAtomic(Path.Combine(runtimeDirectory, "game-rules.json"), new GameRulesExport(language, Entries, version));
                _done = true;
            }
            return _done;
        }
        catch (Exception ex) { if (_lastError != ex.Message) { Battle.Log.LogWarning("[TohotopiaMod] Game rule export deferred: " + ex.Message); _lastError = ex.Message; } return false; }
    }

    internal static bool ExportMatchContext(string runtimeDirectory, object mapConfig)
    {
        if (mapConfig == null || string.IsNullOrWhiteSpace(runtimeDirectory)) return InvalidateMatchContext(runtimeDirectory);
        try
        {
            var settlements = Native.Items(Native.Get<object>(mapConfig, "PlayerSettlements"));
            var settlementEntries = new List<Dictionary<string, object?>>();
            foreach (var item in settlements)
            {
                var tasks = new List<Dictionary<string, object?>>();
                foreach (var task in Native.Items(Native.Get<object>(item, "Tasks")))
                {
                    var type = Scalar(task, "TaskType");
                    var limitsForTask = new List<string>();
                    foreach (var limit in Native.Items(Native.Get<object>(task, "UnlockLimits"))) limitsForTask.Add(LimitDescription(ObjectString(limit)));
                    tasks.Add(new Dictionary<string, object?> {
                        ["taskType"] = type, ["description"] = TaskDescription(type, task),
                        ["customDescription"] = Localized(task, "CustomDesc"), ["customHint"] = Localized(task, "CustomHint"),
                        ["param1"] = Native.Get<int>(task, "Param1"), ["param2"] = Native.Get<int>(task, "Param2"),
                        ["param3"] = Native.Get<int>(task, "Param3"), ["param4"] = Scalar(task, "Param4"),
                        ["order"] = Native.Get<int>(task, "Order"), ["unlockLimits"] = limitsForTask
                    });
                }
                string settlementType = Scalar(item, "SettlementType");
                settlementEntries.Add(new Dictionary<string, object?> {
                    ["settlementType"] = settlementType, ["description"] = PlayerSettlementDescription(settlementType), ["tasks"] = tasks
                });
            }
            var limits = new List<string>();
            foreach (var item in Native.Items(Native.Get<object>(mapConfig, "MatchLimits"))) limits.Add(LimitDescription(ObjectString(item)));
            string matchSettlement = Scalar(mapConfig, "MatchSettlement");
            string mapConfigId = Scalar(mapConfig, "Id");
            string mapName = Native.TextField(mapConfig, "MapName");
            string gameMode = Scalar(mapConfig, "GameMode");
            var context = new Dictionary<string, object?> {
                ["schemaVersion"] = 1, ["language"] = CurrentLanguage(),
                ["mapConfigId"] = mapConfigId, ["mapName"] = mapName, ["gameMode"] = gameMode,
                ["matchSettlement"] = matchSettlement, ["matchSettlementDescription"] = MatchSettlementDescription(matchSettlement),
                ["playerSettlements"] = settlementEntries, ["matchLimits"] = limits,
                ["isLimitTime"] = Native.Get<byte>(mapConfig, "IsLimitTime") != 0,
                ["timeLimitSeconds"] = Native.Get<int>(mapConfig, "TimeLimitSeconds"),
                ["source"] = "current MapConfig public match configuration"
            };
            string fingerprintInput = JsonSerializer.Serialize(context, Json);
            context["fingerprint"] = Hash(fingerprintInput);
            string json = JsonSerializer.Serialize(context, Json);
            string path = Path.Combine(runtimeDirectory, "match-rules.json");
            if (_lastMatchJson != json || _lastMatchPath != path)
            {
                Directory.CreateDirectory(runtimeDirectory);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                _lastMatchJson = json; _lastMatchPath = path;
            }
            return true;
        }
        catch (Exception ex) { Battle.Log.LogWarning("[TohotopiaMod] Match rule export skipped: " + ex.Message); return InvalidateMatchContext(runtimeDirectory); }
    }

    static bool InvalidateMatchContext(string runtimeDirectory)
    {
        _lastMatchJson = null; _lastMatchPath = null;
        try { if (!string.IsNullOrWhiteSpace(runtimeDirectory)) File.Delete(Path.Combine(runtimeDirectory, "match-rules.json")); } catch { }
        return false;
    }

    static void InvalidateRuleCatalog(string? runtimeDirectory)
    {
        try { if (!string.IsNullOrWhiteSpace(runtimeDirectory)) File.Delete(Path.Combine(runtimeDirectory, "game-rules.json")); } catch { }
    }

    static void AddItem(string kind, object item)
    {
        if (item == null || (kind is "Action" or "Tech" or "TechAtom" or "Skill" or "Unit") && ReadBool(item, "Retired")) return;
        switch (kind)
        {
            case "Action":
            {
                string name = Localized(item, "ActionName"), desc = Localized(item, "Desc");
                desc += "\n" + Localized(item, "ShortDesc") + "\n科技要求：" + Localized(item, "TechDesc") + "\n锁定条件：" + Localized(item, "LockDesc");
                desc += $"\n基础费用={Native.Get<int>(item, "Cost")}；同城同建筑费用增量={Native.Get<int>(item, "CostIncreasePerSameCityBuilding")}；城市经验={Native.Get<int>(item, "CityExp")}。实际费用以当前候选为准。";
                var behavior = Native.Get<object>(item, "ActionId");
                var aliases = new List<string>();
                foreach (var field in new[] { "ActionType", "UnitActionType", "GridMiscActionType", "ResourceType", "TechType", "PlayerActionType" })
                {
                    string value = Scalar(behavior, field);
                    if (value != "None") aliases.Add(value);
                }
                Entries.Add(new GameRule(FieldString(item, "DefinitionId"), "action", name, desc, Source: SourceFor("ActionInfo", name, desc), Aliases: aliases.ToArray()));
                AddVariants(item, "IconList", FieldString(item, "DefinitionId"), "action");
                break;
            }
            case "Terrain":
            case "Resource":
            {
                string code = Scalar(item, kind == "Terrain" ? "TerrainType" : "Resource");
                string name = Localized(item, "ResourceName"), desc = Localized(item, "ResourceDesc");
                if (kind == "Resource") desc += $"\n基础城市经验={Native.Get<int>(item, "Exp")}；等级上限={Native.Get<int>(item, "MaxLevel")}；每等级城市经验={Native.Get<int>(item, "CityExpPerLevel")}。";
                Entries.Add(new GameRule(kind.ToLowerInvariant() + ":" + code, kind.ToLowerInvariant(), name, desc, Source: SourceFor(kind + "Info", name, desc), Aliases: new[] { code }));
                AddVariants(item, "SpriteList", kind.ToLowerInvariant() + ":" + code, kind.ToLowerInvariant());
                break;
            }
            case "Wiki":
            {
                string name = Localized(item, "Name");
                var descriptions = new List<string>();
                foreach (var part in Native.Items(Native.Get<object>(item, "DescItems")))
                {
                    string text = Localized(part, "Desc");
                    if (!string.IsNullOrWhiteSpace(text)) descriptions.Add(text);
                    else if (ReadBool(part, "UseHint")) descriptions.Add("动态提示需依据本次实际状态，静态资料未解析。");
                }
                string desc = string.Join("\n", descriptions);
                Entries.Add(new GameRule("wiki:" + Native.Get<uint>(item, "Id"), "wiki", name, desc, Source: SourceFor("WikiData.Items", name, desc)));
                break;
            }
            case "NaturalWonder":
            {
                string name = Localized(item, "Name"), desc = Localized(item, "GameplayDescription") + "\n" + Localized(item, "GeographyDescription");
                Entries.Add(new GameRule(FieldString(item, "DefinitionId"), "naturalWonder", name, desc, Source: SourceFor("NaturalWonderInfo", name, desc), Aliases: new[] { Native.TextField(item, "StableKey") }));
                break;
            }
            case "GridContent":
            {
                var key = Native.BoxField(item, "<Key>k__BackingField");
                string category = EnumText(key, "Kind").ToLowerInvariant();
                string name = Localized(item, "<Name>k__BackingField"), desc = Localized(item, "<Description>k__BackingField");
                desc += GridNumbers(item);
                Entries.Add(new GameRule(category + ":" + Native.BoxedDefinitionId(key), category, name, desc, Source: SourceFor("GridContentDefinition", name, desc), Aliases: new[] { Native.BoxedDefinitionId(key) }));
                break;
            }
            case "Tech":
            {
                var id = FieldString(item, "TechId"); var prereqs = TechPrerequisites(item);
                string desc = Localized(item, "Description");
                var effects = FieldIds(item, "TechAtomIds");
                string name = Localized(item, "TechName");
                Entries.Add(new GameRule(id, "technology", name, desc,
                    ReadInt(item, "CostLevel"), "requiresGroupsAllOf", SourceFor("TechInfo", name, desc), Merge("", prereqs, "effect:", effects), Aliases("TechType", item, "TechType")));
                break;
            }
            case "TechAtom":
            {
                string name = Localized(item, "TechAtomName"), desc = Localized(item, "Desc");
                Entries.Add(new GameRule(FieldString(item, "TechAtomId"), "technologyEffect", name, desc, null, null, SourceFor("TechAtomInfo", name, desc), FieldIds(item, "TechActionIds"), Aliases("TechAtom", item, "TechAtom")));
                break;
            }
            case "Skill":
            {
                string name = Localized(item, "SkillName"), desc = Localized(item, "SkillDesc");
                Entries.Add(new GameRule(FieldString(item, "SkillId"), "skill", name, desc, null, null, SourceFor("SkillInfo", name, desc), null, Aliases("SkillType", item, "SkillType")));
                break;
            }
            case "Unit":
            {
                string id = FieldString(item, "UnitDefId");
                var skillIds = FieldIds(item, "SkillIds");
                var upgrades = new List<string>();
                foreach (var field in new[] { "UpgradeFromUnitDefId", "UpgradeToUnitDefId" })
                    try { object boxed = Native.BoxField(item, field); var value = ObjectString(boxed); if (boxed != null && Native.Get<uint>(boxed, "LocalId") != 0) upgrades.Add("upgrade:" + field + ":" + value); } catch { }
                var level = ReadUInt(item, "UnitLevel");
                string name = Localized(item, "Name"), desc = Localized(item, "Desc");
                string giantType = Scalar(item, "GiantType");
                bool isHero = HeroIds.Contains(id) || HeroGiantTypes.Contains(giantType);
                Entries.Add(new GameRule(id, isHero ? "hero" : "unit", name, desc, level > 0 ? (int)level : null, "upgrade", SourceFor("UnitTypeInfo", name, desc), Merge("skill:", skillIds, "", upgrades.ToArray()), UnitAliases(item)));
                break;
            }
            case "Hero":
            {
                var tasks = Native.Items(Native.Get<object>(item, "TaskList"));
                string giantType = Scalar(item, "GiantType");
                var groups = new Dictionary<uint, List<object>>();
                foreach (object task in tasks)
                {
                    uint taskGroup = ReadUInt(task, "TaskId");
                    if (taskGroup == 0) continue;
                    if (!groups.TryGetValue(taskGroup, out var group)) groups[taskGroup] = group = new List<object>();
                    group.Add(task);
                }
                var heroUnits = Entries.FindAll(r => r.Category == "hero" && r.Aliases?.Contains("GiantType:" + giantType, StringComparer.Ordinal) == true);
                foreach (var heroUnit in heroUnits)
                {
                    if (heroUnit.Level is not int level) continue;
                    uint applicableGroup = 0;
                    foreach (uint groupId in groups.Keys) if (groupId <= (uint)level && groupId > applicableGroup) applicableGroup = groupId;
                    if (applicableGroup == 0) continue; // Matches HeroDataAssets.kau: use the greatest TaskId <= unit level.
                    string groupText = applicableGroup.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string taskRuleId = heroUnit.Id + ":task:level:" + level.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":group:" + groupText;
                    var descriptions = new List<string>();
                    var related = new List<string> { "hero:" + heroUnit.Id };
                    foreach (object task in groups[applicableGroup])
                    {
                        string desc = Localized(task, "Desc");
                        uint experience = ReadUInt(task, "ExperiencePerProgress");
                        descriptions.Add((string.IsNullOrWhiteSpace(desc) ? "任务说明未知。" : desc) + "（每进度英雄经验：" + experience.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）");
                        related.Add("ExperiencePerProgress:" + experience.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        try { string skillId = FieldString(task, "SkillId"); if (!string.IsNullOrWhiteSpace(skillId)) related.Add("skill:" + skillId); } catch { }
                    }
                    Entries.Add(new GameRule(taskRuleId, "heroTask", heroUnit.Name + "任务组 " + groupText, string.Join("\n", descriptions), level, "availableAtHeroLevel", "HeroTaskInfoData.TaskIdGroup", related.ToArray()));
                    int heroUnitIndex = Entries.FindIndex(r => r.Id == heroUnit.Id && r.Category == "hero");
                    if (heroUnitIndex >= 0)
                    {
                        var links = new List<string>(Entries[heroUnitIndex].RelatedIds ?? Array.Empty<string>()) { "task:" + taskRuleId };
                        Entries[heroUnitIndex] = Entries[heroUnitIndex] with { RelatedIds = links.ToArray() };
                    }
                }
                break;
            }
        }
    }

    static string[] Merge(string p1, string[] a, string p2, string[]? b)
    { var r = new List<string>(); foreach (var x in a) r.Add(p1 + x); if (b != null) foreach (var x in b) r.Add(p2 + x); return r.ToArray(); }
    static string[] FieldIds(object o, string field) { var xs = Native.Items(Native.Get<object>(o, field)); var r = new string[xs.Count]; for (int i=0;i<xs.Count;i++) r[i] = ObjectString(xs[i]); return r; }
    static string[] TechPrerequisites(object item)
    {
        try
        {
            var groups = Native.Items(Native.Get<object>(item, "PrerequisiteGroups"));
            if (groups.Count > 0)
            {
                var encoded = new List<string>();
                for (int i = 0; i < groups.Count; i++)
                    foreach (var id in Native.Items(Native.Get<object>(groups[i], "AnyOfTechIds"))) encoded.Add("group" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + ObjectString(id));
                return encoded.ToArray(); // Every group is required; any member within a group satisfies it.
            }
        }
        catch { }
        return FieldIds(item, "FatherTechIds");
    }
    static object ListItem(object list, int index) => Native.Call(list, "get_Item", index);
    static string[] Aliases(string prefix, object o, string field) { try { var value = Scalar(o, field); return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value, prefix + ":" + value }; } catch { return Array.Empty<string>(); } }
    static string[] UnitAliases(object o) { var result = new List<string>(); result.AddRange(Aliases("UnitType", o, "UnitType")); result.AddRange(Aliases("GiantType", o, "GiantType")); try { var level = ReadUInt(o, "UnitLevel"); if (level > 0) result.Add("UnitLevel:" + level.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { } return result.ToArray(); }
    static object GridCatalog()
    {
        var catalog = Native.Static("TH1_Logic.GridContent.GridContentRuntime", "_catalog");
        return catalog != null ? catalog : Native.Static("TH1_Logic.GridContent.GridContentRuntime", "BuiltinCatalog");
    }
    static string GridNumbers(object item)
    {
        var numbers = Native.Get<object>(item, "<NumericOverrides>k__BackingField");
        var text = new StringBuilder();
        foreach (var field in Native.EnumValues("TH1_Data.GridContentNumericField"))
        {
            int key = field.Value, value = 0;
            if (Native.TryDictionaryInt(numbers, key, out value))
                text.Append("\n").Append(field.Key).Append('=').Append(value);
        }
        return text.ToString();
    }
    static void AddVariants(object item, string field, string id, string category)
    {
        int index = 0;
        foreach (var variant in Native.Items(Native.Get<object>(item, field)))
        {
            index++;
            string name = Localized(variant, "Name"), desc = Localized(variant, "Desc");
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(desc)) continue;
            desc += $"\n条件：IgnoreCivId={ReadBool(variant, "IgnoreCivId")}；CivId={ReadUInt(variant, "CivId")}；IgnoreForceId={ReadBool(variant, "IgnoreForceId")}；ForceId={ReadUInt(variant, "ForceId")}；IsGridSpType={ReadBool(variant, "IsGridSpType")}；GridSpType={Scalar(variant, "GridSpType")}。仅在对应条件下适用。";
            Entries.Add(new GameRule(id + ":variant:" + index, category, name, desc, Relation: "variantOf", Source: SourceFor("EmpireGridSpInfoPack", name, desc), RelatedIds: new[] { id }));
        }
    }
    static string SourceFor(string type, string name, string desc) => string.IsNullOrWhiteSpace(name) ? type + ":unresolvedLocalization" : type;
    static string Localized(object o, string field)
    {
        string raw = Native.TextField(o, field);
        if (uint.TryParse(raw, out uint id)) raw = ChineseText(id);
        return GameRuleText.Resolve(raw, ChineseText);
    }
    static string ChineseText(uint id)
    {
        if (Chinese.TryGetValue(id, out var chinese) && !string.IsNullOrWhiteSpace(chinese)) return chinese;
        var entry = Native.DictionaryObject(_languageDictionary, id);
        if (entry != null)
        {
            chinese = Native.TextField(entry, "ZH");
            if (!string.IsNullOrWhiteSpace(chinese)) { Chinese[id] = chinese; return chinese; }
        }
        try
        {
            if (_multilingualManager == null) _multilingualManager = StaticProperty("Logic.Multilingual.MultilingualManager", "Instance");
            _zhValue ??= Native.EnumValues("Logic.Multilingual.MultilingualType").TryGetValue("ZH", out int zhValue) ? zhValue : 1;
            if (_multilingualManager != null)
            {
                var result = Native.Call(_multilingualManager, "hoz", id, _zhValue.Value);
                if (result is string text && !string.IsNullOrWhiteSpace(text) && !uint.TryParse(text, out _)) return text;
            }
        }
        catch { }
        return ""; // Never present an unresolved localization key as player-facing prose.
    }
    static string FieldString(object o, string field) => Native.DefinitionId(o, field);
    static string ObjectString(object o)
    {
        if (o == null) return "";
        try { return Native.BoxedDefinitionId(o); } catch { }
        return Native.TextCall(o, "ToString");
    }
    static string Scalar(object o, string field) => EnumText(o, field);
    static string EnumText(object o, string field) => Native.EnumField(o, field);
    static bool ReadBool(object o, string field) { try { return Native.Get<byte>(o, field) != 0; } catch { return false; } }
    static int? ReadInt(object o, string field) { try { return Native.Get<int>(o, field); } catch { return null; } }
    static uint ReadUInt(object o, string field) { try { return Native.Get<uint>(o, field); } catch { return 0; } }
    static object StaticProperty(string type, string property)
    {
        var c = R.FindClass(type); if (c == null) return null;
        var m = R.FindMethod(c, "get_" + property, 0);
        if (m != null) return R.InvokeStaticMethod(m, Array.Empty<object>());
        try { return Native.Static(type, "<" + property + ">k__BackingField"); } catch { }
        try { return Native.Static(type, property); } catch { return null; }
    }
    static string CurrentLanguage() => "ZH";
    static string MatchSettlementDescription(string value) => value switch {
        "Normal" => "常规结算：任一玩家达成胜利结算后结束；否则等待存活玩家完成结算，其他玩家均失败或出局后结束。",
        "Tutor" => "教程结算：存活玩家完成结算后结束；成功任务可按UnlockLimits解除对应对局限制。具体目标见配置任务。",
        "Story" => "剧情结算：所有存活玩家完成结算后结束。具体目标见配置任务。",
        "None" => "未配置结算类型，胜利条件未知。",
        _ => "未知结算类型，胜利条件未知。"
    };
    static string PlayerSettlementDescription(string value) => value switch {
        "AllSuccessOrFailure" => "全部任务成功则胜利；任一任务失败则失败。按Order从小到大执行同序任务。",
        "OneSuccessOrFailure" => "任一任务成功即胜利；所有任务均已结算且无成功时失败。按Order从小到大执行同序任务。",
        "None" => "未配置玩家结算类型，具体条件未知。",
        _ => "未知玩家结算类型，具体条件未知。"
    };
    static string TaskDescription(string value, object task)
    {
        int p1 = Native.Get<int>(task, "Param1"), p2 = Native.Get<int>(task, "Param2");
        string p4 = Scalar(task, "Param4");
        return value switch {
            "SurviveOrLoseMatch" => "存活并达到第 " + p1 + " 回合时成功；在此之前失败则任务失败。PERFECT 模式按已完成回合判断（当前行动玩家的有效进度可能少 1 回合），且所有仍存活的帝国玩家都达到目标回合后才成功。",
            "OtherDie" => "玩家存活时，已死亡的帝国玩家数加仍存活的同队帝国玩家数达到目标即成功；Param1=" + p1 + (p1 < 0 ? "，目标按帝国玩家总数 + Param1 计算。" : "，目标为 Param1。"),
            "ScoreWin" => "从第 5 回合起计算分数排名（1 为最高；只有分数严格更高的帝国玩家计入前方名次）；排名 ≤ Param1（" + p1 + "）时成功。",
            "ScoreValue" => "玩家总分达到 Param1（" + p1 + "）时成功。",
            "HasCityCount" => "玩家城市数量达到 Param1（" + p1 + "）时成功。",
            "HasUnitCount" => "玩家拥有 UnitType=" + p4 + " 的单位数量达到 Param1（" + p1 + "）时成功。",
            "HasLevelCityCount" => "玩家等级 ≥ Param1（" + p1 + "）的城市数量达到 Param2（" + p2 + "）时成功。",
            "HasCoinCount" => "玩家金币数量达到 Param1（" + p1 + "）时成功。",
            "HasExploreCount" => "玩家阵营累计探索的迷雾格数量达到 Param1（" + p1 + "）时成功。",
            "None" => "任务类型未配置，具体条件未知。",
            _ => "未知任务类型，具体条件未知。"
        };
    }
    static string LimitDescription(string value) => value switch {
        "PlayerCannotLearnTech" => "玩家不能学习科技。", "PlayerCannotSelectHero" => "玩家不能选择英雄。",
        "AICannotLearnTech" => "AI不能学习科技。", "AICannotBuildOrUpgradeCity" => "AI不能建造或升级城市。",
        "AIMove3RoundsPerAction" => "AI每次行动移动3回合。", "PlayerCannotTrainUnit" => "玩家不能训练单位。",
        "PlayerOnlyShowT2AndT22Tech" => "玩家只显示T2和T22科技。", "PlayerCannotNextTurn" => "玩家不能结束回合。",
        "None" => "无额外规则限制。", _ => "未知限制：" + value
    };
    static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }
    static void WriteAtomic<T>(string path, T value)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); string tmp=path+".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json)); if (File.Exists(path)) File.Replace(tmp,path,null); else File.Move(tmp,path); }
}
