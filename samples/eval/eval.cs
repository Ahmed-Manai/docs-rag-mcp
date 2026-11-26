#:package ModelContextProtocol@2.4.0

// Measures search quality on a set of questions with known answers. Indexes a folder once, then asks
// every question in each search mode and reports how often the expected page comes first (hit@1),
// how often it is in the top 3 (hit@3) and the mean reciprocal rank (MRR).
//
//   dotnet build
//   dotnet run samples/eval/eval.cs -- samples/eval/handbook.json .
//
//   git clone https://github.com/modelcontextprotocol/csharp-sdk /tmp/csharp-sdk
//   git -C /tmp/csharp-sdk checkout 64cfd66b147553d8cf953f9e2a374aae96014968
//   dotnet run samples/eval/eval.cs -- samples/eval/mcp-csharp-sdk-docs.json /tmp/csharp-sdk
//
// Uses Ollama by default; set the DOCSRAG_* variables to evaluate another embedding provider.
// When DOCSRAG_RERANK_ENDPOINT is set, hybrid search is also measured with reranking.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run samples/eval/eval.cs -- <questions.json> <repository checkout> [--details]");
    return 1;
}

var questionsFile = Path.GetFullPath(args[0]);
var checkout = Path.GetFullPath(args[1]);
var showDetails = args.Contains("--details");

using var json = JsonDocument.Parse(File.ReadAllText(questionsFile));
var folder = Path.Combine(checkout, json.RootElement.GetProperty("source").GetProperty("folder").GetString()!);
var questions = json.RootElement.GetProperty("questions").EnumerateArray()
    .Select(q => (Text: q.GetProperty("question").GetString()!,
                  Expected: q.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!).ToArray()))
    .ToList();

var project = Path.Combine(Directory.GetCurrentDirectory(), "src", "DocsRagMcp");
var database = Path.Combine(Path.GetTempPath(), $"docs-rag-eval-{Guid.NewGuid():N}.db");
var hitLine = new Regex(@"^\[(\d+)\] (.+?)(?: > (.*))? \(score [\d.]+\)$", RegexOptions.Multiline);

try
{
    Console.WriteLine($"{questions.Count} questions, documents in {folder}\n");
    Console.WriteLine("| Search mode | Right page first (hit@1) | Right page in top 3 (hit@3) | MRR |");
    Console.WriteLine("|---|---|---|---|");

    var runs = new List<(string Name, string SearchMode, bool Rerank)> { ("vector", "vector", false), ("hybrid", "hybrid", false) };
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCSRAG_RERANK_ENDPOINT")))
    {
        runs.Add(("hybrid + rerank", "hybrid", true));
    }

    var details = new List<string>();
    foreach (var (mode, searchMode, rerank) in runs)
    {
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "docs-rag-mcp",
            Command = "dotnet",
            Arguments = ["run", "--no-build", "--project", project, "--", "--database", database, "--search-mode", searchMode],
            // An empty value turns reranking off for the runs without it.
            EnvironmentVariables = rerank ? null : new Dictionary<string, string?> { ["DOCSRAG_RERANK_ENDPOINT"] = string.Empty },
        }));

        // The first run builds the index; the others find every file unchanged and reuse it.
        await CallAsync(client, "index_folder", new() { ["path"] = folder });

        int hit1 = 0, hit3 = 0;
        double reciprocalRanks = 0;
        foreach (var (text, expected) in questions)
        {
            var output = await CallAsync(client, "search_docs", new() { ["query"] = text, ["topK"] = 10 });
            // An expected answer is either a page ("guide.md") or a section of one ("guide.md > Setup").
            var pages = hitLine.Matches(output)
                .Select(m => (Page: Path.GetRelativePath(folder, m.Groups[2].Value).Replace('\\', '/'), Section: m.Groups[3].Value))
                .Select(hit => (hit.Page, Full: hit.Section.Length == 0 ? hit.Page : $"{hit.Page} > {hit.Section}"))
                .ToList();

            var rank = pages.FindIndex(hit => expected.Contains(hit.Page) || expected.Contains(hit.Full)) + 1;
            if (rank == 1) hit1++;
            if (rank is >= 1 and <= 3) hit3++;
            if (rank > 0) reciprocalRanks += 1.0 / rank;

            details.Add(string.Create(CultureInfo.InvariantCulture,
                $"{mode,-15} rank {(rank == 0 ? "-" : rank.ToString(CultureInfo.InvariantCulture)),2}  {text}  (top: {pages.FirstOrDefault().Full})"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"| {mode} | {hit1} of {questions.Count} | {hit3} of {questions.Count} | {reciprocalRanks / questions.Count:0.00} |"));
    }

    if (showDetails)
    {
        Console.WriteLine();
        details.ForEach(Console.WriteLine);
    }

    return 0;
}
finally
{
    foreach (var file in new[] { database, database + "-wal", database + "-shm" })
    {
        File.Delete(file);
    }
}

static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
{
    var result = await client.CallToolAsync(tool, arguments);
    var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    if (result.IsError == true)
    {
        throw new InvalidOperationException($"{tool} failed: {text}");
    }

    return text;
}
