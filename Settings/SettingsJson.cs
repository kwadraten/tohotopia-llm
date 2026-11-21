using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tohotopia;
using TohotopiaShared;

namespace Settings;

internal sealed record AccountUi(string Provider);

internal static class SettingsJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, TypeInfoResolver = SettingsJsonContext.Default
    };
    internal static JsonTypeInfo<T> Type<T>(JsonSerializerOptions? options = null) =>
        (JsonTypeInfo<T>)(options ?? Options).GetTypeInfo(typeof(T));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(AccountUi))]
[JsonSerializable(typeof(RoomConfig))]
[JsonSerializable(typeof(RoomStatus))]
[JsonSerializable(typeof(RoomCommand))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(List<AccountStatus>))]
[JsonSerializable(typeof(List<CatalogProvider>))]
[JsonSerializable(typeof(List<CatalogModel>))]
[JsonSerializable(typeof(LoginState))]
[JsonSerializable(typeof(OAuthCompletion))]
[JsonSerializable(typeof(ApiKeyRequest))]
[JsonSerializable(typeof(CredentialInfo))]
[JsonSerializable(typeof(PingResult))]
internal partial class SettingsJsonContext : JsonSerializerContext;
