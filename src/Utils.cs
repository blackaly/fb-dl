using System.Text.RegularExpressions;
using System.Web;
using System.Text;

namespace VideoDownloaderConsole
{
    public static class Utils
    {
        /// <summary>
        /// Sanitize content to prevent potential issues
        /// </summary>
        public static string SanitizeContent(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;

            // Basic HTML tag removal
            content = Regex.Replace(content, "<.*?>", string.Empty);

            // Decode HTML entities
            content = HttpUtility.HtmlDecode(content);

            return content.Trim();
        }

        /// <summary>
        /// Get parameter value from URL
        /// </summary>
        public static string GetParameterByName(string name, string url)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                return string.Empty;

            try
            {
                var uri = new Uri(url);
                var query = HttpUtility.ParseQueryString(uri.Query);
                return query[name] ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Validate URL format
        /// </summary>
        public static bool IsValidUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            return Uri.TryCreate(url, UriKind.Absolute, out var result) &&
                   (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);
        }

        internal static string NormalizeUrlInput(string? input)
        {
            var url = input?.Trim() ?? "";
            // Unwrap a complete pasted Markdown link, using its destination,
            // never its display text. Host validation still happens afterward.
            var markdown = Regex.Match(url,
                @"\A\[[^\]\r\n]*\]\((?<url><https?://[^<>\s]+>|https?://[^<>\s]+)\)\z",
                RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
            if (markdown.Success)
                url = markdown.Groups["url"].Value;
            if (url.StartsWith('<') && url.EndsWith('>'))
                url = url[1..^1];
            return url;
        }

        /// <summary>
        /// Get safe filename from title
        /// </summary>
        public static string GetSafeFileName(string? title, string extension = "mp4")
        {
            if (!Regex.IsMatch(extension, @"\A[a-zA-Z0-9]+\z"))
                throw new ArgumentException("Invalid file extension.", nameof(extension));
            var safeName = new string((title ?? "").Select(c =>
                char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            // Leave room for the extension and collision suffix on filesystems
            // whose filename limit is measured in UTF-8 bytes, not characters.
            var shortened = new StringBuilder();
            var bytes = 0;
            foreach (var rune in safeName.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 180)
                    break;
                shortened.Append(rune.ToString());
                bytes += rune.Utf8SequenceLength;
            }
            safeName = shortened.ToString().TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "video";
            if (Regex.IsMatch(safeName.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase))
                safeName = "_" + safeName;
            return $"{safeName}.{extension}";
        }

        internal static Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            // Console's synchronized reader can block even through ReadLineAsync.
            // Wait on a background read so Ctrl+C also works at either prompt.
            var input = Console.In;
            return Task.Run(input.ReadLine, cancellationToken).WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Format file size
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
        
         /*  private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }


        public static void AddToPATH()
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string exeDirectory = Path.GetDirectoryName(exePath);
            if (IsAdministrator())
            {
                string sysPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
                if (!sysPath.Split(';').Contains(exeDirectory, StringComparer.OrdinalIgnoreCase))
                {
                    string newSysPath = sysPath + ";" + exeDirectory;
                    Environment.SetEnvironmentVariable("PATH", newSysPath, EnvironmentVariableTarget.Machine);
                    Console.WriteLine("Added to system PATH.");
                }
                else
                {
                    Console.WriteLine("Already in system PATH.");
                }
            }
            else
            {
                Console.WriteLine("Must run as administrator to edit system PATH.");
            }
        } */

    }
}
