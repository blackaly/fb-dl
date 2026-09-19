using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VideoDownloaderConsole;

internal static class AtomicFile
{
    internal static void MoveNew(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: false);
            return;
        }
        // File.Move(false) on Unix can use a check-then-rename that overwrites a racing writer.
        // These native operations ask the filesystem to enforce no replacement atomically.
        var result = OperatingSystem.IsLinux() ? RenameAt2(-100, source, -100, destination, 1)
            : OperatingSystem.IsMacOS() ? RenameExclusive(source, destination, 4)
            : throw new PlatformNotSupportedException("Atomic file promotion requires Windows, Linux, or macOS.");
        if (result == 0) return;
        var error = Marshal.GetLastPInvokeError();
        throw new IOException($"Could not save '{destination}': {new Win32Exception(error).Message}");
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
}
