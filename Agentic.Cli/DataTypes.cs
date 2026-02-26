using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic.Cli;

public record HsCode {
    public string Chapter { get; init; }
    public string Position { get; init; }
    public string SubPosition { get; init; }

    public HsCode(string chapter, string position, string subPosition) {
        // Ensures fields are padded with leading zeros (e.g., "1" becomes "01")
        Chapter = chapter?.Trim().PadLeft(2, '0') ?? "00";
        Position = position?.Trim().PadLeft(2, '0') ?? "00";
        SubPosition = subPosition?.Trim().PadLeft(4, '0') ?? "0000";
    }

    public override string ToString() => $"{Chapter}{Position}{SubPosition}";

    public string ToFullFormat() => $"{Chapter}{Position}.{SubPosition.Substring(0, 2)}.{SubPosition.Substring(2)}";

    public string ToHeading() => $"{Chapter}.{Position}";

    public static string DigitsOnly(string? input)
        => string.IsNullOrWhiteSpace(input) ? "" : new string(input.Where(char.IsDigit).ToArray());

    public static string FormatHs6(string hs6Digits) {
        var d = DigitsOnly(hs6Digits);
        return d.Length != 6 ? d : $"{d[..4]}.{d.Substring(4, 2)}";
    }

    public static string FormatCn8(string cn8Digits) {
        var d = DigitsOnly(cn8Digits);
        return d.Length != 8 ? d : $"{d[..4]}.{d.Substring(4, 2)}.{d.Substring(6, 2)}";
    }
}

public class HSDescription {
    public HsCode Code { get; init; } = new HsCode("00", "00", "0000");
    public string Description { get; init; } = "";

    public override string ToString() {
        // We take the first line of the description for a concise ToString
        var shortDesc = Description.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return $"{Code.ToFullFormat()}: {shortDesc}";
    }

    public static List<HSDescription> FromJson(string json) {
        return JsonSerializer.Deserialize<List<HSDescription>>(json) ?? new List<HSDescription>();
    }
}
