using System.ComponentModel;
using System.Text;

namespace Agentic.Cli;

public class ImageTools(LM lm) : IAgentToolSet
{
    private static readonly HttpClient s_http = new();
    [Tool, Description(
        "Analyse an image using vision via the /v1/responses API. " +
        "Accepts an HTTP/HTTPS URL or a base64 data URL (data:image/...;base64,...). " +
        "Returns a detailed answer to the prompt about what the image shows.")]
    public async Task<string> AnalyseImage(
        [ToolParam("Image URL or base64 data URL")] string imageUrl,
        [ToolParam("What to describe or extract from the image")] string prompt = "Describe this image in detail.")
    {
        try
        {
            // The LM server blocks private-IP URLs (SSRF prevention).
            // Download the image here and forward it as a base64 data URL instead.
            var dataUrl = await ToDataUrlAsync(imageUrl);
            var resp    = await lm.RespondAsync([ResponseInput.User(prompt, [dataUrl])], model: "ocr");
            return ExtractText(resp) is { Length: > 0 } t ? t : "(no response)";
        }
        catch (Exception ex)
        {
            return $"Failed to analyse image: {ex.Message}";
        }
    }

    [Tool, Description(
        "Analyse a local image file on disk using vision. " +
        "The file is read, base64-encoded, and sent as an input_image to the /v1/responses API. " +
        "Accepts an absolute path or a file name relative to the wwwroot/images folder.")]
    public async Task<string> AnalyseLocalImage(
        [ToolParam("Absolute path or file name relative to wwwroot/images")] string filePath,
        [ToolParam("What to describe or extract from the image")] string prompt = "Describe this image in detail.")
    {
        var resolved = Resolve(filePath);
        if (!File.Exists(resolved))
            return $"File not found: {filePath}";
        try
        {
            var img  = InputImageContent.FromFile(resolved);  // → data:image/…;base64,…
            var resp = await lm.RespondAsync([ResponseInput.User(prompt, [img.ImageUrl!])]);
            return ExtractText(resp) is { Length: > 0 } t ? t : "(no response)";
        }
        catch (Exception ex)
        {
            return $"Failed to analyse image: {ex.Message}";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    internal static async Task<string> ToDataUrlAsync(string imageUrl)
    {
        // Already a data URL — pass straight through.
        if (!imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return imageUrl;

        using var r    = await s_http.GetAsync(imageUrl);
        r.EnsureSuccessStatusCode();
        var mime  = r.Content.Headers.ContentType?.MediaType ?? InferMimeFromUrl(imageUrl);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string InferMimeFromUrl(string url)
    {
        var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            _                 => "image/jpeg",
        };
    }

    private static string Resolve(string filePath)
    {
        if (Path.IsPathRooted(filePath) && File.Exists(filePath)) return filePath;

        var imagesDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", filePath);
        if (File.Exists(imagesDir)) return imagesDir;

        return Path.Combine(AppContext.BaseDirectory, "wwwroot", filePath);
    }

    private static string ExtractText(ResponseResponse resp)
    {
        var sb = new StringBuilder();
        foreach (var item in resp.Output)
            if (item.Type == "message" && item.Content is not null)
                foreach (var part in item.Content)
                    if (part.Type == "output_text" && part.Text is not null)
                        sb.Append(part.Text);
        return sb.ToString();
    }
}
