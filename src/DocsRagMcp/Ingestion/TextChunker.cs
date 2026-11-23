using System.Text;
using System.Text.RegularExpressions;

namespace DocsRagMcp.Ingestion;

/// <summary>
/// Splits text into chunks small enough to embed. It cuts at paragraph breaks where it can,
/// falls back to word boundaries for very long paragraphs, and repeats the end of each chunk
/// at the start of the next one so an idea that spans the cut can still be found.
/// </summary>
public sealed partial class TextChunker
{
    private const string Separator = "\n\n";
    private static readonly char[] Whitespace = [' ', '\n', '\t'];

    private readonly int _maxChars;
    private readonly int _overlapChars;

    public TextChunker(int maxChars = 1200, int overlapChars = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 50);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapChars);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlapChars, maxChars / 2);

        _maxChars = maxChars;
        _overlapChars = overlapChars;
    }

    public IReadOnlyList<string> Split(string text)
    {
        text = text.ReplaceLineEndings("\n").Trim();
        if (text.Length == 0)
        {
            return [];
        }

        if (text.Length <= _maxChars)
        {
            return [text];
        }

        var pieces = ParagraphBreak().Split(text)
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => paragraph.Length > 0)
            .SelectMany(paragraph => paragraph.Length <= _maxChars ? [paragraph] : SplitAtWords(paragraph));

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var piece in pieces)
        {
            if (current.Length > 0 && current.Length + Separator.Length + piece.Length > _maxChars)
            {
                var chunk = current.ToString();
                chunks.Add(chunk);
                current.Clear();

                var overlap = Tail(chunk);
                if (overlap.Length > 0 && overlap.Length + Separator.Length + piece.Length <= _maxChars)
                {
                    current.Append(overlap);
                }
            }

            if (current.Length > 0)
            {
                current.Append(Separator);
            }

            current.Append(piece);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    private IEnumerable<string> SplitAtWords(string paragraph)
    {
        var start = 0;
        while (start < paragraph.Length)
        {
            var length = Math.Min(_maxChars, paragraph.Length - start);

            if (start + length < paragraph.Length)
            {
                var cut = paragraph.LastIndexOfAny(Whitespace, start + length - 1, length);
                if (cut > start)
                {
                    length = cut - start;
                }
            }

            var piece = paragraph.Substring(start, length).Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }

            start += length;
            while (start < paragraph.Length && char.IsWhiteSpace(paragraph[start]))
            {
                start++;
            }
        }
    }

    /// <summary>The last <c>overlapChars</c> of a chunk, starting at a word boundary.</summary>
    private string Tail(string chunk)
    {
        if (_overlapChars == 0 || chunk.Length <= _overlapChars)
        {
            return string.Empty;
        }

        var boundary = chunk.IndexOfAny(Whitespace, chunk.Length - _overlapChars);
        return boundary < 0 ? string.Empty : chunk[(boundary + 1)..].Trim();
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();
}
