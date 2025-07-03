using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace VideoDownloaderConsole
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Write("=== Facebook Downloader By ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("Ali Mashally");
            Console.ResetColor();
            Console.WriteLine(" ===");
            Console.WriteLine("Supported: Facebook");
            Console.WriteLine("Type 'exit' to quit\n");

            var downloader = new VideoDownloaderService();

            if (args.Length >= 2 && !string.IsNullOrEmpty(args[1]))
            {
                await downloader.ProcessVideoUrl(args[1]);
                return;
            }

            while (true)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("Enter video URL: ");
                    Console.ResetColor();

                    string input = Console.ReadLine()?.Trim();

                    if (string.IsNullOrEmpty(input))
                        continue;

                    if (input.ToLower() == "exit")
                        break;

                    try
                    {
                        await downloader.ProcessVideoUrl(input);
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error: {ex.Message}");
                        Console.ResetColor();
                    }

                    Console.WriteLine("\n" + new string('=', 60) + "\n");
                }

            downloader.Dispose();
        }
    }
}