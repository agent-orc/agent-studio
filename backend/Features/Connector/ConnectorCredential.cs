using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentStudio.Connector;

public sealed record ConnectorCredential(string Bearer)
{
    // Records print every member; keep the bearer out of logs and exception text.
    public override string ToString() => "ConnectorCredential { Bearer = *** }";
}

public sealed record ConnectorCredentialLoadResult(
    ConnectorCredential? Credential,
    string? FailureCode,
    string? FailureMessage = null)
{
    public bool IsLoaded => Credential is not null;
}

public interface IConnectorCredentialSource
{
    ConnectorCredentialLoadResult Load(ConnectorOptions options);
}

/// <summary>
/// A credential read failure with a stable code and an operator-readable
/// message. The message reaches the browser through the attach refusal, so it
/// names the setting but never the secret or the credential's file path.
/// </summary>
public sealed class ConnectorCredentialException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ConnectorCredentialFailureCodes
{
    public const string Unavailable = "credential-unavailable";
    public const string Empty = "credential-empty";
    public const string InsecureFile = "credential-file-insecure";
}

public sealed class ConnectorCredentialSource : IConnectorCredentialSource
{
    internal const string DockerSecretPath = "/run/secrets/studio-task-server-token";

    public ConnectorCredentialLoadResult Load(ConnectorOptions options)
    {
        try
        {
            var value = options.Mode == ConnectorOptions.DockerMode
                ? ReadDockerSecret(DockerSecretPath)
                : OperatingSystem.IsWindows()
                    ? ReadWindowsCredential(options.CredentialTarget)
                    : ReadCredentialFile(options.CredentialFile);
            return string.IsNullOrWhiteSpace(value)
                ? new ConnectorCredentialLoadResult(null, ConnectorCredentialFailureCodes.Empty, "The stored Studio credential is empty.")
                : new ConnectorCredentialLoadResult(new ConnectorCredential(value), null);
        }
        catch (ConnectorCredentialException exception)
        {
            return new ConnectorCredentialLoadResult(null, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or Win32Exception
                                          or PlatformNotSupportedException)
        {
            return new ConnectorCredentialLoadResult(
                null,
                ConnectorCredentialFailureCodes.Unavailable,
                $"The Studio credential could not be read ({exception.GetType().Name}).");
        }
    }

    internal static string ReadDockerSecret(string path)
        => ReadOwnerOnlyFile(path, allowOwnerWrite: false, "The Docker connector credential secret");

    /// <summary>
    /// Linux and macOS native connector: the credential lives in one regular
    /// file readable only by the connector's user (mode 0600 or 0400). The
    /// operator rotates it by rewriting the file; the next refresh picks it up.
    /// </summary>
    internal static string ReadCredentialFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ConnectorCredentialException(
                ConnectorCredentialFailureCodes.Unavailable,
                "No Connector:CredentialFile is configured and no user configuration directory is available.");
        }
        return ReadOwnerOnlyFile(path, allowOwnerWrite: true, "The Connector:CredentialFile");
    }

    private static string ReadOwnerOnlyFile(string path, bool allowOwnerWrite, string subject)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ConnectorCredentialException(
                ConnectorCredentialFailureCodes.Unavailable,
                $"{subject} does not exist.");
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ConnectorCredentialException(
                ConnectorCredentialFailureCodes.InsecureFile,
                $"{subject} must be a regular file, not a symbolic link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(fullPath);
            var forbidden = UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if (!allowOwnerWrite) forbidden |= UnixFileMode.UserWrite;
            if ((mode & UnixFileMode.UserRead) == 0 || (mode & forbidden) != 0)
            {
                throw new ConnectorCredentialException(
                    ConnectorCredentialFailureCodes.InsecureFile,
                    allowOwnerWrite
                        ? $"{subject} must be readable only by its owner (chmod 600)."
                        : $"{subject} must be owner-readable and otherwise read-only (0400).");
            }
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
            throw new ConnectorCredentialException(ConnectorCredentialFailureCodes.Empty, $"{subject} is empty.");
        return value;
    }

    internal static string ReadWindowsCredential(string target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native connector credentials require Windows Credential Manager.");
        if (!CredRead(target, 1, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            const int notFound = 1168;
            throw new ConnectorCredentialException(
                ConnectorCredentialFailureCodes.Unavailable,
                error == notFound
                    ? $"Windows Credential Manager has no generic credential '{target}'. Store it with set-studio-credential.ps1."
                    : $"Windows Credential Manager could not read '{target}' (Win32 error {error}).");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                throw new ConnectorCredentialException(ConnectorCredentialFailureCodes.Empty, $"The Windows credential '{target}' is empty.");
            var value = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)))?.TrimEnd('\0').Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new ConnectorCredentialException(ConnectorCredentialFailureCodes.Empty, $"The Windows credential '{target}' is empty.");
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

/// <summary>
/// One credential read paired with the revision it was stored under. The pair
/// is published as a single object so a reader can never combine one read's
/// credential with another read's revision.
/// </summary>
public sealed record ConnectorCredentialState(ConnectorCredentialLoadResult Result, long Revision);

/// <summary>
/// Rotation without restart: the connector re-reads its credential store at
/// most once per <see cref="ConnectorOptions.CredentialRefreshInterval"/>, and
/// immediately after the Task Server rejects the current credential. A changed
/// value bumps <see cref="Revision"/>, which forces a fresh attach handshake so
/// the new credential is proven before it is trusted.
/// </summary>
public sealed class ConnectorCredentialProvider(
    ConnectorOptions options,
    IConnectorCredentialSource source,
    TimeProvider time)
{
    private readonly object _gate = new();
    private CachedCredential? _cached;

    public long Revision => Volatile.Read(ref _cached)?.State.Revision ?? 0;

    public ConnectorCredentialLoadResult Current() => CurrentState().Result;

    /// <summary>The current credential together with its revision, read atomically.</summary>
    public ConnectorCredentialState CurrentState()
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && time.GetUtcNow() - cached.LoadedAtUtc < options.CredentialRefreshInterval)
            return cached.State;

        lock (_gate)
        {
            cached = _cached;
            var now = time.GetUtcNow();
            if (cached is not null && now - cached.LoadedAtUtc < options.CredentialRefreshInterval)
                return cached.State;
            var loaded = source.Load(options);
            var revision = cached is null
                ? 1
                : Equals(cached.State.Result.Credential, loaded.Credential)
                    ? cached.State.Revision
                    : cached.State.Revision + 1;
            var state = new ConnectorCredentialState(loaded, revision);
            Volatile.Write(ref _cached, new CachedCredential(state, now));
            return state;
        }
    }

    /// <summary>Forces the next <see cref="Current"/> call to read the credential store again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            if (_cached is not null)
                Volatile.Write(ref _cached, _cached with { LoadedAtUtc = DateTimeOffset.MinValue });
        }
    }

    private sealed record CachedCredential(ConnectorCredentialState State, DateTimeOffset LoadedAtUtc);
}
