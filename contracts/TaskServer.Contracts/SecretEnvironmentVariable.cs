namespace AgentStudio.Secrets;

/// <summary>Maps a declared secret name to its container environment variable.</summary>
public static class SecretEnvironmentVariable
{
    public static string Name(string secretName)
        => secretName.Replace('-', '_').Replace('.', '_').ToUpperInvariant();

    public static bool HasCollision(IEnumerable<string> secretNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return secretNames.Any(name => !names.Add(Name(name)));
    }
}
