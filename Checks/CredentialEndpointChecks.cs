using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tohotopia;
using Xunit;

static class CredentialEndpointChecks
{
    internal static async Task Run(HttpClient host, string root)
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "models-dev.json")))!.AsObject();
        catalog["deepseek"] = new JsonObject
        {
            ["name"] = "DeepSeek", ["npm"] = "@ai-sdk/openai-compatible", ["api"] = "https://api.deepseek.com",
            ["env"] = new JsonArray("DEEPSEEK_API_KEY"), ["models"] = new JsonObject()
        };
        File.WriteAllText(Path.Combine(root, "models-dev.json"), catalog.ToJsonString());
        using var missing = await host.GetAsync("/auth/deepseek/credential");
        var empty = await missing.Content.ReadFromJsonAsync(HostJson.Type<CredentialInfo>());
        Check(missing.IsSuccessStatusCode && empty == new CredentialInfo("deepseek", "", "", "", false), "GET credential returns the exact empty metadata contract with generated JSON");
        using var added = await host.PostAsJsonAsync("/auth/deepseek/api-key", new ApiKeyRequest("fixture-http-secret"), HostJson.Type<ApiKeyRequest>());
        added.EnsureSuccessStatusCode();
        using var edited = await host.PutAsJsonAsync("/auth/deepseek/api-key", new ApiKeyRequest("", "https://edited.example/v1", "workspace-http"), HostJson.Type<ApiKeyRequest>());
        edited.EnsureSuccessStatusCode();
        using var read = await host.GetAsync("/auth/deepseek/credential");
        string wire = await read.Content.ReadAsStringAsync();
        var info = JsonSerializer.Deserialize(wire, HostJson.Type<CredentialInfo>());
        using var doc = JsonDocument.Parse(wire);
        Check(info == new CredentialInfo("deepseek", "api_key", "https://edited.example/v1", "workspace-http", true)
            && doc.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "authType", "baseUrl", "hasSecret", "provider", "workspaceId" })
            && !wire.Contains("fixture-http-secret"), "PUT blank key updates editable metadata and GET exposes no token or key fields");
        var encrypted = File.ReadAllBytes(CredentialPaths.File(root, "deepseek.token"));
        using var invalid = await host.PostAsJsonAsync("/auth/deepseek/api-key", new ApiKeyRequest("new-key", "http://remote.example/v1"), HostJson.Type<ApiKeyRequest>());
        using var login = await host.PostAsync("/auth/deepseek/start", null);
        Check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity && login.StatusCode == HttpStatusCode.UnprocessableEntity
            && encrypted.SequenceEqual(File.ReadAllBytes(CredentialPaths.File(root, "deepseek.token"))),
            "unsupported subscription and invalid POST return HTTP 422 without replacing the existing DeepSeek key");
        using var alias = await host.PostAsJsonAsync("/auth/xai/api-key", new ApiKeyRequest("fixture-alias"), HostJson.Type<ApiKeyRequest>());
        alias.EnsureSuccessStatusCode();
        var settings = File.ReadAllBytes(Path.Combine(root, "settings.json"));
        using var removed = await host.DeleteAsync("/auth/deepseek");
        using var repeated = await host.DeleteAsync("/auth/deepseek");
        Check(removed.StatusCode == HttpStatusCode.NoContent && repeated.StatusCode == HttpStatusCode.NoContent
            && !File.Exists(CredentialPaths.File(root, "deepseek.token")) && !File.Exists(CredentialPaths.File(root, "deepseek-auth-status.json"))
            && settings.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "settings.json"))),
            "DELETE credential is idempotent HTTP 204 and preserves global settings");
        using var aliasRemoved = await host.DeleteAsync("/auth/xai");
        var aliasInfo = await host.GetFromJsonAsync("/auth/grok/credential", HostJson.Type<CredentialInfo>());
        Check(aliasRemoved.StatusCode == HttpStatusCode.NoContent && aliasInfo == new CredentialInfo("grok", "", "", "", false),
            "DELETE provider alias removes only its canonical credential");
        using var auth = await host.PostAsync("/auth/anthropic/start", null);
        auth.EnsureSuccessStatusCode();
        var pending = await auth.Content.ReadFromJsonAsync(HostJson.Type<LoginState>());
        using var deletedLogin = await host.DeleteAsync("/auth/anthropic");
        using var state = await host.GetAsync("/auth/login/" + pending!.Id);
        Check(deletedLogin.StatusCode == HttpStatusCode.NoContent && state.StatusCode == HttpStatusCode.NotFound,
            "DELETE invalidates pending authorization and its login-state endpoint returns HTTP 404");
        using var blank = await host.PutAsJsonAsync("/auth/deepseek/api-key", new ApiKeyRequest(""), HostJson.Type<ApiKeyRequest>());
        Check(blank.StatusCode == HttpStatusCode.UnprocessableEntity && !File.Exists(CredentialPaths.File(root, "deepseek.token")),
            "PUT cannot create an account from an absent blank secret");
    }
    static void Check(bool value, string message) => Assert.True(value, message);
}
