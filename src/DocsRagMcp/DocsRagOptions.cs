using Microsoft.Extensions.Configuration;

namespace DocsRagMcp;

public enum EmbeddingProvider
{
    Ollama,
    OpenAI,
    AzureOpenAI,
}

/// <summary>Thrown when the server settings are missing or invalid.</summary>
public sealed class DocsRagConfigurationException(string message) : Exception(message);

/// <summary>
/// Server settings, read from <c>DOCSRAG_*</c> environment variables or command-line switches.
/// API keys are only read from the environment so they don't end up in process lists or shell history.
/// </summary>
public sealed record DocsRagOptions
{
    public const string EnvironmentPrefix = "DOCSRAG_";

    private static readonly Dictionary<string, string> CommandLineSwitches = new()
    {
        ["--provider"] = "PROVIDER",
        ["--model"] = "MODEL",
        ["--endpoint"] = "ENDPOINT",
        ["--database"] = "DATABASE",
        ["--allowed-paths"] = "ALLOWED_PATHS",
        ["--chunk-size"] = "CHUNK_SIZE",
        ["--chunk-overlap"] = "CHUNK_OVERLAP",
    };

    public EmbeddingProvider Provider { get; init; } = EmbeddingProvider.Ollama;

    public required string Model { get; init; }

    public string? Endpoint { get; init; }

    public string? ApiKey { get; init; }

    public required string DatabasePath { get; init; }

    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    public int ChunkSize { get; init; } = 1200;

    public int ChunkOverlap { get; init; } = 200;

    /// <summary>
    /// Identifies the embedding model, so an index is never searched with vectors from a different model.
    /// </summary>
    public string EmbeddingModelId => $"{Provider.ToString().ToLowerInvariant()}/{Model}";

    public static string DefaultDatabasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docs-rag-mcp", "index.db");

    public static IConfiguration BuildConfiguration(string[] args) =>
        new ConfigurationBuilder()
            .AddEnvironmentVariables(EnvironmentPrefix)
            .AddCommandLine(args, CommandLineSwitches)
            .Build();

    public static DocsRagOptions FromConfiguration(IConfiguration config)
    {
        var provider = ParseProvider(Value(config, "PROVIDER"));
        var model = Value(config, "MODEL");
        var endpoint = Value(config, "ENDPOINT");
        var apiKey = Value(config, "API_KEY");

        switch (provider)
        {
            case EmbeddingProvider.Ollama:
                model ??= "nomic-embed-text";
                endpoint ??= "http://localhost:11434";
                break;

            case EmbeddingProvider.OpenAI:
                model ??= "text-embedding-3-small";
                if (apiKey is null && endpoint is null)
                {
                    throw new DocsRagConfigurationException(
                        "DOCSRAG_API_KEY is required for the openai provider.");
                }

                break;

            case EmbeddingProvider.AzureOpenAI:
                if (endpoint is null)
                {
                    throw new DocsRagConfigurationException(
                        "DOCSRAG_ENDPOINT is required for the azure provider, for example https://my-resource.openai.azure.com/.");
                }

                if (model is null)
                {
                    throw new DocsRagConfigurationException(
                        "DOCSRAG_MODEL must be set to the name of your Azure OpenAI embedding deployment.");
                }

                break;

            default:
                throw new DocsRagConfigurationException($"Unsupported provider {provider}.");
        }

        if (endpoint is not null && !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            throw new DocsRagConfigurationException($"DOCSRAG_ENDPOINT '{endpoint}' is not a valid absolute URL.");
        }

        var chunkSize = ParseInt(config, "CHUNK_SIZE", 1200);
        var chunkOverlap = ParseInt(config, "CHUNK_OVERLAP", 200);

        if (chunkSize < 200)
        {
            throw new DocsRagConfigurationException("DOCSRAG_CHUNK_SIZE must be at least 200 characters.");
        }

        if (chunkOverlap < 0 || chunkOverlap >= chunkSize / 2)
        {
            throw new DocsRagConfigurationException(
                "DOCSRAG_CHUNK_OVERLAP must be between 0 and half of DOCSRAG_CHUNK_SIZE.");
        }

        var allowedPaths = (Value(config, "ALLOWED_PATHS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PathUtils.Normalize)
            .ToArray();

        return new DocsRagOptions
        {
            Provider = provider,
            Model = model,
            Endpoint = endpoint,
            ApiKey = apiKey,
            DatabasePath = PathUtils.Normalize(Value(config, "DATABASE") ?? DefaultDatabasePath),
            AllowedPaths = allowedPaths,
            ChunkSize = chunkSize,
            ChunkOverlap = chunkOverlap,
        };
    }

    private static EmbeddingProvider ParseProvider(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "ollama" => EmbeddingProvider.Ollama,
            "openai" => EmbeddingProvider.OpenAI,
            "azure" or "azure-openai" or "azureopenai" => EmbeddingProvider.AzureOpenAI,
            _ => throw new DocsRagConfigurationException(
                $"Unknown DOCSRAG_PROVIDER '{value}'. Use ollama, openai or azure."),
        };

    private static int ParseInt(IConfiguration config, string key, int defaultValue)
    {
        var value = Value(config, key);
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, out var result)
            ? result
            : throw new DocsRagConfigurationException($"DOCSRAG_{key} must be a whole number, but was '{value}'.");
    }

    private static string? Value(IConfiguration config, string key)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
