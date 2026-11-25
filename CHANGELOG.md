# Changelog

## 0.4.0 - 2026-11-25

- YAML front matter at the start of a Markdown file is no longer indexed, and its `title` is used as the section name of the text before the first heading. A `---` horizontal rule later in the file is kept. Files indexed before are cleaned up the next time they change (#3)
- `list_sources` starts with the index file, its size and the embedding model it was built with, for example `Index: /home/me/.docs-rag-mcp/index.db (12.4 MB), built with ollama/nomic-embed-text` (#4)

## 0.3.0 - 2026-11-24

- Watch mode: folders indexed with `index_folder` are remembered and watched. New, changed and deleted files are indexed automatically a couple of seconds after the folder goes quiet, and changes made while the server was stopped are picked up when it starts. Only files whose content changed are embedded again.
- `DOCSRAG_WATCH` (`--watch`): `true` (default), or `false` to index only when `index_folder` is called
- `list_sources` also lists the watched folders, and `remove_from_index` stops watching a folder it removes
- Indexes built with 0.2.0 work as they are; their folders are watched once they are indexed again with `index_folder`

## 0.2.0 - 2026-11-24

- Hybrid search: `search_docs` now blends vector similarity with keyword matching (SQLite FTS5 with BM25). When passages are about equally close in meaning, the one that uses the exact terms of the question, including in its heading, comes first. On the MCP C# SDK docs evaluation, the right page is now in the top 3 for 17 of 22 questions instead of 15; on the handbook evaluation every answer stays first.
- `DOCSRAG_SEARCH_MODE` (`--search-mode`): `hybrid` (default) or `vector` for the previous behaviour
- Indexes built with 0.1.0 get their keyword index the first time they're opened, without re-indexing
- `samples/eval/` measures search quality on two sets of questions with known answers

## 0.1.0 - 2026-11-23

First release.

- MCP tools: `index_folder`, `search_docs`, `list_sources` and `remove_from_index`
- Reads Markdown, plain-text and PDF files; Markdown is split at headings and PDFs by page
- Embeddings from Ollama, OpenAI (or any OpenAI-compatible API) and Azure OpenAI, with API key or Microsoft Entra ID sign-in
- Local SQLite index with incremental re-indexing based on file hashes
- `DOCSRAG_ALLOWED_PATHS` to limit which folders can be indexed
