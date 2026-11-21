using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

sealed class FixtureTransport : HttpMessageHandler
{
    public int ModelCalls, RefreshCalls; public string LastBody = ""; public bool FailRefresh;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == "/api/accounts/oauth/token")
        {
            RefreshCalls++;
            if (FailRefresh)
                return new(System.Net.HttpStatusCode.BadRequest);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"fixture-new\",\"refresh_token\":\"fixture-rotated\",\"expires_in\":3600}")
            };
        }
        if (request.RequestUri.AbsoluteUri != "https://api.openai.com/v1/responses")
            throw new Exception("Unexpected endpoint");
        ModelCalls++;
        LastBody = await request.Content!.ReadAsStringAsync(ct);
        if (ModelCalls == 1)
            return new(System.Net.HttpStatusCode.Unauthorized);
        if (request.Headers.Authorization?.Parameter != "fixture-new")
            throw new Exception("Refresh credential not applied");
        if (request.Headers.Contains("originator") || request.Headers.Contains("version") || request.Headers.Contains("ChatGPT-Account-Id"))
            throw new Exception("Codex headers must not accompany direct subscription tokens");
        return new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"fixture-choice\"}]}]}}\n\n")
        };
    }
}

sealed class CatalogTransport : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsoluteUri != "https://api.openai.com/v1/models" || request.Headers.Contains("originator") || request.Headers.Contains("ChatGPT-Account-Id"))
            throw new Exception("Model catalog must use the direct subscription route");
        Calls++;
        return Task.FromResult(new HttpResponseMessage(Calls == 1 ? System.Net.HttpStatusCode.Unauthorized : System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"models":[{"slug":"visible-model","visibility":"list"},{"slug":"hidden-model","visibility":"hidden"}]}""")
        });
    }
}

sealed class FakeClient : IChatClient
{
    public Queue<string> Responses = new();
    public int Calls; public string LastPrompt = "", Response = """{"requestId":"test-1","candidateId":"a0","reason":"expand"}"""; public bool Fail;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
    {
        Calls++;
        LastPrompt = (o?.Instructions ?? "") + string.Join("\n", m.Select(x => x.Text));
        if (Fail) throw new InvalidOperationException("fixture ping failed: HTTP 429");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Responses.Count > 0 ? Responses.Dequeue() : Response)));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new(ChatRole.Assistant, Response);
    }
    public object? GetService(Type t, object? key = null) => null; public void Dispose()
    {
    }
}
