namespace AgentStudio.Shared;

/// <summary>
/// The one comparer for dictionaries and groupings keyed by a normalized
/// filesystem path (repository roots, watch paths, task folders). It follows
/// the host filesystem's default case rule: case-insensitive on Windows and
/// macOS, ordinal on Linux, where two paths that differ only by case are two
/// different directories and must never share cached state.
/// </summary>
public static class FileSystemPathComparer
{
    public static StringComparer Instance { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
