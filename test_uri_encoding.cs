using System;

class Program 
{
    static void Main()
    {
        var specialChars = new[] { "<", ">", ":", "\"", "|", "*", "?" };
        
        foreach (var specialChar in specialChars)
        {
            try 
            {
                var url = new Uri($"https://example.com/test{specialChar}file.jpg");
                Console.WriteLine($"'{specialChar}' -> Path: '{url.AbsolutePath}'");
            }
            catch (Exception e)
            {
                Console.WriteLine($"'{specialChar}' -> Error: {e.GetType().Name}");
            }
        }
    }
}
