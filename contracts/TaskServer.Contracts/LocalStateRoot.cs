namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The per-user local application data directory (<c>$XDG_DATA_HOME</c> or
/// <c>~/.local/share</c>, <c>%LOCALAPPDATA%</c>) that holds machine-local host
/// state: batch gate store and leases, gate result cache, remote gate runs,
/// quota caps, fallback preferences, model routing state and similar files.
///
/// <c>Environment.GetFolderPath(LocalApplicationData)</c> returns an empty
/// string on Linux when the folder does not exist yet, which turned every
/// <c>Path.Combine</c> below it into a path relative to the working directory.
/// In a container that is the image layer, so the state vanished on every
/// recreate. This root is always absolute and exists once returned; when it
/// cannot be created the host fails at startup instead of writing elsewhere.
/// </summary>
public static class LocalStateRoot
{
    private static readonly Lazy<string> Default = new(() => Ensure(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify)));

    /// <summary>The absolute, existing local data directory of this process.</summary>
    /// <exception cref="LocalStateRootUnavailableException">The directory cannot be resolved or created.</exception>
    public static string Resolve() => Default.Value;

    /// <summary>A path below <see cref="Resolve"/>.</summary>
    public static string Combine(params string[] segments) => Path.Combine([Resolve(), .. segments]);

    /// <summary>
    /// Validates <paramref name="candidate"/> as the local data directory and
    /// creates it. Only a fully qualified path is accepted, so a missing
    /// <c>HOME</c> can never resolve to the working directory.
    /// </summary>
    public static string Ensure(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            throw new LocalStateRootUnavailableException(
                "The local application data directory could not be resolved. Set HOME (or XDG_DATA_HOME) "
                + "to a writable directory on persistent storage.");
        if (!Path.IsPathFullyQualified(candidate))
            throw new LocalStateRootUnavailableException(
                $"The local application data directory '{candidate}' is not an absolute path. Set HOME "
                + "(or XDG_DATA_HOME) to an absolute, writable directory on persistent storage.");
        var root = Path.GetFullPath(candidate);
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new LocalStateRootUnavailableException(
                $"The local application data directory '{root}' cannot be created: {exception.Message} "
                + "Make it writable for the service account, or set HOME (or XDG_DATA_HOME) to a writable "
                + "directory on persistent storage.", exception);
        }
        return root;
    }
}

/// <summary>The local data directory is missing and cannot be created.</summary>
public sealed class LocalStateRootUnavailableException : InvalidOperationException
{
    public LocalStateRootUnavailableException(string message) : base(message) { }

    public LocalStateRootUnavailableException(string message, Exception inner) : base(message, inner) { }
}
