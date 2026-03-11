using Agentic;
using Agentic.Runtime.Core;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Mantle = Agentic.Runtime.Mantle;

namespace Agentic.Tests;

[TestClass]
public sealed class MultiImageNativeBackendTests
{
    [ClassInitialize]
    public static void ClassInit(TestContext context) { }

    [ClassCleanup]
    public static void ClassCleanup() { }

    [TestInitialize]
    public void TestInit() { }

    [TestCleanup]
    public void TestCleanup() { }

    /// <summary>
    /// Renders every page of the bundled test invoice PDF to a JPEG data URL,
    /// sends them all in a single vision request, and asserts the model returns
    /// a non-empty document analysis.
    /// Requires <c>AGENTIC_NATIVE_MODEL_PATH</c> to point to a vision-capable GGUF.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeMultiPageInvoice_AllPagesAsImages_ResponseContainsDocumentContent()
    {
        var modelPath = Environment.GetEnvironmentVariable("AGENTIC_NATIVE_MODEL_PATH")
            ?? @"C:\Users\Theo\.lmstudio\models\lmstudio-community\Qwen3.5-9B-GGUF\Qwen3.5-9B-Q4_K_M.gguf";
        if (!File.Exists(modelPath))
            Assert.Inconclusive($"Chat model not found at '{modelPath}'. Set AGENTIC_NATIVE_MODEL_PATH to a valid GGUF path.");

        var pdfPath = Path.Combine(AppContext.BaseDirectory, "obscure_invoice_test.pdf");
        Assert.IsTrue(File.Exists(pdfPath), $"Test asset not found: {pdfPath}");

        var images = RenderAllPagesAsDataUrls(pdfPath);
        Assert.IsTrue(images.Count > 0, "No pages were rendered from the PDF.");

        var llamaBackend = Enum.TryParse<LlamaBackend>(
            Environment.GetEnvironmentVariable("AGENTIC_BACKEND"), ignoreCase: true, out var b)
            ? b : LlamaBackend.Cuda;

        var sessionOptions = new Mantle.LmSessionOptions
        {
            ModelPath        = modelPath,
            ToolRegistry     = new Mantle.ToolRegistry(),
            Compaction       = new Mantle.ConversationCompactionOptions(8192, ReservedForGeneration: 256),
            ContextTokens    = 8192,
            BatchTokens      = 1024,
            MicroBatchTokens = 1024,
            DefaultRequest   = new Mantle.ResponseRequest
            {
                MaxOutputTokens = 1024,
                EnableThinking  = false,
            },
        };

        await using var lm = new NativeBackend(sessionOptions, llamaBackend);

        var response = await lm.RespondAsync(
            [ResponseInput.User(
                "Analyse this invoice document in full. " +
                "Identify and describe the supplier, buyer, invoice number, date, currency, " +
                "and every line item with its description, quantity, unit price, and total.",
                images)],
            reasoning: ReasoningEffort.None);

        var text = response.Text;
        Assert.IsNotNull(text);
        Assert.IsTrue(text.Length > 0, "The model returned an empty response.");

        Console.WriteLine($"[Pages rendered: {images.Count}]");
        Console.WriteLine(text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<string> RenderAllPagesAsDataUrls(string pdfPath)
    {
        var results = new List<string>();

        using var reader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(2.0));
        int pageCount = reader.GetPageCount();

        for (int i = 0; i < pageCount; i++)
        {
            using var page = reader.GetPageReader(i);
            using var img  = Image.LoadPixelData<Bgra32>(
                page.GetImage(), page.GetPageWidth(), page.GetPageHeight());
            img.Mutate(x => x.BackgroundColor(Color.White));

            using var ms = new MemoryStream();
            img.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
            results.Add($"data:image/jpeg;base64,{Convert.ToBase64String(ms.ToArray())}");
        }

        return results;
    }
}
