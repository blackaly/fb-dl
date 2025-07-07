using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Win32;
using System.Security.Principal;

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

            return Uri.TryCreate(url, UriKind.Absolute, out Uri result) &&
                   (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Get safe filename from title
        /// </summary>
        public static string GetSafeFileName(string title, string extension = "mp4")
        {
            if (string.IsNullOrEmpty(title))
                title = "video";

            // Remove invalid characters
            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var safeName = string.Join("_", title.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

            // Limit length
            if (safeName.Length > 100)
                safeName = safeName.Substring(0, 100);

            return $"{safeName}.{extension}";
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