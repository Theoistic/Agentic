---
title: RAG Pipeline
parent: Examples
nav_order: 5
---

# RAG Pipeline
{: .no_toc }

A complete Retrieval-Augmented Generation (RAG) pipeline using Agentic's vector storage.

## Setup

```csharp
using Agentic;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddSingleton<ILLMBackend>(_ => new OpenAIBackend(new LMConfig
{
    Endpoint       = "http://localhost:1234",
    ModelName      = "your-model-name",
    EmbeddingModel = "your-embedding-model",   // required for RAG
}));

// Use SQLite for local development, Postgres for production
services.AddStore();

var sp = services.BuildServiceProvider();
var lm    = sp.GetRequiredService<ILLMBackend>();
var store = sp.GetRequiredService<IStore>();
```

## Document Model

```csharp
public record KnowledgeArticle(
    string Title,
    string Body,
    string Source,
    DateTime IndexedAt);
```

## Indexing Documents

```csharp
var articles = store.Collection<KnowledgeArticle>("knowledge");

var docs = new[]
{
    new KnowledgeArticle(
        "What is Agentic?",
        "Agentic is a lightweight .NET library for building LLM-powered agents with streaming chat, " +
        "MCP tool hosting, context compaction, and vector storage.",
        "docs",
        DateTime.UtcNow),

    new KnowledgeArticle(
        "How to install Agentic",
        "Run: dotnet add package Agentic. Requires .NET 10 and ASP.NET Core.",
        "docs",
        DateTime.UtcNow),

    new KnowledgeArticle(
        "What is the MCP protocol?",
        "Model Context Protocol (MCP) is an open standard for connecting LLMs to external tools and " +
        "data sources over HTTP using JSON-RPC 2.0 and Server-Sent Events.",
        "docs",
        DateTime.UtcNow),
};

foreach (var doc in docs)
{
    var text      = $"{doc.Title}\n{doc.Body}";
    var embedding = await lm.EmbedAsync(text);
    await articles.InsertAsync(doc, embedding);
}

Console.WriteLine($"Indexed {docs.Length} articles.");
```

## Querying

```csharp
async Task<string> AskAsync(string question)
{
    // 1. Embed the question
    var queryVector = await lm.EmbedAsync(question);

    // 2. Retrieve top-3 relevant articles
    var results = await articles.SearchAsync(queryVector, topK: 3);

    if (results.Count == 0)
        return "No relevant documents found.";

    // 3. Build the context
    var context = string.Join("\n\n---\n\n",
        results.Select(r => $"**{r.Document.Title}**\n{r.Document.Body}"));

    Console.WriteLine($"Found {results.Count} relevant articles (top score: {results[0].Score:F4})");

    // 4. Ask the agent with context
    var agent = new Agent(lm, new AgentOptions
    {
        SystemPrompt =
            "You are a helpful assistant. Answer the user's question using ONLY the context below. " +
            "If the answer is not in the context, say so.\n\n" +
            $"Context:\n{context}",
        Inference = new InferenceConfig { Temperature = 0.2 },   // deterministic
    });

    return await agent.RunAsync(question);
}

// Example queries
Console.WriteLine(await AskAsync("What is Agentic?"));
Console.WriteLine(await AskAsync("How do I install it?"));
Console.WriteLine(await AskAsync("What is MCP?"));
```

## Tool-Augmented RAG

Combine RAG retrieval with tool access for dynamic knowledge bases:

```csharp
public class KnowledgeTools : IAgentToolSet
{
    private readonly ICollection<KnowledgeArticle> _articles;
    private readonly ILLMBackend _lm;

    public KnowledgeTools(IStore store, ILLMBackend lm)
    {
        _articles = store.Collection<KnowledgeArticle>("knowledge");
        _lm       = lm;
    }

    [Tool, Description("Search the knowledge base for articles relevant to a query.")]
    public async Task<string> SearchKnowledge(
        [ToolParam("Search query")] string query,
        [ToolParam("Number of results (default 3)")] int topK = 3)
    {
        var embedding = await _lm.EmbedAsync(query);
        var results   = await _articles.SearchAsync(embedding, topK: topK);

        if (results.Count == 0)
            return "No relevant articles found.";

        return string.Join("\n\n---\n\n",
            results.Select(r => $"Score: {r.Score:F4}\n{r.Document.Title}\n{r.Document.Body}"));
    }
}

// Register and run
tools.Register(new KnowledgeTools(store, lm));

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt =
        "You are a helpful assistant. Use the search_knowledge tool " +
        "to find relevant information before answering questions.",
});

await agent.ChatStreamAsync(
    "Tell me everything about Agentic's MCP server feature.",
    mcpServerUrl: "http://localhost:5100/mcp");
```

## What it demonstrates

- Setting up vector storage with SQLite
- Embedding documents at index time
- Semantic search at query time
- Injecting retrieved context into the system prompt
- Alternative: exposing search as an MCP tool
