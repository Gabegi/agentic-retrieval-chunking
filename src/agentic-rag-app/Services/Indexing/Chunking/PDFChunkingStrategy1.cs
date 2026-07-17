// RAG / search / LLM input → combine. Microsoft's own recommended pattern: request outputContentFormat=markdown, 
// then chunk the single returned content string on paragraph/section boundaries. 
// You can use the Markdown content from the Layout model to split documents based on paragraph boundaries, 
// create specific chunks for tables, and fine-tune your chunking strategy to improve response quality. 
// Don't manually reassemble text from separate paragraphs/tables/lines arrays — 
// the markdown output already does that for you, with tables rendered as HTML tables and headings as #
// https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/concept/retrieval-augmented-generation?view=doc-intel-4.0.0