using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DocsRagMcp.Reranking;

/// <summary>A model that scores how well each document answers a query, such as a cross-encoder.</summary>
public interface IRerankClient
{
    /// <summary>
    /// Returns a relevance score for each document, in the order of <paramref name="documents"/>.
    /// Higher is more relevant. A document the model didn't score gets <see langword="null"/>.
    /// </summary>
    Task<IReadOnlyList<double?>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken);
}

/// <summary>
/// Calls a rerank endpoint with the request and response format that Cohere introduced and that
/// Jina, llama.cpp's <c>llama-server</c>, vLLM, Infinity and LocalAI also use:
/// <c>{ model, query, documents, top_n }</c> in, <c>{ results: [{ index, relevance_score }] }</c> out.
/// </summary>
public sealed class RerankClient(HttpClient httpClient, Uri endpoint, string? model, string? apiKey) : IRerankClient
{
    public async Task<IReadOnlyList<double?>> ScoreAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new RerankRequest(model, query, documents, documents.Count)),
        };

        if (apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"The rerank endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {Shorten(body)}",
                inner: null,
                response.StatusCode);
        }

        var results = (await response.Content.ReadFromJsonAsync<RerankResponse>(cancellationToken))?.Results
            ?? throw new InvalidDataException("The rerank endpoint returned no results.");

        var scores = new double?[documents.Count];
        foreach (var result in results)
        {
            if (result.Index is not { } index || index < 0 || index >= documents.Count || result.RelevanceScore is not { } score)
            {
                throw new InvalidDataException("The rerank endpoint returned a result without a valid index and relevance_score.");
            }

            scores[index] = score;
        }

        return scores;
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : string.Concat(text.AsSpan(0, 200), "...");

    private sealed record RerankRequest(
        [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents,
        [property: JsonPropertyName("top_n")] int TopN);

    private sealed record RerankResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")] int? Index,
        [property: JsonPropertyName("relevance_score")] double? RelevanceScore);
}
