using System.Security.Cryptography;
using System.Diagnostics;
using Tohotopia;
using TohotopiaShared;
Files.Root = RuntimePaths.PrepareRoot(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_DATA"));
Directory.CreateDirectory(Files.Root);
string tokenPath = Path.Combine(Files.Root, "bridge.token");
if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
string token = File.ReadAllText(tokenPath).Trim();
var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Clear();
    options.SerializerOptions.TypeInfoResolverChain.Add(HostJsonContext.Default);
});
string port = Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_PORT") ?? "17861";
if (!int.TryParse(port, out int portNumber) || portNumber is < 1 or > 65535) throw new ArgumentException("Invalid loopback port");
builder.WebHost.UseUrls("http://127.0.0.1:" + portNumber);
builder.Services.AddSingleton<OAuth>();
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton<Director>();
var app = builder.Build();
app.Use(async (ctx, next) => { if (ctx.Request.Headers.Authorization.ToString() != "Bearer " + token) { ctx.Response.StatusCode = 401; return; } await next(); });
app.MapGet("/health", () => new HealthResponse(true, "Microsoft Agent Framework", "0.1.0"));
app.MapGet("/settings", () => Files.Config);
app.MapPut("/settings", (AgentConfig c) => { if (c.TimeoutSeconds is < 10 or > 600 || c.MaxDecisionsPerTurn is < 1 or > 200) return Results.BadRequest(); Files.Save(c); return Results.Ok(); });
app.MapPost("/decision", async (DecisionRequest r, Director d, CancellationToken ct) => { try { return Results.Ok(await d.Decide(r, ct)); } catch (Exception e) { return Results.Problem(e is OperationCanceledException ? "模型请求已超时" : e.Message, statusCode: 422); } });
app.MapPost("/communication", async (CommunicationRequest r, Director d, CancellationToken ct) => { try { return Results.Ok(await d.Communicate(r, ct)); } catch (Exception e) { return Results.Problem(e is OperationCanceledException ? "通讯请求已超时" : e.Message, statusCode: 422); } });
app.MapPost("/auth/{provider}/start", async (string provider, OAuth a, CancellationToken ct) => { try { return Results.Ok(await a.Start(provider, ct)); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 422); } });
app.MapPost("/auth/{provider}/api-key", (string provider, ApiKeyRequest input, OAuth a) => { try { return Results.Ok(a.ConfigureApiKey(provider, input)); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 422); } });
app.MapGet("/auth/{provider}/credential", (string provider, OAuth a) => { try { return Results.Ok(a.Credential(provider)); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 422); } });
app.MapPut("/auth/{provider}/api-key", (string provider, ApiKeyRequest input, OAuth a) => { try { return Results.Ok(a.UpdateApiKey(provider, input)); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 422); } });
app.MapDelete("/auth/{provider}", (string provider, OAuth a) => { try { a.Delete(provider); return Results.NoContent(); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 422); } });
app.MapGet("/auth/login/{id}", (string id, OAuth a) => { try { return Results.Ok(a.State(id)); } catch (KeyNotFoundException) { return Results.NotFound(); } });
app.MapGet("/auth/{provider}/status", (string provider, OAuth a) => a.Status(provider));
app.MapGet("/auth/accounts", (OAuth a) => a.Accounts());
app.MapPost("/auth/{provider}/ping", async (string provider, Director d, ModelCatalog c, CancellationToken ct) => await d.Ping(provider, c, ct));
app.MapPost("/auth/{provider}/refresh", async (string provider, OAuth a, CancellationToken ct) => { try { await a.Credentials(provider, ct, true); return Results.Ok(a.Status(provider)); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 422); } });
app.MapPost("/auth/login/{id}/complete", async (string id, OAuthCompletion input, OAuth a, CancellationToken ct) => { try { return Results.Ok(await a.Complete(id, input.Input, ct)); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 422); } });
app.MapGet("/catalog/providers", async (ModelCatalog c, CancellationToken ct) => await c.Providers(ct));
app.MapGet("/catalog/{provider}/models", async (string provider, ModelCatalog c, CancellationToken ct) => { try { return Results.Ok(await c.Models(provider, ct)); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 422); } });
// The host must not outlive the game (or a standalone settings window).
// Hold a process handle so PID reuse cannot attach us to an unrelated process.
if (int.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_PID"), out int ownerPid) &&
    long.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_START"), out long ownerStart))
{
    _ = Task.Run(async () =>
    {
        try
        {
            using var owner = Process.GetProcessById(ownerPid);
            if (owner.StartTime.ToUniversalTime().Ticks == ownerStart)
                while (!owner.HasExited) await Task.Delay(250);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        app.Lifetime.StopApplication();
    });
}
app.Run();
