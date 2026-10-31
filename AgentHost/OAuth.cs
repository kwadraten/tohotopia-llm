using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
namespace Tohotopia;

public record Token(string Access, string Refresh, DateTimeOffset Expires, string AccountId = "", string ClientId = "", string Scope = "", string IdToken = "", string AuthType = "subscription", string BaseUrl = "", string WorkspaceId = "");
public sealed class OAuth
{
    readonly HttpClient http;
    readonly ConcurrentDictionary<string, LoginState> logins = new();
    readonly ConcurrentDictionary<string, Pending> pending = new();
    readonly SemaphoreSlim refreshLock = new(1, 1);
    record Pending(string Provider, string State, string Verifier, string Redirect, DateTimeOffset Deadline, string Nonce = "");
    record Spec(string Client, string Device, string Token, string Scope);
    static ProviderAuthMethod ApiKeyMethod(params ProviderAuthPrompt[] prompts) => new("api", "填写 API Key", prompts);
    static ProviderAuthMethod SubscriptionMethod(string label) => new("oauth", label);
    public static ProviderAuthMethod[] AuthMethods(string provider)
    {
        static ProviderAuthPrompt Key() => new("secret", "apiKey", "API Key", Required: true);
        return ModelCatalog.Driver(provider) switch
        {
            "openai" => [SubscriptionMethod("ChatGPT Plus/Pro"), ApiKeyMethod(Key())],
            "anthropic" => [SubscriptionMethod("Claude 订阅账号"), ApiKeyMethod(Key(), new("text", "workspaceId", "Workspace ID（可选）"))],
            "grok" => [SubscriptionMethod("xAI 订阅账号"), ApiKeyMethod(Key())],
            "kimi" => [SubscriptionMethod("Kimi Code Plan")],
            "moonshotai" => [ApiKeyMethod(Key(), new("select", "baseUrl", "接口区域", Options: [new("国际站（api.moonshot.ai）", "https://api.moonshot.ai/v1"), new("中国站（api.moonshot.cn）", "https://api.moonshot.cn/v1")]))],
            _ => ProviderConnections.AuthMethods(provider)
        };
    }
    public OAuth() : this(new HttpClient() { Timeout = TimeSpan.FromSeconds(30) }) { }
    public OAuth(HttpClient client)
    {
        http = client;
    }
    static Spec SpecFor(string p) => p switch
    {
        "openai" => new("dynamic_agent_client", "", "https://auth.openai.com/api/accounts/oauth/token", "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct"),
        "anthropic" => new("9d1c250a-e61b-44d9-88ed-5944d1962f5e", "", "https://platform.claude.com/v1/oauth/token", "user:profile user:inference"),
        "grok" => new("b1a00492-073a-47ea-816f-4c329264a828", "https://auth.x.ai/oauth2/device/code", "https://auth.x.ai/oauth2/token", "openid profile email offline_access grok-cli:access api:access"),
        "kimi" => new("17e5f671-d194-4dfb-9706-5516cb48c098", "https://auth.kimi.com/api/oauth/device_authorization", "https://auth.kimi.com/api/oauth/token", ""),
        _ => throw new ArgumentException("未知提供商")
    };
    public LoginState State(string id)
    {
        if (!logins.TryGetValue(id, out var s))
            throw new KeyNotFoundException();
        if (s.Status == "pending" && pending.TryGetValue(id, out var p) && p.Deadline < DateTimeOffset.UtcNow)
        {
            pending.TryRemove(id, out _);
            return logins[id] = s with
            {
                Status = "failed",
                Message = "授权已过期，请重新登录"
            };
        }
        return s;
    }
    public bool CanInfer(string p) => Status(p).Connected;
    public static bool SupportsInference(string provider) => ModelCatalog.Driver(provider) is "openai" or "anthropic" or "grok" or "kimi" or "moonshotai"
        || ProviderConnections.TryApiDefinition(provider, out _);
    public AccountStatus Status(string p)
    {
        p = ModelCatalog.Driver(p);
        if (AuthMethods(p).Length == 0)
            throw new ArgumentException("该提供商没有可用的认证方式");
        if (!File.Exists(Path.Combine(Files.Root, p + ".token")))
            return new(p, false, "not_configured", "尚未配置", Configured: false);
        try
        {
            var t = Load(p);
            bool refresh = !string.IsNullOrEmpty(t.Refresh);
            if (string.IsNullOrWhiteSpace(t.Access))
                return new(p, false, "reauthorize", "已保存凭据缺少访问令牌", t.Expires, refresh, true, t.AuthType);
            if (t.AuthType == "api_key")
                return new(p, SupportsInference(p), "configured", SupportsInference(p) ? "API Key 已加密保存" : "API Key 已加密保存；该提供商的专用模型请求协议尚未接入，暂不可用于 AI 席位。", null, false, true, "api_key");
            if (p == "openai" && !t.Scope.Split(' ').Contains("chatgpt.tokens.use.direct"))
                return new(p, false, "reauthorize", "已保存旧授权；需要一次新的 ChatGPT 套餐授权", t.Expires, refresh, true, t.AuthType);
            var notice = TohotopiaShared.RoomFiles.Read<AccountStatus>(Files.Root, p + "-auth-status.json");
            if (notice?.Status == "refresh_failed")
                return notice with
                {
                    Connected = t.Expires > DateTimeOffset.UtcNow,
                    Expires = t.Expires,
                    RefreshAvailable = refresh,
                    Configured = true,
                    AuthType = t.AuthType
                };
            bool expired = t.Expires <= DateTimeOffset.UtcNow;
            return new(p, !expired || refresh, expired ? (refresh ? "refresh_pending" : "reauthorize") : "connected", expired ? (refresh ? "已保存登录；调用前将自动刷新，无需重复登录" : "授权已过期且没有刷新令牌，需要重新登录") : "已登录；凭据已加密持久化", t.Expires, refresh, true, t.AuthType);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException) { return new(p, false, "credential_error", "无法读取已保存凭据（当前 Windows 用户或文件不匹配）", Configured: true); }
    }
    public AccountStatus[] Accounts() => new[] { "openai", "anthropic", "kimi", "grok", "moonshotai" }
        .Concat(ProviderConnections.ApiKeyProviderIds).Select(ModelCatalog.Driver).Distinct().Select(Status).ToArray();
    public string ApiBaseUrl(string provider)
    {
        string p = ModelCatalog.Driver(provider);
        if (!ProviderConnections.TryApiDefinition(p, out _)) throw new ArgumentException("该提供商不支持 API Key 请求");
        string path = Path.Combine(Files.Root, p + ".token");
        string savedUrl = File.Exists(path) ? Load(p).BaseUrl : "";
        return string.IsNullOrWhiteSpace(savedUrl) ? ProviderConnections.ApiUrl(p) : savedUrl;
    }
    public AccountStatus ConfigureApiKey(string provider, ApiKeyRequest input)
    {
        string p = ModelCatalog.Driver(provider);
        if (!AuthMethods(p).Any(method => method.Type == "api"))
            throw new ArgumentException("该提供商不支持 API Key 连接");
        string apiKey = input.ApiKey?.Trim() ?? "";
        if (apiKey.Length is < 1 or > 4096 || apiKey.Any(char.IsControl))
            throw new ArgumentException("API Key 不能为空，且长度不能超过 4096 个字符");
        string workspaceId = input.WorkspaceId?.Trim() ?? "";
        if (workspaceId.Length > 128 || workspaceId.Any(char.IsControl))
            throw new ArgumentException("Workspace ID 无效");
        string baseUrl = "";
        if (p == "moonshotai")
        {
            string requested = string.IsNullOrWhiteSpace(input.BaseUrl) ? "https://api.moonshot.ai/v1" : input.BaseUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(requested, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/v1" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host is not ("api.moonshot.ai" or "api.moonshot.cn"))
                throw new ArgumentException("Moonshot API 地址必须是 https://api.moonshot.ai/v1 或 https://api.moonshot.cn/v1");
            baseUrl = requested;
        }
        else if (p is not ("openai" or "anthropic" or "grok") && ProviderConnections.TryKeyDefinition(p, out _))
        {
            baseUrl = string.IsNullOrWhiteSpace(input.BaseUrl) ? ProviderConnections.ApiUrl(p) : input.BaseUrl.Trim();
            baseUrl = baseUrl.TrimEnd('/');
            if (baseUrl.Length > 0 || SupportsInference(p))
            {
                if (baseUrl.Contains('{') || baseUrl.Contains('}') || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                    || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                    throw new ArgumentException("请填写有效的 HTTPS API 根地址；本机服务也可使用 HTTP 地址。地址中的占位符需要替换为实际配置。");
            }
        }
        Save(p, new Token(apiKey, "", DateTimeOffset.MaxValue, AuthType: "api_key", BaseUrl: baseUrl, WorkspaceId: workspaceId));
        return Status(p);
    }
    public static string Random() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task<LoginState> Start(string p, CancellationToken ct)
    {
        p = ModelCatalog.Driver(p);
        var spec = SpecFor(p);
        string id = Guid.NewGuid().ToString("N");
        foreach (var existing in logins.Values)
        if (existing.Provider == p && existing.Status == "pending" && pending.TryGetValue(existing.Id, out var active) && active.Deadline > DateTimeOffset.UtcNow)
            return existing;
        if (p is "openai" or "anthropic")
        {
            string state = Random(), verifier = Random();
            TcpListener? listener = null;
            string redirect = "https://platform.claude.com/oauth/code/callback";
            if (p == "openai")
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback";
            }
            var values = new Dictionary<string, string?> { { "client_id", spec.Client }, { "response_type", "code" }, { "redirect_uri", redirect }, { "scope", spec.Scope }, { "code_challenge", Challenge(verifier) }, { "code_challenge_method", "S256" }, { "state", state } };
            string nonce = "";
            if (p == "openai")
            {
                string registration = Path.Combine(Files.Root, "openai-client-id");
                string client = File.Exists(registration) ? File.ReadAllText(registration).Trim() : spec.Client;
                values["client_id"] = client;
                if (client == "dynamic_agent_client")
                    values["agent_name_hint"] = "Tohotopia Model Battle";
                string host = Path.Combine(Files.Root, "openai-host-id");
                if (!File.Exists(host))
                    File.WriteAllText(host, "urn:uuid:" + Guid.NewGuid());
                values["ext_agent_host_id"] = File.ReadAllText(host).Trim();
                values["resource"] = "https://api.openai.com/v1";
                nonce = Random();
                values["nonce"] = nonce;
            }
            else
                values["code"] = "true";
            string url = QueryHelpers.AddQueryString(p == "openai" ? "https://auth.openai.com/api/accounts/authorize" : "https://claude.ai/oauth/authorize", values);
            var login = new LoginState(id, p, url, "", "pending", p == "openai" ? "请在浏览器登录 ChatGPT，授权后自动返回。" : "请在浏览器授权，然后粘贴返回的完整回调地址或 code#state。");
            logins[id] = login;
            pending[id] = new(p, state, verifier, redirect, DateTimeOffset.UtcNow.AddMinutes(10), nonce);
            if (listener != null)
                _ = Listen(id, listener);
            return login;
        }
        var fields = new Dictionary<string, string> { { "client_id", spec.Client } };
        if (spec.Scope != "")
            fields["scope"] = spec.Scope;
        using var response = await Form(p, spec.Device, fields, ct);
        Ensure(response, "设备授权");
        var d = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        string auth = d.TryGetProperty("verification_uri_complete", out var complete) ? complete.GetString()! : d.GetProperty("verification_uri").GetString()!;
        if (!Uri.TryCreate(auth, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidOperationException("Invalid authorization URL");
        var deviceLogin = new LoginState(id, p, auth, d.GetProperty("user_code").GetString()!, "pending", "请在浏览器授权此设备。");
        logins[id] = deviceLogin;
        string device = d.GetProperty("device_code").GetString()!;
        int interval = d.TryGetProperty("interval", out var i) ? i.GetInt32() : 5, lifetime = d.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 300;
        _ = Task.Run(async () => { try { var deadline = DateTimeOffset.UtcNow.AddSeconds(lifetime); while (DateTimeOffset.UtcNow < deadline) { await Task.Delay(TimeSpan.FromSeconds(Math.Max(interval, 1))); using var r = await Form(p, spec.Token, new() { { "client_id", spec.Client }, { "device_code", device }, { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" } }, CancellationToken.None); var b = await r.Content.ReadFromJsonAsync<JsonElement>(); if (r.IsSuccessStatusCode) { Save(p, Parse(b)); logins[id] = deviceLogin with { Status = "connected", Message = "已连接" }; return; } string error = b.TryGetProperty("error", out var err) ? err.GetString()! : "unknown"; if (error == "slow_down") { interval += 5; continue; } if (error != "authorization_pending") throw new InvalidOperationException("授权失败：" + error); } throw new TimeoutException("授权已过期"); } catch (Exception e) { logins[id] = deviceLogin with { Status = "failed", Message = e.Message }; } });
        return deviceLogin;
    }
    async Task Listen(string id, TcpListener listener)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            while (pending.ContainsKey(id))
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string line = await reader.ReadLineAsync(timeout.Token) ?? "";
                string target = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                string? header;
                do
                {
                    header = await reader.ReadLineAsync(timeout.Token);
                } while (!string.IsNullOrEmpty(header));
                var uri = new Uri("http://localhost" + target);
                int status = 400;
                string message = "Invalid OAuth callback.";
                if (uri.AbsolutePath == "/auth/callback")
                {
                    try
                    {
                        await Complete(id, uri.AbsoluteUri, timeout.Token);
                        status = 200;
                        message = "Authorization complete. You can close this window.";
                    }
                    catch (ArgumentException) { message = "OAuth state validation failed."; }
                }
                byte[] body = Encoding.UTF8.GetBytes(message);
                byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
            }
        }
        catch (Exception e) { logins[id] = logins[id] with { Status = "failed", Message = e is OperationCanceledException ? "授权已过期" : e.Message }; }
        finally { listener.Stop(); pending.TryRemove(id, out _); }
    }
    public async Task<LoginState> Complete(string id, string input, CancellationToken ct)
    {
        if (!pending.TryGetValue(id, out var p) || p.Deadline < DateTimeOffset.UtcNow)
            throw new ArgumentException("授权请求不存在或已过期");
        var (code, state) = ParseCallback(input);
        if (state != p.State || string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("OAuth state 不匹配");
        var s = SpecFor(p.Provider);
        string client = s.Client;
        if (p.Provider == "openai")
        {
            string registration = Path.Combine(Files.Root, "openai-client-id");
            if (Uri.TryCreate(input, UriKind.Absolute, out var callback) && QueryHelpers.ParseQuery(callback.Query).TryGetValue("client_id", out var issued) && !string.IsNullOrWhiteSpace(issued))
            {
                client = issued.ToString();
                File.WriteAllText(registration, client);
            }
            else if (File.Exists(registration))
                client = File.ReadAllText(registration).Trim();
            if (client == "dynamic_agent_client")
                throw new InvalidOperationException("授权回调缺少签发的 client_id");
        }
        var fields = new Dictionary<string, string> { { "client_id", client }, { "grant_type", "authorization_code" }, { "code", code }, { "code_verifier", p.Verifier }, { "redirect_uri", p.Redirect } };
        if (p.Provider == "openai")
            fields["resource"] = "https://api.openai.com/v1";
        else
            fields["state"] = state;
        using var response = await Form(p.Provider, s.Token, fields, ct);
        Ensure(response, "令牌交换");
        var b = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var token = Parse(b);
        if (p.Provider == "openai")
        {
            string scope = b.TryGetProperty("scope", out var scopes) ? scopes.GetString() ?? "" : "";
            if (!scope.Split(' ').Contains("chatgpt.tokens.use.direct"))
                throw new InvalidOperationException("未授予使用 ChatGPT 套餐的权限，请重新登录并同意套餐使用授权");
            string identity = b.GetProperty("id_token").GetString()!;
            await VerifyIdentity(identity, client, p.Nonce, ct);
            token = token with
            {
                ClientId = client,
                Scope = scope,
                IdToken = identity,
                AccountId = ""
            };
        }
        Save(p.Provider, token);
        pending.TryRemove(id, out _);
        return logins[id] = logins[id] with
        {
            Status = "connected",
            Message = "已连接",
            Code = "",
            Url = ""
        };
    }
    public static (string Code, string State) ParseCallback(string input)
    {
        if (Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u))
        {
            var q = QueryHelpers.ParseQuery(u.Query);
            return (q["code"].ToString(), q["state"].ToString());
        }
        var parts = input.Trim().Split('#');
        if (parts.Length == 2)
            return (parts[0], parts[1]);
        var query = QueryHelpers.ParseQuery(input);
        return (query["code"].ToString(), query["state"].ToString());
    }
    async Task<HttpResponseMessage> Form(string p, string url, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = p == "anthropic" ? JsonContent.Create(fields) : new FormUrlEncodedContent(fields) };
        req.Headers.UserAgent.ParseAdd("TohotopiaLLM/0.2");
        if (p == "kimi")
        {
            req.Headers.Add("X-Msh-Platform", "kimi_cli");
            req.Headers.Add("X-Msh-Device-Id", Environment.MachineName);
            req.Headers.Add("X-Msh-Device-Name", Environment.MachineName);
        }
        return await http.SendAsync(req, ct);
    }
    static void Ensure(HttpResponseMessage r, string operation)
    {
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation}失败：HTTP {(int)r.StatusCode}");
    }
    static Token Parse(JsonElement b, string refresh = "", string account = "") => new(b.GetProperty("access_token").GetString()!, b.TryGetProperty("refresh_token", out var r) ? r.GetString()! : refresh, DateTimeOffset.UtcNow.AddSeconds(b.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600), Account(b, account));
    static string Account(JsonElement b, string previous)
    {
        try
        {
            string jwt = b.TryGetProperty("id_token", out var id) ? id.GetString()! : b.GetProperty("access_token").GetString()!;
            string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.GetProperty("https://api.openai.com/auth").GetProperty("chatgpt_account_id").GetString() ?? previous;
        }
        catch { return previous; }
    }
    static void Save(string p, Token t)
    {
        Directory.CreateDirectory(Files.Root);
        string path = Path.Combine(Files.Root, p + ".token");
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(t)), null, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true);
        TohotopiaShared.RoomFiles.Write(Files.Root, p + "-auth-status.json", new AccountStatus(p, true, t.AuthType == "api_key" ? "configured" : "connected", t.AuthType == "api_key" ? "API Key 已加密保存" : "登录凭据已加密保存", t.AuthType == "api_key" ? null : t.Expires, t.Refresh.Length > 0, true, t.AuthType));
        TohotopiaShared.AgentEvents.Write(Files.Root, new(DateTimeOffset.UtcNow, "host", "auth_saved", "连接凭据已加密保存", Provider: p));
    }
    Token Load(string p) => JsonSerializer.Deserialize<Token>(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Files.Root, p + ".token")), null, DataProtectionScope.CurrentUser))!;
    public async Task<Token> Credentials(string p, CancellationToken ct, bool force = false)
    {
        p = ModelCatalog.Driver(p);
        if (AuthMethods(p).Length == 0) throw new ArgumentException("该提供商没有可用的认证方式");
        await refreshLock.WaitAsync(ct);
        bool refreshing = false;
        try
        {
            string path = Path.Combine(Files.Root, p + ".token");
            if (!File.Exists(path))
                throw new InvalidOperationException("请先在设置窗口配置 " + p);
            var t = Load(p);
            if (t.AuthType == "api_key")
            {
                if (string.IsNullOrWhiteSpace(t.Access))
                    throw new InvalidOperationException("API Key 为空，请在设置窗口重新填写");
                return t;
            }
            if (p == "openai" && !t.Scope.Split(' ').Contains("chatgpt.tokens.use.direct"))
                throw new InvalidOperationException("请重新登录 ChatGPT 并授予套餐使用权限，旧 Codex 登录不能用于模型对战");
            if (!force && t.Expires > DateTimeOffset.UtcNow.AddMinutes(3))
                return t;
            if (t.Refresh.Length == 0)
                throw new InvalidOperationException("授权已过期且没有刷新令牌，请重新登录");
            refreshing = true;
            var s = SpecFor(p);
            var fields = new Dictionary<string, string> { { "client_id", p == "openai" ? t.ClientId : s.Client }, { "grant_type", "refresh_token" }, { "refresh_token", t.Refresh } };
            if (p == "openai")
                fields["resource"] = "https://api.openai.com/v1";
            using var r = await Form(p, s.Token, fields, ct);
            Ensure(r, "令牌刷新");
            var b = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            var refreshed = Parse(b, t.Refresh, t.AccountId);
            t = refreshed with
            {
                ClientId = t.ClientId,
                Scope = b.TryGetProperty("scope", out var scope) ? scope.GetString() ?? t.Scope : t.Scope,
                IdToken = b.TryGetProperty("id_token", out var id) ? id.GetString() ?? t.IdToken : t.IdToken
            };
            Save(p, t);
            return t;
        }
        catch (Exception) { if (refreshing && !ct.IsCancellationRequested) { TohotopiaShared.RoomFiles.Write(Files.Root, p + "-auth-status.json", new AccountStatus(p, false, "refresh_failed", "凭据刷新失败；保留已有登录，可重试检查。")); TohotopiaShared.AgentEvents.Write(Files.Root, new(DateTimeOffset.UtcNow, "host", "auth_refresh_failed", "凭据刷新失败；已有凭据保留", "error", Provider: p)); } throw; }
        finally { refreshLock.Release(); }
    }
    static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }
    public async Task VerifyIdentity(string jwt, string client, string nonce, CancellationToken ct)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("ID token 格式无效");
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        if (header.RootElement.GetProperty("alg").GetString() != "RS256")
            throw new InvalidOperationException("ID token 签名算法无效");
        string kid = header.RootElement.GetProperty("kid").GetString()!;
        using var keys = JsonDocument.Parse(await http.GetStringAsync("https://auth.openai.com/.well-known/jwks.json", ct));
        var key = keys.RootElement.GetProperty("keys").EnumerateArray().First(k => k.GetProperty("kid").GetString() == kid);
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Decode(key.GetProperty("n").GetString()!), Exponent = Decode(key.GetProperty("e").GetString()!) });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("ID token 签名无效");
        var claims = payload.RootElement;
        var aud = claims.GetProperty("aud");
        bool validAudience = aud.ValueKind == JsonValueKind.String ? aud.GetString() == client : aud.EnumerateArray().Any(a => a.GetString() == client);
        if (!validAudience || claims.GetProperty("iss").GetString() != "https://auth.openai.com" || claims.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || claims.GetProperty("nonce").GetString() != nonce || string.IsNullOrWhiteSpace(claims.GetProperty("sub").GetString()))
            throw new InvalidOperationException("ID token 身份、期限或 nonce 校验失败");
    }
    public async Task<string> Access(string p, CancellationToken ct) => (await Credentials(p, ct)).Access;
}

