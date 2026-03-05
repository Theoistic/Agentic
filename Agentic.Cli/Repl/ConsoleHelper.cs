namespace Agentic.Cli;

internal static class ConsoleHelper
{
    public static void Write(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    public static void WriteDim(string text) => Write(ConsoleColor.DarkGray, text + "\n");

    public static void PrintBanner(string text)
    {
        var rule = new string('═', text.Length + 4);
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(rule);
        Console.WriteLine($"  {text}  ");
        Console.WriteLine(rule);
        Console.ResetColor();
    }
}
