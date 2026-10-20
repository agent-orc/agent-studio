using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentStudio.Telemetry;

/// <summary>Persists the directory entries that connect staged files and committed SQLite databases.</summary>
internal static class TelemetryDurability
{
    private const int MoveFileWriteThrough = 0x8;

    public static void MoveStagedFile(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(source, destination, MoveFileWriteThrough))
                throw new IOException("Cannot persist telemetry stage rename.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        else
        {
            File.Move(source, destination);
            FlushParentDirectory(destination);
        }
    }

    public static void FlushParentDirectory(string path)
    {
        // MoveFileEx with WRITE_THROUGH persists the stage rename on Windows. A stale
        // stage after deletion is safe because the SQLite commit marker is idempotent.
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var descriptor = OpenDirectory(directory, 0);
        if (descriptor < 0) throw new IOException($"Cannot open telemetry directory {directory} for sync.",
            new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            if (Fsync(descriptor) != 0)
                throw new IOException($"Cannot sync telemetry directory {directory}.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { _ = CloseDirectory(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseDirectory(int descriptor);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, int flags);
}
