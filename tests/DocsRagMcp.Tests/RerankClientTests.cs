using System.Net;
using System.Text;
using System.Text.Json;
using DocsRagMcp.Reranking;

namespace DocsRagMcp.Tests;

public sealed class RerankClientTests
{
    private static readonly Uri Endpoint = new("http://localhost:8012/v1/rerank");

    [Fact]
    public async Task Sends_a_cohere_style_request()
    {
        var handler = new FakeHandler("""{"results":[]}""");
        var client = new RerankClient(new HttpClient(handler), Endpoint, "bge-reranker-v2-m3", "secret");

        await client.ScoreAsync("how do refunds work", ["first", "second"], CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(Endpoint, handler.Request.RequestUri);
        Assert.Equal("Bearer secret", handler.Request.Headers.Authorization?.ToString());

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("bge-reranker-v2-m3", body.GetProperty("model").GetString());
        Assert.Equal("how do refunds work", body.GetProperty("query").GetString());
        Assert.Equal(["first", "second"], body.GetProperty("documents").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(2, body.GetProperty("top_n").GetInt32());
    }

    [Fact]
    public async Task Leaves_out_the_model_and_key_when_they_are_not_set()
    {
        var handler = new FakeHandler("""{"results":[]}""");
        var client = new RerankClient(new HttpClient(handler), Endpoint, model: null, apiKey: null);

        await client.ScoreAsync("query", ["document"], CancellationToken.None);

        Assert.Null(handler.Request!.Headers.Authorization);
        Assert.False(JsonDocument.Parse(handler.Body!).RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task Maps_the_results_back_to_the_documents_in_any_order()
    {
        var handler = new FakeHandler(
            """{"model":"m","results":[{"index":2,"relevance_score":0.93},{"index":0,"relevance_score":-1.5}],"usage":{"total_tokens":42}}""");
        var client = new RerankClient(new HttpClient(handler), Endpoint, null, null);

        var scores = await client.ScoreAsync("query", ["a", "b", "c"], CancellationToken.None);

        Assert.Equal(new double?[] { -1.5, null, 0.93 }, scores);
    }

    [Fact]
    public async Task An_error_status_is_reported_with_the_response()
    {
        var handler = new FakeHandler("""{"error":"model not found"}""", HttpStatusCode.NotFound);
        var client = new RerankClient(new HttpClient(handler), Endpoint, null, null);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ScoreAsync("query", ["a"], CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains("404", error.Message);
        Assert.Contains("model not found", error.Message);
    }

    [Theory]
    [InlineData("""{"results":[{"index":5,"relevance_score":0.5}]}""")]
    [InlineData("""{"results":[{"index":0}]}""")]
    [InlineData("""{"data":[]}""")]
    public async Task An_unexpected_response_is_rejected(string response)
    {
        var client = new RerankClient(new HttpClient(new FakeHandler(response)), Endpoint, null, null);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ScoreAsync("query", ["a"], CancellationToken.None));
    }

    private sealed class FakeHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
