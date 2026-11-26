# Changelog

## 0.7.0 - 2026-11-26

- Indexed files are MCP resources. `resources/list` lists every indexed file with its `file://` URI, and `resources/read` returns Markdown and text files as they are on disk, and HTML, Word and PDF files as their extracted text (`text/markdown` or `text/plain`). Only files in the index that are still inside `DOCSRAG_ALLOWED_PATHS` can be read, and symbolic links are not followed. Clients are sent `notifications/resources/list_changed` when files are indexed, changed or removed
- The `folder` filter of `search_docs` also takes a single file, a `file://` resource URI or a path relative to the indexed folders (`api/v2`). When no indexed file is inside it, the search says so instead of returning nothing

## 0.6.0 - 2026-11-25

- Word support: `.docx` files are indexed (#1). Documents are split at their Title and Heading 1 to 3 paragraphs, including custom styles based on a heading and headings set with an outline level, and the document title from its properties names the text before the first heading. Headings are found by the built-in style names, so documents from non-English versions of Word work too. List items become `- ` lines and table rows `cell | cell` lines; deleted text from tracked changes and field codes are left out
- Word's `~$name.docx` lock files are skipped
- No new dependency: a `.docx` file is a zip of XML parts, read with the .NET base library

## 0.5.0 - 2026-11-25

- HTML support: `.html` and `.htm` files are indexed (#2). Pages are split at their `h1` to `h3` headings, and the page title names the text before the first heading. When the page marks its content with `<main>`, only that is read. Scripts, styles, navigation, hidden elements and the page's own header and footer are left out; a header inside an `article` or `main` element is kept, since it usually holds the title. Code blocks keep their indentation. The page's `<meta charset>` is respected
- New dependency: [AngleSharp](https://github.com/AngleSharp/AngleSharp) (MIT) to parse HTML

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
