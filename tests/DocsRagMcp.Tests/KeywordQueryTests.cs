using DocsRagMcp.Storage;

namespace DocsRagMcp.Tests;

public sealed class KeywordQueryTests
{
    [Fact]
    public void Keeps_the_meaningful_words_of_a_question()
    {
        var query = KeywordQuery.Build("How do I protect my HTTP server with OAuth?");

        Assert.Equal("\"protect\" OR \"http\" OR \"server\" OR \"oauth\"", query);
    }

    [Fact]
    public void Splits_on_punctuation_and_removes_duplicates()
    {
        var query = KeywordQuery.Build("Signed-in users, signed-in USERS!");

        Assert.Equal("\"signed\" OR \"users\"", query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("How do I?")]
    [InlineData("?!.,")]
    public void Returns_null_when_nothing_is_worth_searching_for(string text)
    {
        Assert.Null(KeywordQuery.Build(text));
    }

    [Fact]
    public void Quotes_words_that_FTS5_would_read_as_operators()
    {
        var query = KeywordQuery.Build("cats NEAR dogs AND NOT birds");

        Assert.Equal("\"cats\" OR \"near\" OR \"dogs\" OR \"birds\"", query);
    }

    [Fact]
    public void Keeps_letters_and_digits_from_any_language()
    {
        Assert.Equal("\"café\" OR \"2026\"", KeywordQuery.Build("café 2026"));
    }
}
