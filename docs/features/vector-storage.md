---
title: Vector Storage
parent: Features
nav_order: 6
---

# Vector Storage
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

Agentic provides a typed vector store abstraction (`IStore` / `ICollection<T>`) with two production-ready backends — SQLite and PostgreSQL + pgvector — plus an in-memory store for tests.

## Setup

### SQLite (default)

```csharp
services.AddStore();
```

SQLite stores embeddings in a local `.db` file. No additional infrastructure required.

### PostgreSQL + pgvector

```csharp
// appsettings.json
{
  "Database": {
    "Provider": "postgres",
    "ConnectionString": "Host=localhost;Database=mydb;Username=postgres;Password=secret"
  }
}

// Program.cs
services.AddStore(configuration);
```

Requires the [pgvector](https://github.com/pgvector/pgvector) extension to be installed in your PostgreSQL database:

```sql
CREATE EXTENSION vector;
```

### In-memory (testing)

```csharp
services.AddInMemoryStore();
```

The in-memory store is non-persistent and ideal for unit tests and integration tests.

## Working with Collections

### Insert and upsert

```csharp
var store    = services.BuildServiceProvider().GetRequiredService<IStore>();
var articles = store.Collection<Article>("articles");

// Insert a new document with its embedding
var id = await articles.InsertAsync(
    new Article { Title = "Hello", Body = "..." },
    embedding: embeddingVector);

// Update an existing document (keep same embedding)
await articles.UpsertAsync(id, new Article { Title = "Updated", Body = "..." });
```

### Semantic search

```csharp
var queryVector = await lm.EmbedAsync("search query text");

var results = await articles.SearchAsync(queryVector, topK: 5);
foreach (var r in results)
    Console.WriteLine($"{r.Score:F4}  {r.Document.Title}");
```

### Delete

```csharp
await articles.DeleteAsync(id);
```

## ICollection<T> Reference

| Method | Description |
|--------|-------------|
| `InsertAsync(doc, embedding)` | Insert a new document and store its embedding |
| `UpsertAsync(id, doc)` | Update the document at `id` (embedding unchanged) |
| `UpsertAsync(id, doc, embedding)` | Update the document and its embedding |
| `DeleteAsync(id)` | Remove the document by ID |
| `SearchAsync(queryVector, topK)` | Return the top-K most similar documents with similarity scores |
| `GetAsync(id)` | Retrieve a document by ID |

## Document Type Requirements

Documents can be any C# class. No base class or interface is required — they are serialised to/from JSON:

```csharp
public class Article
{
    public string Title  { get; set; } = "";
    public string Body   { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}
```

## Example: RAG Pipeline

A complete Retrieval-Augmented Generation (RAG) pipeline using Agentic:

```csharp
// 1. Index documents
var store    = sp.GetRequiredService<IStore>();
var articles = store.Collection<Article>("articles");

foreach (var article in myArticles)
{
    var embedding = await lm.EmbedAsync(article.Title + " " + article.Body);
    await articles.InsertAsync(article, embedding);
}

// 2. Retrieve relevant context
var queryEmbedding = await lm.EmbedAsync(userQuery);
var topResults     = await articles.SearchAsync(queryEmbedding, topK: 3);

var context = string.Join("\n\n", topResults.Select(r =>
    $"Title: {r.Document.Title}\n{r.Document.Body}"));

// 3. Answer with context
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt =
        $"You are a helpful assistant. Use the following context to answer questions:\n\n{context}",
});

await agent.ChatStreamAsync(userQuery);
```
