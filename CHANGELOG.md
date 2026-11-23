# Changelog

## 0.1.0 - 2026-11-23

First release.

- MCP tools: `index_folder`, `search_docs`, `list_sources` and `remove_from_index`
- Reads Markdown, plain-text and PDF files; Markdown is split at headings and PDFs by page
- Embeddings from Ollama, OpenAI (or any OpenAI-compatible API) and Azure OpenAI, with API key or Microsoft Entra ID sign-in
- Local SQLite index with incremental re-indexing based on file hashes
- `DOCSRAG_ALLOWED_PATHS` to limit which folders can be indexed
