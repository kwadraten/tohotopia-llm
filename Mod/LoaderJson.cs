using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

// Keep the shared wire contracts identical to AgentHost while using the game's JSON runtime.
namespace TohotopiaMod.Json;

public sealed class JsonSerializerOptions
{
    public JsonSerializerOptions() { }
    public JsonSerializerOptions(JsonSerializerOptions source)
    { PropertyNamingPolicy = source.PropertyNamingPolicy; PropertyNameCaseInsensitive = source.PropertyNameCaseInsensitive; WriteIndented = source.WriteIndented; }
    public JsonNamingPolicy PropertyNamingPolicy { get; set; }
    public bool PropertyNameCaseInsensitive { get; set; }
    public bool WriteIndented { get; set; }
}
public sealed class JsonNamingPolicy { public static readonly JsonNamingPolicy CamelCase = new(); }
public enum JsonValueKind { Undefined, Object, Array, String, Number, True, False, Null }
public sealed class JsonException : Exception { public JsonException(string message, Exception inner) : base(message, inner) { } }
[Newtonsoft.Json.JsonConverter(typeof(ElementConverter))]
public readonly struct JsonElement
{
    internal readonly JToken Token;
    internal JsonElement(JToken token) { Token = token; }
    public JsonValueKind ValueKind => Token == null ? JsonValueKind.Undefined : Token.Type switch
    {
        JTokenType.Object => JsonValueKind.Object, JTokenType.Array => JsonValueKind.Array,
        JTokenType.String => JsonValueKind.String, JTokenType.Integer or JTokenType.Float => JsonValueKind.Number,
        JTokenType.Boolean => Token.Value<bool>() ? JsonValueKind.True : JsonValueKind.False, _ => JsonValueKind.Null
    };
    public JsonElement GetProperty(string name) => TryGetProperty(name, out var value) ? value : throw new KeyNotFoundException(name);
    public bool TryGetProperty(string name, out JsonElement value)
    {
        var token = (Token as JObject)?[name]; value = new(token); return token != null;
    }
    public string GetString() => Token?.Type == JTokenType.Null ? null : Token?.Value<string>();
    public int GetInt32() => Token.Value<int>();
    public int GetArrayLength() => ((JArray)Token).Count;
    public T Deserialize<T>(JsonSerializerOptions options = null) => JsonSerializer.Deserialize<T>(Token.ToString(Formatting.None), options);
    public JsonElement Clone() => new(Token?.DeepClone());
}
internal sealed class ElementConverter : Newtonsoft.Json.JsonConverter
{
    public override bool CanConvert(Type type) => type == typeof(JsonElement);
    public override void WriteJson(JsonWriter writer, object value, Newtonsoft.Json.JsonSerializer serializer)
    { var token = ((JsonElement)value).Token; if (token == null) writer.WriteNull(); else token.WriteTo(writer); }
    public override object ReadJson(JsonReader reader, Type type, object existingValue, Newtonsoft.Json.JsonSerializer serializer) => new JsonElement(JToken.Load(reader));
}
public sealed class JsonDocument : IDisposable
{
    public JsonElement RootElement { get; }
    JsonDocument(JToken token) { RootElement = new(token); }
    public static JsonDocument Parse(string text)
    {
        try { using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None }; return new(JToken.Load(reader)); }
        catch (Newtonsoft.Json.JsonException error) { throw new JsonException(error.Message, error); }
    }
    public void Dispose() { }
}
public static class JsonSerializer
{
    static JsonSerializerSettings Settings(JsonSerializerOptions options) => new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = options?.PropertyNamingPolicy == null ? null : new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
        Formatting = options?.WriteIndented == true ? Formatting.Indented : Formatting.None,
        DateParseHandling = DateParseHandling.None,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Culture = System.Globalization.CultureInfo.InvariantCulture
    };
    public static string Serialize<T>(T value, JsonSerializerOptions options = null)
    {
        try { return JsonConvert.SerializeObject(value, Settings(options)); }
        catch (Newtonsoft.Json.JsonException error) { throw new JsonException(error.Message, error); }
    }
    public static T Deserialize<T>(string text, JsonSerializerOptions options = null)
    {
        try { return JsonConvert.DeserializeObject<T>(text, Settings(options)); }
        catch (Newtonsoft.Json.JsonException error) { throw new JsonException(error.Message, error); }
    }
    public static T Deserialize<T>(Stream stream, JsonSerializerOptions options = null)
    { using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 1024, true); return Deserialize<T>(reader.ReadToEnd(), options); }
    public static JsonElement SerializeToElement<T>(T value, JsonSerializerOptions options = null) => JsonDocument.Parse(Serialize(value, options)).RootElement;
}
