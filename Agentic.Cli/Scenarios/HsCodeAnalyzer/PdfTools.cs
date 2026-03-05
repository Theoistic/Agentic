using System.ComponentModel;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Agentic.Cli;

public class PdfTools(LM lm) : IAgentToolSet
{
    private static readonly string s_renderDir =
        Path.Combine(AppContext.BaseDirectory, "pdf-renders");

    private static readonly HttpClient s_http = new();

    [Tool, Description(
        "Get the page count and basic info of a PDF. " +
        "Accepts a local file path or HTTP/HTTPS URL.")]
    public async Task<string> GetPdfInfo(
        [ToolParam("Local file path or HTTP/HTTPS URL to the PDF")] string pdfPath)
    {
        try
        {
            var local = await EnsureLocalAsync(pdfPath);
            using var reader = DocLib.Instance.GetDocReader(local, new PageDimensions(1.0));
            return $"PDF has {reader.GetPageCount()} page(s).";
        }
        catch (Exception ex) { return $"Failed to read PDF: {ex.Message}"; }
    }

    [Tool, Description(
        "Render one page of a PDF and OCR it with vision AI. Returns all extracted text for that page. " +
        "Call GetPdfInfo first to know how many pages exist. Page index is 0-based.")]
    public async Task<string> ScanPdfPage(
        [ToolParam("Local file path or HTTP/HTTPS URL to the PDF")] string pdfPath,
        [ToolParam("Zero-based page index to scan")] int pageIndex = 0,
        [ToolParam("Instruction for the vision model")] string prompt =
            "Extract all invoice content: supplier, buyer, invoice number, date, currency, " +
            "and every line item with its product description, quantity, unit, unit price, total price, and country of origin.")
    {
        try
        {
            var local              = await EnsureLocalAsync(pdfPath);
            var (dataUrl, savePath) = RenderPageToDataUrl(local, pageIndex);
            var resp               = await lm.RespondAsync(
                    [ResponseInput.User(prompt, [dataUrl])],
                    thinking: new ThinkingConfig { Enabled = false });

            var text = ExtractText(resp);
            return text.Length > 0
                ? $"[Page {pageIndex} · render saved → {savePath}]\n{text}"
                : $"[Page {pageIndex} · render saved → {savePath}] (no content extracted)";
        }
        catch (Exception ex) { return $"Failed to scan page {pageIndex}: {ex.Message}"; }
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private static (string DataUrl, string SavePath) RenderPageToDataUrl(string localPath, int pageIndex)
    {
        using var reader = DocLib.Instance.GetDocReader(localPath, new PageDimensions(2.0));
        var count = reader.GetPageCount();
        if (pageIndex < 0 || pageIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex),
                $"Page {pageIndex} is out of range (0–{count - 1}).");

        using var page   = reader.GetPageReader(pageIndex);
        var width  = page.GetPageWidth();
        var height = page.GetPageHeight();
        var bytes  = page.GetImage();

        using var img = Image.LoadPixelData<Bgra32>(bytes, width, height);
        img.Mutate(x => x.BackgroundColor(Color.White));
        using var ms  = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
        var jpeg = ms.ToArray();

        Directory.CreateDirectory(s_renderDir);
        var fileName = $"{Path.GetFileNameWithoutExtension(localPath)}-page{pageIndex}.jpg";
        var savePath = Path.Combine(s_renderDir, fileName);
        File.WriteAllBytes(savePath, jpeg);

        return ($"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}", savePath);
    }

    private static async Task<string> EnsureLocalAsync(string path)
    {
        if (!path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        using var r = await s_http.GetAsync(path);
        r.EnsureSuccessStatusCode();
        await File.WriteAllBytesAsync(tmp, await r.Content.ReadAsByteArrayAsync());
        return tmp;
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
