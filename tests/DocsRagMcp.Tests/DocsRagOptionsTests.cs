using Microsoft.Extensions.Configuration;

namespace DocsRagMcp.Tests;

public class DocsRagOptionsTests
{
    [Fact]
    public void Defaults_to_a_local_ollama_model()
    {
        var options = Parse();

        Assert.Equal(EmbeddingProvider.Ollama, options.Provider);
        Assert.Equal("nomic-embed-text", options.Model);
        Assert.Equal("http://localhost:11434", options.Endpoint);
        Assert.Equal(DocsRagOptions.DefaultDatabasePath, options.DatabasePath);
        Assert.Empty(options.AllowedPaths);
    }

    [Theory]
    [InlineData(null, SearchMode.Hybrid)]
    [InlineData("hybrid", SearchMode.Hybrid)]
    [InlineData("VECTOR", SearchMode.Vector)]
    public void Search_mode_defaults_to_hybrid(string? value, SearchMode expected)
    {
        var options = value is null ? Parse() : Parse(("SEARCH_MODE", value));

        Assert.Equal(expected, options.SearchMode);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("false", false)]
    [InlineData("OFF", false)]
    [InlineData("0", false)]
    [InlineData("yes", true)]
    public void Watching_is_on_unless_turned_off(string? value, bool expected)
    {
        var options = value is null ? Parse() : Parse(("WATCH", value));

        Assert.Equal(expected, options.Watch);
    }

    [Fact]
    public void Azure_needs_an_endpoint_and_a_deployment_name()
    {
        Assert.Throws<DocsRagConfigurationException>(() => Parse(("PROVIDER", "azure"), ("MODEL", "embeddings")));
        Assert.Throws<DocsRagConfigurationException>(() => Parse(("PROVIDER", "azure"), ("ENDPOINT", "https://x.openai.azure.com/")));

        var options = Parse(("PROVIDER", "azure"), ("ENDPOINT", "https://x.openai.azure.com/"), ("MODEL", "embeddings"));
        Assert.Equal(EmbeddingProvider.AzureOpenAI, options.Provider);
        Assert.Null(options.ApiKey);
    }

    [Fact]
    public void OpenAI_needs_an_api_key_unless_a_compatible_endpoint_is_used()
    {
        Assert.Throws<DocsRagConfigurationException>(() => Parse(("PROVIDER", "openai")));

        Assert.Equal("text-embedding-3-small", Parse(("PROVIDER", "openai"), ("API_KEY", "sk-test")).Model);
        Assert.Null(Parse(("PROVIDER", "openai"), ("ENDPOINT", "http://localhost:1234/v1")).ApiKey);
    }

    [Fact]
    public void Reranking_is_off_unless_an_endpoint_is_set()
    {
        var off = Parse();
        Assert.Null(off.RerankEndpoint);
        Assert.Equal(20, off.RerankCandidates);
        Assert.Equal(TimeSpan.FromSeconds(30), off.RerankTimeout);

        var on = Parse(
            ("RERANK_ENDPOINT", "http://localhost:8012/v1/rerank"),
            ("RERANK_MODEL", "bge-reranker-v2-m3"),
            ("RERANK_API_KEY", "secret"),
            ("RERANK_CANDIDATES", "30"),
            ("RERANK_TIMEOUT", "45"));
        Assert.Equal("http://localhost:8012/v1/rerank", on.RerankEndpoint);
        Assert.Equal("bge-reranker-v2-m3", on.RerankModel);
        Assert.Equal("secret", on.RerankApiKey);
        Assert.Equal(30, on.RerankCandidates);
        Assert.Equal(TimeSpan.FromSeconds(45), on.RerankTimeout);
    }

    [Theory]
    [InlineData("CHUNK_SIZE", "100")]
    [InlineData("CHUNK_OVERLAP", "900")]
    [InlineData("CHUNK_SIZE", "large")]
    [InlineData("PROVIDER", "gemini")]
    [InlineData("SEARCH_MODE", "keyword")]
    [InlineData("WATCH", "sometimes")]
    [InlineData("ENDPOINT", "not a url")]
    [InlineData("RERANK_ENDPOINT", "localhost:8012/rerank")]
    [InlineData("RERANK_ENDPOINT", "ftp://example.com/rerank")]
    [InlineData("RERANK_CANDIDATES", "0")]
    [InlineData("RERANK_CANDIDATES", "101")]
    [InlineData("RERANK_CANDIDATES", "many")]
    [InlineData("RERANK_TIMEOUT", "0")]
    [InlineData("RERANK_TIMEOUT", "many")]
    public void Invalid_settings_are_rejected_with_a_clear_message(string key, string value)
    {
        var error = Assert.Throws<DocsRagConfigurationException>(() => Parse((key, value)));

        Assert.Contains("DOCSRAG_", error.Message);
    }

    [Fact]
    public void Allowed_paths_use_the_platform_path_separator()
    {
        var first = Path.Combine(Path.GetTempPath(), "docs");
        var second = Path.Combine(Path.GetTempPath(), "wiki");

        var options = Parse(("ALLOWED_PATHS", $"{first}{Path.PathSeparator}{second}"));

        Assert.Equal([PathUtils.Normalize(first), PathUtils.Normalize(second)], options.AllowedPaths);
    }

    [Fact]
    public void Command_line_switches_are_supported()
    {
        var config = DocsRagOptions.BuildConfiguration(
        [
            "--provider", "openai", "--model", "text-embedding-3-large", "--chunk-size", "800",
            "--rerank-endpoint", "http://localhost:8012/v1/rerank", "--rerank-model", "bge-reranker-v2-m3", "--rerank-candidates", "10",
            "--rerank-timeout", "45",
        ]);

        Assert.Equal("openai", config["PROVIDER"]);
        Assert.Equal("text-embedding-3-large", config["MODEL"]);
        Assert.Equal("800", config["CHUNK_SIZE"]);
        Assert.Equal("http://localhost:8012/v1/rerank", config["RERANK_ENDPOINT"]);
        Assert.Equal("bge-reranker-v2-m3", config["RERANK_MODEL"]);
        Assert.Equal("10", config["RERANK_CANDIDATES"]);
        Assert.Equal("45", config["RERANK_TIMEOUT"]);
    }

    private static DocsRagOptions Parse(params (string Key, string Value)[] settings) =>
        DocsRagOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build());
}
