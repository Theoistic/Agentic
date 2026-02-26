using System.ComponentModel;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Agentic.Cli;

public class DocumentTools(LM lm, string documentsFolder) : IAgentToolSet
{
    [Tool, Description(
        "List all PDF documents available in the documents folder. " +
        "Returns file name, page count and size for each. Call this first to discover what is available.")]
    public Task<string> ListDocuments()
    {
        if (!Directory.Exists(documentsFolder))
            return Task.FromResult($"Documents folder not found: {documentsFolder}");

        var files = Directory.GetFiles(documentsFolder, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(f => f).ToList();

        if (files.Count == 0)
            return Task.FromResult("No PDF documents found.");

        var sb = new StringBuilder();
        sb.AppendLine($"{files.Count} document(s) in '{documentsFolder}':");
        sb.AppendLine(new string('─', 60));

        foreach (var file in files)
        {
            var info  = new FileInfo(file);
            var pages = "?";
            try
            {
                using var lib = DocLib.Instance;
                using var dr  = lib.GetDocReader(file, new PageDimensions(1.0));
                pages = dr.GetPageCount().ToString();
            }
            catch { }
            sb.AppendLine($"  {info.Name}  ({pages} pages, {info.Length / 1024.0:F0} KB)");
            sb.AppendLine($"    path: {file}");
        }

        return Task.FromResult(sb.ToString());
    }

    [Tool, Description(
        "Get the total number of pages in a PDF document. " +
        "Always call this first on unknown documents to plan how many AnalysePdfPages calls are needed. " +
        "Accepts a full path or just the file name from the documents folder.")]
    public Task<string> GetPageCount(
        [ToolParam("File path or file name of the PDF")] string filePath)
    {
        var resolved = Resolve(filePath);
        if (!File.Exists(resolved))
            return Task.FromResult($"File not found: {filePath}");
        try
        {
            using var lib       = DocLib.Instance;
            using var docReader = lib.GetDocReader(resolved, new PageDimensions(1.0));
            return Task.FromResult(
                $"'{Path.GetFileName(resolved)}' has {docReader.GetPageCount()} page(s).");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to open PDF: {ex.Message}");
        }
    }

    [Tool, Description(
        "Render a range of PDF pages to images and analyse each one using the vision model. " +
        "Use fromPage/toPage to paginate large documents. " +
        "Increase scale for higher-resolution rendering when text is small or dense.")]
    public async Task<string> AnalysePdfPages(
        [ToolParam("File path or file name of the PDF")] string filePath,
        [ToolParam("What to extract or describe (e.g. 'OCR all text', 'summarise content', 'list tables')")] string prompt = "Please read and transcribe all visible text on this page.",
        [ToolParam("First page to analyse (1-based, inclusive)")] int fromPage = 1,
        [ToolParam("Last page to analyse (1-based, inclusive; -1 means same as fromPage)")] int toPage = -1,
        [ToolParam("Render scale factor: 1.0 = 72 dpi, 2.0 = 144 dpi, 3.0 = 216 dpi")] double scale = 2.0)
    {
        var resolved = Resolve(filePath);
        if (!File.Exists(resolved))
            return $"File not found: {filePath}";
        try
        {
            using var lib       = DocLib.Instance;
            using var docReader = lib.GetDocReader(resolved, new PageDimensions(scale));
            var pageCount = docReader.GetPageCount();

            var first = Math.Clamp(fromPage - 1, 0, pageCount - 1);
            var last  = toPage < 1
                ? first
                : Math.Clamp(toPage - 1, first, pageCount - 1);

            var sb = new StringBuilder();
            sb.AppendLine($"PDF: {Path.GetFileName(resolved)}  ({pageCount} pages total)");
            sb.AppendLine($"Analysing page(s) {first + 1}–{last + 1} at ×{scale} scale:");
            sb.AppendLine(new string('─', 60));

            for (int i = first; i <= last; i++)
            {
                using var pageReader = docReader.GetPageReader(i);
                var width    = pageReader.GetPageWidth();
                var height   = pageReader.GetPageHeight();
                var rawBytes = pageReader.GetImage();

                var dataUri  = BgraToJpegDataUri(rawBytes, width, height);
                var analysis = await lm.DescribeImageAsync(prompt, dataUri);

                sb.AppendLine();
                sb.AppendLine($"── Page {i + 1} / {pageCount}  ({width}×{height} px) ──");
                sb.AppendLine(analysis);
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to analyse PDF: {ex.Message}";
        }
    }

    [Tool, Description(
        "Extract embedded text from a range of PDF pages without using vision — fast and exact for " +
        "text-based PDFs. Returns a note when a page contains no embedded text (scanned/image pages); " +
        "use AnalysePdfPages with vision OCR for those.")]
    public Task<string> ExtractPdfText(
        [ToolParam("File path or file name of the PDF")] string filePath,
        [ToolParam("First page to extract (1-based, inclusive)")] int fromPage = 1,
        [ToolParam("Last page to extract (1-based, inclusive; -1 means same as fromPage)")] int toPage = -1)
    {
        var resolved = Resolve(filePath);
        if (!File.Exists(resolved))
            return Task.FromResult($"File not found: {filePath}");
        try
        {
            using var lib       = DocLib.Instance;
            using var docReader = lib.GetDocReader(resolved, new PageDimensions(1.0));
            var pageCount = docReader.GetPageCount();

            var first = Math.Clamp(fromPage - 1, 0, pageCount - 1);
            var last  = toPage < 1
                ? first
                : Math.Clamp(toPage - 1, first, pageCount - 1);

            var sb = new StringBuilder();
            sb.AppendLine($"PDF: {Path.GetFileName(resolved)}  ({pageCount} pages total)");
            sb.AppendLine($"Embedded text from page(s) {first + 1}–{last + 1}:");
            sb.AppendLine(new string('─', 60));

            for (int i = first; i <= last; i++)
            {
                using var pageReader = docReader.GetPageReader(i);
                var text = pageReader.GetText();
                sb.AppendLine();
                sb.AppendLine($"── Page {i + 1} / {pageCount} ──");
                sb.AppendLine(string.IsNullOrWhiteSpace(text)
                    ? "(no embedded text — use AnalysePdfPages for vision OCR)"
                    : text);
            }

            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to extract text: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Resolves a filename or partial path against the documents folder.</summary>
    private string Resolve(string filePath)
    {
        if (Path.IsPathRooted(filePath) && File.Exists(filePath))
            return filePath;

        var byName = Path.Combine(documentsFolder, filePath);
        if (File.Exists(byName)) return byName;

        // Recursive search by filename only
        return Directory
            .GetFiles(documentsFolder, Path.GetFileName(filePath), SearchOption.AllDirectories)
            .FirstOrDefault() ?? filePath;
    }

    private static string BgraToJpegDataUri(byte[] bgra, int width, int height)
    {
        using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
        using var ms    = new MemoryStream();
        image.SaveAsJpeg(ms);
        return $"data:image/jpeg;base64,{Convert.ToBase64String(ms.ToArray())}";
    }
}

