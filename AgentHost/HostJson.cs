using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using TohotopiaShared;

namespace Tohotopia;

// Every host serializer uses this closed set of generated contracts. Unknown
// runtime types fail explicitly instead of falling back to reflection.
public static class HostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = HostJsonContext.Default
    };
    public static readonly JsonSerializerOptions Plain = new() { TypeInfoResolver = HostJsonContext.Default };

    public static JsonTypeInfo<T> Type<T>(JsonSerializerOptions? options = null) =>
        (JsonTypeInfo<T>)(options ?? Options).GetTypeInfo(typeof(T));

    public static string SerializeValue(object? value) => value is null ? "null" :
        JsonSerializer.Serialize(value, Plain.GetTypeInfo(value.GetType()));

    public static JsonNode? Node(object? value) => value is JsonNode node ? node.DeepClone() :
        value is null ? null : JsonSerializer.SerializeToNode(value, Plain.GetTypeInfo(value.GetType()));
}

public record HealthResponse(bool Ready, string Framework, string Version);
internal record CandidatePrompt(string Id, string Name, string Kind, uint UnitId, uint CityId, uint GridId,
    uint TargetUnitId, uint TargetGridId, uint TargetPlayerId, string[] RuleIds, string Description);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(DecisionRequest))]
[JsonSerializable(typeof(Decision))]
[JsonSerializable(typeof(CommunicationRequest))]
[JsonSerializable(typeof(AmbientReply))]
[JsonSerializable(typeof(VisibleSnapshot))]
[JsonSerializable(typeof(RoomConfig))]
[JsonSerializable(typeof(RoomStatus))]
[JsonSerializable(typeof(RoomCommand))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(GameRulesExport))]
[JsonSerializable(typeof(Token))]
[JsonSerializable(typeof(LoginState))]
[JsonSerializable(typeof(OAuthCompletion))]
[JsonSerializable(typeof(ApiKeyRequest))]
[JsonSerializable(typeof(CredentialInfo))]
[JsonSerializable(typeof(AccountStatus[]))]
[JsonSerializable(typeof(List<CatalogProvider>))]
[JsonSerializable(typeof(List<CatalogModel>))]
[JsonSerializable(typeof(PingResult))]
[JsonSerializable(typeof(CandidatePrompt[]))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IDictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(JsonElement[]))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonObject[]))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
internal partial class HostJsonContext : JsonSerializerContext;
