using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentStudio.Connector;

public sealed record ConnectorCredential(string Bearer);

public sealed record ConnectorCredentialLoadResult(ConnectorCredential? Credential, string? FailureCode)
{
    public bool IsLoaded => Credential is not null;
}

public interface IConnectorCredentialSource
{
    ConnectorCredentialLoadResult Load(ConnectorOptions options);
}

public sealed class ConnectorCredentialSource : IConnectorCredentialSource
{
    internal const string WindowsCredentialTarget = "AgentStudio/TaskServer/studio-robert-windows";
    internal const string DockerSecretPath = "/run/secrets/studio-task-server-token";

    public ConnectorCredentialLoadResult Load(ConnectorOptions options)
    {
        try
        {
            var value = options.Mode == ConnectorOptions.DockerMode
                ? ReadDockerSecret(DockerSecretPath)
                : ReadWindowsCredential(WindowsCredentialTarget);
            return string.IsNullOrWhiteSpace(value)
                ? new ConnectorCredentialLoadResult(null, "credential-empty")
                : new ConnectorCredentialLoadResult(new ConnectorCredential(value), null);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or Win32Exception
                                          or PlatformNotSupportedException)
        {
            return new ConnectorCredentialLoadResult(null, "credential-unavailable");
        }
    }

    internal static string ReadDockerSecret(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException("The Docker connector credential is unavailable.");
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The Docker connector credential must not be a symbolic link.");

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(fullPath);
            const UnixFileMode forbidden = UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & UnixFileMode.UserRead) == 0 || (mode & forbidden) != 0)
                throw new InvalidOperationException(
                    "The Docker connector credential must be owner-readable and otherwise read-only (0400).");
        }

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var value = reader.ReadToEnd().Trim();
        if (value.Length == 0)
            throw new InvalidOperationException("The Docker connector credential is empty.");
        return value;
    }

    internal static string ReadWindowsCredential(string target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native connector credentials require Windows Credential Manager.");
        if (!CredRead(target, 1, 0, out var credentialPointer))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                throw new InvalidOperationException("The Windows connector credential is empty.");
            var value = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)))?.TrimEnd('\0').Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("The Windows connector credential is empty.");
            return value;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        int type,
        int reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credentialPointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
