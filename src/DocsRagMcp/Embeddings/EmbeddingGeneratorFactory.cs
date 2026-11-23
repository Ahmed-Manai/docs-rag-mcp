using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace DocsRagMcp.Embeddings;

/// <summary>
/// Creates the embedding client for the configured provider. Everything else in the server only
/// sees <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>, so adding a provider only touches this file.
/// </summary>
public static class EmbeddingGeneratorFactory
{
    public static IEmbeddingGenerator<string, Embedding<float>> Create(DocsRagOptions options) =>
        options.Provider switch
        {
            EmbeddingProvider.Ollama => new OllamaApiClient(new Uri(options.Endpoint!), options.Model),
            EmbeddingProvider.OpenAI => CreateOpenAI(options),
            EmbeddingProvider.AzureOpenAI => CreateAzureOpenAI(options),
            _ => throw new DocsRagConfigurationException($"Unsupported provider {options.Provider}."),
        };

    private static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAI(DocsRagOptions options)
    {
        // A custom endpoint lets the server talk to any OpenAI-compatible API, such as LM Studio or vLLM.
        var clientOptions = options.Endpoint is null ? new OpenAIClientOptions() : new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) };
        var credential = new ApiKeyCredential(options.ApiKey ?? "not-needed");

        return new OpenAIClient(credential, clientOptions)
            .GetEmbeddingClient(options.Model)
            .AsIEmbeddingGenerator();
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateAzureOpenAI(DocsRagOptions options)
    {
        var endpoint = new Uri(options.Endpoint!);

        // Without an API key, sign in with Microsoft Entra ID (az login, managed identity, Visual Studio, ...).
        var client = options.ApiKey is null
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(endpoint, new AzureKeyCredential(options.ApiKey));

        return client.GetEmbeddingClient(options.Model).AsIEmbeddingGenerator();
    }
}
