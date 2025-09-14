using System.Text.RegularExpressions;

namespace ConsoleApp1;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Hello, World!");
        var url = new Uri("https://example.com/test:file*.jpg");
        Console.WriteLine($"AbsolutePath: '{url.AbsolutePath}'");
        Console.WriteLine($"Decoded: '{Uri.UnescapeDataString(url.AbsolutePath)}'");

        // Test the sanitization
        var regex = new Regex(@"[<>:""\\|?* ]");
        var decoded = Uri.UnescapeDataString(url.AbsolutePath);
        var sanitized = regex.Replace(decoded, "_");
        Console.WriteLine($"Sanitized: '{sanitized}'");
        Console.WriteLine($"Contains ':': {sanitized.Contains(':')}");
        Console.WriteLine($"Contains '*': {sanitized.Contains('*')}");
    }
}