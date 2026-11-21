using System.Security.Cryptography;
using System.Text.Json;

namespace Tohotopia;

public sealed partial class OAuth
{
    static readonly object credentialGate = new();
    static readonly Dictionary<(string Root, string Provider), long> generations = new();
    record Lease(string Root, string Provider, long Generation);
    static string CredentialRoot(string root) => Path.Combine(root, "credentials");
    static string CredentialPath(string provider, string suffix) => Path.Combine(CredentialRoot(Files.Root), provider + suffix);
    static string Provider(string provider)
    {
        if (string.IsNullOrEmpty(provider) || provider.Length > 128 || provider.Contains("..") || provider.StartsWith('.') || provider.EndsWith('.')
            || provider.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("提供商标识无效");
        return ModelCatalog.Driver(provider);
    }
    static Lease Capture(string provider)
    {
        lock (credentialGate)
        {
            string root = Path.GetFullPath(Files.Root);
            return new(root, provider, generations.GetValueOrDefault((root, provider)));
        }
    }
    static bool Current(Lease owner) => owner.Root == Path.GetFullPath(Files.Root)
        && generations.GetValueOrDefault((owner.Root, owner.Provider)) == owner.Generation;
    void Invalidate(string provider, string keep = "")
    {
        var owner = Capture(provider);
        generations[(owner.Root, provider)] = owner.Generation + 1;
        foreach (var item in logins.Where(item => item.Value.Provider == provider && item.Key != keep).ToArray())
            logins.TryRemove(item.Key, out _);
        foreach (var item in pending.Where(item => item.Value.Provider == provider && item.Key != keep).ToArray())
            if (pending.TryRemove(item.Key, out var flow)) flow.Cancellation.Cancel();
    }
    void Commit(Lease owner, Token token, string client = "", string login = "")
    {
        lock (credentialGate)
        {
            if (!Current(owner)) throw new OperationCanceledException("连接已被移除或替换，请重新授权");
            if (string.IsNullOrWhiteSpace(token.Access) || token.Access.Any(char.IsControl))
                throw new InvalidOperationException("授权结果缺少有效访问令牌；已有凭据保留");
            SaveCore(owner.Provider, token);
            if (client.Length > 0) File.WriteAllText(Path.Combine(CredentialRoot(owner.Root), "openai-client-id"), client);
            if (login.Length > 0)
            {
                Invalidate(owner.Provider, login);
                if (logins.TryGetValue(login, out var state))
                    logins[login] = state with { Status = "connected", Message = "已连接", Code = "", Url = "" };
                pending.TryRemove(login, out _);
            }
        }
    }
    void Fail(string id, Pending flow, string message)
    {
        lock (credentialGate)
        {
            if (!Current(flow.Owner) || !logins.TryGetValue(id, out var state)) return;
            pending.TryRemove(id, out _);
            logins[id] = state with { Status = "failed", Message = message, Code = "", Url = "" };
        }
    }
    public CredentialInfo Credential(string provider)
    {
        string p = Provider(provider);
        lock (credentialGate)
        {
            if (!File.Exists(CredentialPath(p, ".token"))) return new(p, "", "", "", false);
            try
            {
                var token = Load(p);
                return new(p, token.AuthType, token.BaseUrl, token.WorkspaceId, !string.IsNullOrWhiteSpace(token.Access));
            }
            catch (Exception e) when (e is CryptographicException or JsonException or IOException)
            {
                throw new InvalidOperationException("无法读取已保存凭据，请移除后重新配置");
            }
        }
    }
    public void Delete(string provider)
    {
        string p = Provider(provider);
        lock (credentialGate)
        {
            Invalidate(p);
            foreach (string file in new[] { p + ".token.tmp", p + "-auth-status.json", p + "-auth-status.json.tmp" })
                DeleteFile(Path.Combine(CredentialRoot(Files.Root), file));
            if (p == "openai")
                foreach (string file in new[] { "openai-client-id", "openai-host-id" })
                    DeleteFile(Path.Combine(CredentialRoot(Files.Root), file));
            DeleteFile(CredentialPath(p, ".token"));
        }
    }
    static void DeleteFile(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.Delete(path);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 59)
                    throw new IOException("凭据文件仍被其他程序占用或无法访问，请稍后重试。账号尚未移除：" + path, e);
                Thread.Sleep(50);
            }
        }
    }
}
