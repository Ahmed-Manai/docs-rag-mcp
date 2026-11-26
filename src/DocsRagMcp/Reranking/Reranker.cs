using System.Globalization;
using DocsRagMcp.Indexing;
using DocsRagMcp.Storage;
using Microsoft.Extensions.Logging;

namespace DocsRagMcp.Reranking;

/// <summary>
/// Reorders the best search results with a cross-encoder model, which reads the question and each
/// passage together and so judges relevance better than comparing two embeddings. Only the top
/// candidates of the normal ranking are sent, which keeps it fast enough for every search. If the
/// model can't be reached or returns something unexpected, the normal ranking is kept.
/// </summary>
public sealed class Reranker(IRerankClient? client, DocsRagOptions options, ILogger<Reranker> logger)
{
    /// <summary>How long a rerank call may take before the normal ranking is used instead.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsEnabled => client is not null;

    /// <summary>How many results the search should return so that <paramref name="topK"/> can be picked from them.</summary>
    public int CandidatesFor(int topK) => IsEnabled ? Math.Max(topK, options.RerankCandidates) : topK;

    /// <summary>
    /// Returns the <paramref name="topK"/> most relevant of <paramref name="hits"/>, which are in the
    /// order of the normal ranking. Passages keep their cosine similarity as score.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> RerankAsync(
        string query, IReadOnlyList<SearchHit> hits, int topK, CancellationToken cancellationToken)
    {
        if (client is null || hits.Count <= 1)
        {
            return hits.Take(topK).ToList();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            // The model sees the file name and heading too, as the embedding model does when indexing.
            var documents = hits
                .Select(hit => DocumentIndexer.ContextualText(Path.GetFileName(hit.Path), hit.Location, hit.Text))
                .ToList();
            var scores = await client.ScoreAsync(query, documents, timeout.Token);
            if (scores.Count != hits.Count)
            {
                throw new InvalidDataException($"Expected {hits.Count} scores but got {scores.Count}.");
            }

            // Passages the model didn't score go after the ones it did; ties keep the normal order.
            return hits
                .Select((hit, rank) => (Hit: hit, Rank: rank, Score: scores[rank]))
                .OrderByDescending(candidate => candidate.Score.HasValue)
                .ThenByDescending(candidate => candidate.Score ?? 0)
                .ThenBy(candidate => candidate.Rank)
                .Take(topK)
                .Select(candidate => candidate.Hit)
                .ToList();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var reason = ex is OperationCanceledException
                ? string.Create(CultureInfo.InvariantCulture, $"no answer within {Timeout.TotalSeconds:0.#} seconds")
                : ex.Message;
            logger.LogWarning(
                "Reranking failed, so the results keep their {SearchMode} order: {Reason}",
                options.SearchMode.ToString().ToLowerInvariant(),
                reason);
            return hits.Take(topK).ToList();
        }
    }
}
