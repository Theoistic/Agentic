---
title: Image Input
parent: Features
nav_order: 9
---

# Image Input (Vision)
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

Agentic supports vision-capable models by letting you include images in any agent turn. Images can be provided as a URL, a local file path, or a base64 data URL — the library handles the rest.

## Sending Images with an Agent

Pass images as part of a chat turn using the `images:` parameter:

```csharp
// From a URL
await agent.ChatStreamAsync(
    "What is in this image?",
    images: ["https://example.com/photo.jpg"]);

// From a local file
await agent.ChatStreamAsync(
    "Describe this diagram.",
    images: ["/home/user/documents/diagram.png"]);

// As a base64 data URL
await agent.ChatStreamAsync(
    "What text appears in this screenshot?",
    images: [$"data:image/png;base64,{Convert.ToBase64String(imageBytes)}"]);

// Multiple images
await agent.ChatStreamAsync(
    "Compare these two charts.",
    images: ["https://example.com/chart1.png", "https://example.com/chart2.png"]);
```

## Direct Vision Call via LM

You can also call the LM directly with image inputs:

```csharp
var result = await lm.RespondAsync(
    [ResponseInput.User(
        "What brand is shown in this logo?",
        ["https://example.com/logo.png"])]);

Console.WriteLine(result.OutputText);
```

## Supported Image Formats

| Format | Example |
|--------|---------|
| HTTPS URL | `https://example.com/photo.jpg` |
| HTTP URL | `http://internal-server/image.png` |
| Local file path | `/home/user/image.png` or `C:\images\photo.jpg` |
| Base64 data URL | `data:image/jpeg;base64,/9j/4AAQ...` |

The library detects the format automatically and converts local files to base64 data URLs before sending them to the model.

## Requirements

- Your model must support vision (e.g. GPT-4o, Claude 3, LLaVA, Qwen-VL, etc.)
- The `EmbeddingModel` field is not required for vision — only `ModelName` is needed
- Large local images are loaded entirely into memory before being sent

## Example: Invoice OCR

```csharp
var lm = new LM(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "llava-v1.6-34b",
});

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt =
        "You are a document extraction assistant. " +
        "Extract structured data from images accurately.",
    OnEvent = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta)
            Console.Write(e.Text);
    },
});

await agent.ChatStreamAsync(
    "Extract the invoice number, date, vendor, line items, and total from this invoice.",
    images: ["/tmp/invoice.pdf.png"]);
```

## Example: Multi-page Document Analysis

```csharp
var pageImages = Directory
    .GetFiles("/tmp/pages", "*.png")
    .OrderBy(f => f)
    .ToArray();

await agent.ChatStreamAsync(
    $"I'm sending you {pageImages.Length} pages of a report. " +
    "Please summarise the key findings.",
    images: pageImages);
```
