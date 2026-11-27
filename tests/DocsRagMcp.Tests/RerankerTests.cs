using DocsRagMcp.Reranking;
using DocsRagMcp.Storage;
using Microsoft.Extensions.Logging;

namespace DocsRagMcp.Tests;

public sealed class RerankerTests
{
    private static readonly SearchHit[] Hits =
    [
        Hit("a.md", "First", 0.9f),
        Hit("b.md", "Second", 0.8f),
        Hit("c.md", "Third", 0.7f),
        Hit("d.md", "Fourth", 0.6f),
    ];

    private readonly ListLogger<Reranker> _logger = new();

    [Fact]
    public async Task The_best_results_are_reordered_by_the_model_and_keep_their_cosine_score()
    {
        var client = new FakeRerankClient { Score = (_, _) => [0.1, 0.2, 0.9, 0.4] };

        var reranked = await Create(client).RerankAsync("question", Hits, topK: 3, CancellationToken.None);

        Assert.Equal(["c.md", "d.md", "b.md"], reranked.Select(hit => Path.GetFileName(hit.Path)));
        Assert.Equal([0.7f, 0.6f, 0.8f], reranked.Select(hit => hit.Score));
    }

    [Fact]
    public async Task The_model_sees_the_file_name_and_heading_with_each_passage()
    {
        var client = new FakeRerankClient();

        await Create(client).RerankAsync("question", Hits.Take(2).ToList(), topK: 2, CancellationToken.None);

        var (query, documents) = Assert.Single(client.Calls);
        Assert.Equal("question", query);
        Assert.Equal(["a.md > First\n\nText of a.md", "b.md > Second\n\nText of b.md"], documents);
    }

    [Fact]
    public async Task Ties_and_passages_the_model_did_not_score_keep_the_search_order()
    {
        var client = new FakeRerankClient { Score = (_, _) => [null, 0.5, null, 0.5] };

        var reranked = await Create(client).RerankAsync("question", Hits, topK: 4, CancellationToken.None);

        Assert.Equal(["b.md", "d.md", "a.md", "c.md"], reranked.Select(hit => Path.GetFileName(hit.Path)));
    }

    [Fact]
    public async Task A_failing_model_keeps_the_search_order_and_logs_why()
    {
        var client = new FakeRerankClient { Score = (_, _) => throw new HttpRequestException("Connection refused") };

        var reranked = await Create(client).RerankAsync("question", Hits, topK: 2, CancellationToken.None);

        Assert.Equal(["a.md", "b.md"], reranked.Select(hit => Path.GetFileName(hit.Path)));
        var (level, message) = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal("Reranking failed, so the results keep their hybrid order: Connection refused", message);
    }

    [Fact]
    public async Task A_model_that_returns_the_wrong_number_of_scores_is_ignored()
    {
        var client = new FakeRerankClient { Score = (_, _) => [0.1, 0.9] };

        var reranked = await Create(client).RerankAsync("question", Hits, topK: 4, CancellationToken.None);

        Assert.Equal(Hits, reranked);
        Assert.Contains("Expected 4 scores but got 2", Assert.Single(_logger.Entries).Message);
    }

    [Fact]
    public async Task A_slow_model_is_given_up_on_after_the_timeout()
    {
        var client = new FakeRerankClient { Delay = TimeSpan.FromSeconds(30), Score = (_, _) => [0.1, 0.2, 0.3, 0.4] };
        var reranker = new Reranker(client, Options(), _logger) { Timeout = TimeSpan.FromMilliseconds(100) };

        var reranked = await reranker.RerankAsync("question", Hits, topK: 4, CancellationToken.None);

        Assert.Equal(Hits, reranked);
        Assert.EndsWith("no answer within 0.1 seconds", Assert.Single(_logger.Entries).Message);
    }

    [Fact]
    public async Task Cancelling_the_search_is_not_treated_as_a_failure()
    {
        var client = new FakeRerankClient { Delay = TimeSpan.FromSeconds(30) };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(client).RerankAsync("question", Hits, topK: 4, cancellation.Token));
        Assert.Empty(_logger.Entries);
    }

    [Theory]
    [InlineData(5, 20, 20)]
    [InlineData(20, 10, 20)]
    [InlineData(3, 3, 3)]
    public void At_least_topK_candidates_are_reranked(int topK, int candidates, int expected) =>
        Assert.Equal(expected, Create(new FakeRerankClient(), candidates).CandidatesFor(topK));

    [Fact]
    public async Task Without_a_model_nothing_changes()
    {
        var reranker = new Reranker(null, Options(), _logger);

        Assert.False(reranker.IsEnabled);
        Assert.Equal(5, reranker.CandidatesFor(5));
        Assert.Equal(Hits.Take(2), await reranker.RerankAsync("question", Hits, topK: 2, CancellationToken.None));
    }

    private Reranker Create(IRerankClient client, int candidates = 20) => new(client, Options(candidates), _logger);

    private static DocsRagOptions Options(int candidates = 20) => new()
    {
        Model = "fake-model",
        DatabasePath = "index.db",
        RerankEndpoint = "http://localhost:8012/v1/rerank",
        RerankCandidates = candidates,
    };

    private static SearchHit Hit(string file, string location, float score) =>
        new(Path.Combine(Path.GetTempPath(), "docs", file), location, $"Text of {file}", score);
}
