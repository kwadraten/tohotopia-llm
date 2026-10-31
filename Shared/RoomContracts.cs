using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace TohotopiaShared;

public record PlannedStep(string CandidateId, string Message = "");

public sealed class ActionPlan
{
    public const int MaxSteps = 12;
    readonly List<PlannedStep> steps = new();
    int position;
    public int Count => steps.Count;
    public bool Active => position < steps.Count;
    public PlannedStep Current => steps[position];
    public void Load(IReadOnlyList<PlannedStep> actions)
    {
        Clear();
        if (actions.Count < 1 || actions.Count > MaxSteps)
            throw new InvalidOperationException("动作序列长度无效");
        steps.AddRange(actions);
    }
    public void Advance()
    {
        position++;
        if (!Active) Clear();
    }
    public string Block(string reason)
    {
        var report = $"动作 {Current.CandidateId} 在序列第 {position + 1}/{steps.Count} 步执行受阻：{reason}。后续未执行步骤：[{string.Join(", ", steps.Skip(position + 1).Select(s => s.CandidateId))}]。请根据最新状态与候选重新规划，勿使用旧候选 ID。";
        Clear();
        return report;
    }
    public void Clear()
    {
        steps.Clear();
        position = 0;
    }
}

public record AISeat
{
    public string Provider { get; init; } = "openai";
    public string Model { get; init; } = "";
    public string Reasoning { get; init; } = "default";
    public int TeamId
    {
        get; init;
    }
    public uint Civilization
    {
        get; init;
    }
    public string Name => Model;
}
public record RoomConfig
{
    public string Revision { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "模型对战房间";
    public bool Published
    {
        get; init;
    }
    public bool NativeLobby { get; init; }
    public bool SpectatorMode { get; init; }
    public uint HumanCivilization { get; init; } = 5;
    public int HumanTeamId
    {
        get; init;
    }
    public int Width { get; init; } = 18;
    public int Height { get; init; } = 18;
    public int Seed { get; init; } = 1234567;
    public int WaterType { get; init; } = 2;
    public int GameMode
    {
        get; init;
    }
    public int CityStates { get; init; } = 0;
    public bool BarbarianCamps { get; init; } = true;
    public bool UseModels
    {
        get; init;
    }
    public List<AISeat> AIs { get; init; } = new() { new() { Civilization = 0 }, new() { Civilization = 1 } };
    public void Validate()
    {
        if (NativeLobby && (SpectatorMode || AIs.Count > 4))
            throw new ArgumentException("原生房间需要房主席位，最多可接管 4 个 AI 席位，且不支持观战模式");
        if (AIs.Count < 1 || AIs.Count > 5)
            throw new ArgumentException("AI 数量必须是 1–5");
        if (SpectatorMode && AIs.Count < 2)
            throw new ArgumentException("观战模式至少需要 2 个模型玩家");
        if (SpectatorMode && !UseModels)
            throw new ArgumentException("观战模式必须启用模型控制");
        if (Width < 11 || Width > 36 || Height < 11 || Height > 36)
            throw new ArgumentException("地图尺寸必须是 11–36");
        if (HumanCivilization > 7 || AIs.Exists(a => a.Civilization > 7))
            throw new ArgumentException("文明编号无效");
        if (HumanTeamId < 0 || HumanTeamId > 6 || AIs.Exists(a => a.TeamId < 0 || a.TeamId > 6))
            throw new ArgumentException("队伍编号必须是 0–6，0 表示无队伍");
        if (AIs.Exists(a => string.IsNullOrWhiteSpace(a.Model) || string.IsNullOrWhiteSpace(a.Provider)))
            throw new ArgumentException("请填写模型与提供商");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 40)
            throw new ArgumentException("房间名称需要 1–40 个字符");
        if (WaterType < 0 || WaterType > 5 || GameMode < 0 || GameMode > 2 || CityStates < 0 || CityStates > 5)
            throw new ArgumentException("地图配置无效");
    }
}
public record RoomCommand(string Id, string Action);
public record CommunicationRequest(string RequestId, string SessionId, uint MapId, uint PlayerId, uint Turn, int AiSlot, JsonElement Observation, string[] AvailableChannels, string CivilizationRules);
public record AmbientReply(string RequestId, string Channel = "", string Message = "", string Memory = "");
public record VisibleSnapshot(string SessionId, uint MapId, uint PlayerId, DateTimeOffset CapturedAt, JsonElement Observation)
{
    public bool Matches(string session, uint map, uint player, DateTimeOffset now) =>
        SessionId == session && MapId == map && PlayerId == player
        && CapturedAt <= now.AddSeconds(2) && CapturedAt >= now.AddSeconds(-20);
}
public record RoomStatus(string State, string Message, bool Joined = false, bool Started = false, uint MapId = 0, uint HumanPlayerId = 0, int AIPlayers = 0, int NetMode = 0, bool ModelsEnabled = false, bool SpectatorMode = false);
public static class RoomFiles
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static T? Read<T>(string root, string name) where T : class
    {
        string p = Path.Combine(root, name);
        if (!File.Exists(p))
            return null;
        using var stream = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Json);
    }
    public static void Write<T>(string root, string name, T value)
    {
        Directory.CreateDirectory(root);
        string p = Path.Combine(root, name);
        string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            // Move(overwrite: true) cannot replace an open destination on Windows,
            // even when its readers allow FileShare.Delete. Replace can.
            if (File.Exists(p)) File.Replace(tmp, p, null);
            else
            {
                try { File.Move(tmp, p); }
                catch (IOException) when (File.Exists(p)) { File.Replace(tmp, p, null); }
            }
        }
        finally
        {
            try { File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
