using System.Reflection;

namespace AgentStudio.Bff;

public static class StudioBffVersion
{
    public static string ProductVersion
        => typeof(StudioBffVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string GitSha
    {
        get
        {
            var informational = typeof(StudioBffVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            var separator = informational?.IndexOf('+') ?? -1;
            if (separator < 0 || separator + 1 >= informational!.Length)
                return "unknown";
            var metadata = informational[(separator + 1)..];
            return metadata.StartsWith("unknown.", StringComparison.Ordinal)
                ? metadata["unknown.".Length..]
                : metadata;
        }
    }

    public static string Display => $"agent-studio-bff {ProductVersion} ({GitSha})";
}
