using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TohotopiaShared;
namespace Tohotopia;

public record AgentConfig
{
    public string Provider { get; init; } = "openai";
    public string Model { get; init; } = "";
    public string Reasoning { get; init; } = "default";
    public string MapFormat { get; init; } = "table";
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxDecisionsPerTurn { get; init; } = 80;
    public string ExtraRules { get; init; } = "";
}
public record Candidate(string Id, string Kind, uint UnitId, uint CityId, uint GridId, uint TargetUnitId, uint TargetGridId, uint TargetPlayerId, string Description);
public record DecisionRequest(string RequestId, uint MapId, uint PlayerId, uint Turn, int Sequence, JsonElement Observation, List<Candidate> Candidates, string CivilizationRules, int AiSlot = 0, string Feedback = "");
public record Decision(string RequestId, string CandidateId = "", string Reason = "", string Memory = "", string Message = "", List<PlannedStep>? Actions = null)
{
    [JsonIgnore] public IReadOnlyList<PlannedStep> Steps => Actions ?? [new(CandidateId, Message)];
}
public record LoginState(string Id, string Provider, string Url, string Code, string Status, string Message);
public record OAuthCompletion(string Input);
public record AccountStatus(string Provider, bool Connected, string Status, string Message, DateTimeOffset? Expires = null, bool RefreshAvailable = false, bool Configured = false, string AuthType = "subscription");
public record ApiKeyRequest(string ApiKey, string BaseUrl = "", string WorkspaceId = "");
public record CatalogModel(string Id, string Name, bool Reasoning, string[] Efforts);
public record AuthPromptOption(string Label, string Value);
public record ProviderAuthPrompt(string Type, string Key, string Message, string? Placeholder = null, bool Required = false, AuthPromptOption[]? Options = null, string? DefaultValue = null);
public record ProviderAuthMethod(string Type, string Label, ProviderAuthPrompt[]? Prompts = null);
public record CatalogProvider(string Id, string Name, bool ConnectionSupported, ProviderAuthMethod[] AuthMethods);
public static class Files
{
    public static string Root = "";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    public static AgentConfig Config => File.Exists(Path.Combine(Root, "settings.json")) ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(Path.Combine(Root, "settings.json")), Json)! : new();
    public static void Save(AgentConfig c)
    {
        Directory.CreateDirectory(Root);
        var f = Path.Combine(Root, "settings.json");
        File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(c, Json));
        File.Move(f + ".tmp", f, true);
    }
}
