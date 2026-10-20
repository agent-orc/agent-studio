using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentStudio.Telemetry;

/// <summary>Persists the directory entries that connect staged files and committed SQLite databases.</summary>
internal interface ITelemetryDurability
{
    void MoveStagedFile(string source, string destination);
    void FlushParentDirectory(string path);
}

internal sealed class TelemetryDurability : ITelemetryDurability
{
    public static readonly ITelemetryDurability Instance = new TelemetryDurability();
    private const int MoveFileWriteThrough = 0x8;

    public void MoveStagedFile(string source, string destination)
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

    public void FlushParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsWindows())
        {
            // Windows cannot flush a directory handle. A flushed marker and a
            // write-through rename force a metadata operation in that directory.
            var marker = Path.Combine(directory, ".telemetry-sync-" + Guid.NewGuid().ToString("N"));
            var renamed = marker + ".done";
            try
            {
                using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1, FileOptions.WriteThrough)) stream.Flush(flushToDisk: true);
                if (!MoveFileEx(marker, renamed, MoveFileWriteThrough))
                    throw new IOException($"Cannot sync telemetry directory {directory}.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            finally
            {
                if (File.Exists(marker)) File.Delete(marker);
                if (File.Exists(renamed)) File.Delete(renamed);
            }
            return;
        }
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
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, int flags);
}
