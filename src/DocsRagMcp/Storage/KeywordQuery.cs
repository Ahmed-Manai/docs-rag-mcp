using System.Text;
using System.Text.RegularExpressions;

namespace DocsRagMcp.Storage;

/// <summary>
/// Turns a question written in plain language into an SQLite FTS5 query: the meaningful words,
/// each quoted so FTS5 never reads them as operators, joined with OR. Ranking by BM25 then puts
/// passages that contain more of the rarer words first.
/// </summary>
public static partial class KeywordQuery
{
    // Words that appear in almost every question and would match almost every passage.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "about", "after", "all", "also", "am", "an", "and", "any", "are", "as", "at",
        "be", "been", "before", "being", "between", "both", "but", "by",
        "can", "could", "did", "do", "does", "doing", "done", "during",
        "each", "either", "else", "even", "every", "for", "from",
        "get", "gets", "getting", "give", "go", "got",
        "had", "has", "have", "having", "he", "her", "here", "him", "his", "how",
        "i", "if", "in", "into", "is", "it", "its", "itself", "just",
        "let", "like", "make", "makes", "may", "me", "might", "more", "most", "much", "must", "my", "myself",
        "need", "needs", "no", "nor", "not", "now", "of", "off", "on", "once", "one", "only", "or", "other", "our", "out", "over", "own",
        "please", "same", "she", "should", "so", "some", "such",
        "than", "that", "the", "their", "them", "then", "there", "these", "they", "this", "those", "through", "to", "too",
        "under", "until", "up", "us", "use", "used", "using",
        "very", "want", "was", "way", "we", "were", "what", "when", "where", "whether", "which", "while", "who", "whom", "why",
        "will", "with", "within", "without", "would", "you", "your", "yours", "yourself",
    };

    private const int MaxTerms = 32;

    /// <summary>
    /// Returns the FTS5 query for <paramref name="text"/>, or <see langword="null"/> when the text
    /// has no word worth searching for.
    /// </summary>
    public static string? Build(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in Word().Matches(text))
        {
            var term = match.Value.ToLowerInvariant();
            if (term.Length < 2 || StopWords.Contains(term) || !seen.Add(term))
            {
                continue;
            }

            terms.Add(term);
            if (terms.Count == MaxTerms)
            {
                break;
            }
        }

        if (terms.Count == 0)
        {
            return null;
        }

        var query = new StringBuilder();
        foreach (var term in terms)
        {
            if (query.Length > 0)
            {
                query.Append(" OR ");
            }

            // Terms only contain letters and digits, so quoting them is enough to keep FTS5
            // from treating words such as NEAR, AND or NOT as operators.
            query.Append('"').Append(term).Append('"');
        }

        return query.ToString();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
