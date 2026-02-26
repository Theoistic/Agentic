using System.ComponentModel;

namespace Agentic;

public class EmbeddingTools(LM lm) : IAgentToolSet {
    [Tool, Description("Generate an embedding vector for a text string. Returns the vector dimensions and a preview of the first values.")]
    public async Task<string> Embed(
        [ToolParam("The text to embed")] string text) {
        try {
            var vector = await lm.EmbedAsync(text);
            var preview = string.Join(", ", vector.Take(8).Select(v => v.ToString("F6")));
            return $"Embedding ({vector.Length} dimensions): [{preview}, ...]";
        } catch (Exception ex) {
            return $"Failed to generate embedding: {ex.Message}";
        }
    }

    [Tool, Description("Generate embeddings for multiple texts and compute cosine similarity between the first text and all others.")]
    public async Task<string> CompareSimilarity(
        [ToolParam("The reference text to compare against")] string referenceText,
        [ToolParam("Comma-separated texts to compare with the reference")] string comparisonTexts) {
        try {
            var texts = comparisonTexts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (texts.Length == 0)
                return "No comparison texts provided.";

            var allInputs = new[] { referenceText }.Concat(texts);
            var vectors = await lm.EmbedBatchAsync(allInputs);

            var refVec = vectors[0];
            var lines = texts.Select((t, i) => {
                var sim = CosineSimilarity(refVec, vectors[i + 1]);
                return $"  {sim:F4}  {t}";
            });

            return $"Similarity to \"{Truncate(referenceText, 60)}\":\n{string.Join("\n", lines)}";
        } catch (Exception ex) {
            return $"Failed to compare: {ex.Message}";
        }
    }

    private static float CosineSimilarity(float[] a, float[] b) {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++) {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}