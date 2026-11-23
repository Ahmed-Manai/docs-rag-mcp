using DocsRagMcp.Ingestion;

namespace DocsRagMcp.Tests;

public class TextChunkerTests
{
    [Fact]
    public void Short_text_is_a_single_chunk()
    {
        Assert.Equal(["Hello world."], new TextChunker(100, 20).Split("  Hello world.  "));
    }

    [Fact]
    public void Blank_text_has_no_chunks()
    {
        Assert.Empty(new TextChunker(100, 20).Split(" \n\n \r\n"));
    }

    [Fact]
    public void Long_text_is_split_at_paragraph_breaks_within_the_size_limit()
    {
        var paragraphs = Enumerable.Range(1, 10)
            .Select(i => $"Paragraph {i} " + string.Join(' ', Enumerable.Repeat("word", 10)))
            .ToList();

        var chunks = new TextChunker(200, 0).Split(string.Join("\n\n", paragraphs));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 200));
        Assert.Equal(paragraphs, chunks.SelectMany(chunk => chunk.Split("\n\n")));
    }

    [Fact]
    public void Each_chunk_starts_with_the_end_of_the_previous_one()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"Sentence {i} talks about topic {i} in some detail."));

        var chunks = new TextChunker(120, 40).Split(text);

        Assert.True(chunks.Count > 2);
        for (var i = 1; i < chunks.Count; i++)
        {
            var overlap = chunks[i].Split("\n\n")[0];
            Assert.True(overlap.Length <= 40, $"Overlap '{overlap}' is longer than 40 characters.");
            Assert.EndsWith(overlap, chunks[i - 1]);
        }
    }

    [Fact]
    public void Paragraph_longer_than_the_limit_is_split_between_words()
    {
        var words = Enumerable.Range(0, 300).Select(i => $"w{i}").ToList();

        var chunks = new TextChunker(100, 0).Split(string.Join(' ', words));

        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 100));
        Assert.Equal(words, chunks.SelectMany(chunk => chunk.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries)));
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(100, 50)]
    [InlineData(100, -1)]
    public void Invalid_sizes_are_rejected(int maxChars, int overlapChars)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new TextChunker(maxChars, overlapChars));
    }
}
