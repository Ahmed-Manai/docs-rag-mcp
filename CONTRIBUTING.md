# Contributing to docs-rag-mcp

Thanks for your interest in docs-rag-mcp! Bug reports, ideas, documentation fixes and code are all welcome.

## Where to start

- Issues labelled [good first issue](https://github.com/Ahmed-Manai/docs-rag-mcp/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22) are small and come with pointers to the code involved.
- Have a question or an idea that isn't a bug yet? Start a thread in [Discussions](https://github.com/Ahmed-Manai/docs-rag-mcp/discussions).
- Before you start on something bigger, comment on the issue (or open one) so we can agree on the approach first.

## Set up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else is required to build and run the tests.

```bash
git clone https://github.com/Ahmed-Manai/docs-rag-mcp.git
cd docs-rag-mcp
dotnet build
dotnet test
```

The tests use a deterministic fake embedding model, so they need no network and no API key. A few of them start the real server process and talk to it over MCP.

To check a change against a real model, install [Ollama](https://ollama.com), run `ollama pull nomic-embed-text`, and then:

```bash
dotnet run samples/smoke.cs -- "How much can I spend on a hotel in Paris?"
```

You can also open the repository in GitHub Codespaces and work entirely in the browser.

## Making a change

1. Fork the repository and create a branch from `main`.
2. Keep the change focused on one thing. Small pull requests are reviewed faster.
3. Add or update tests for the behaviour you changed. Test files live in `tests/DocsRagMcp.Tests`, next to the area they cover.
4. Make sure `dotnet build` and `dotnet test` pass. Warnings are treated as errors, and the code style in `.editorconfig` is checked during the build.
5. If users will notice the change, add a line under a new `## Unreleased` heading in `CHANGELOG.md`.
6. Open a pull request and fill in the template. CI builds and tests it on Linux, Windows and macOS.

## Project layout

| Path | What's there |
|------|--------------|
| `src/DocsRagMcp/Tools/` | The MCP tools (`index_folder`, `search_docs`, `list_sources`, `remove_from_index`) |
| `src/DocsRagMcp/Resources/` | The indexed files as MCP resources |
| `src/DocsRagMcp/Ingestion/` | Reading Markdown, text, HTML, Word and PDF files, and chunking |
| `src/DocsRagMcp/Indexing/` | Folder sync and the allowed-folder checks |
| `src/DocsRagMcp/Storage/` | SQLite storage and vector search |
| `src/DocsRagMcp/Embeddings/` | Ollama, OpenAI and Azure OpenAI clients |

## Reporting security issues

Please don't open a public issue for a security problem. Use [private vulnerability reporting](https://github.com/Ahmed-Manai/docs-rag-mcp/security/advisories/new) instead.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
