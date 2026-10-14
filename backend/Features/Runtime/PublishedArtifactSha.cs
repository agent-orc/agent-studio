namespace AgentStudio.Runtime;

/// <summary>The commit of the files installed beside this running assembly.</summary>
public sealed record PublishedArtifactSha(string? Value)
{
    public static PublishedArtifactSha Load(string directory, BuildIdentity identity)
    {
        var marker = Path.Combine(directory, "RELEASE-SHA");
        if (File.Exists(marker))
        {
            var sha = File.ReadAllText(marker).Trim();
            if (IsSha(sha)) return new PublishedArtifactSha(sha);
        }

        return new PublishedArtifactSha(!identity.Legacy && IsSha(identity.Commit)
            ? identity.Commit : null);
    }

    private static bool IsSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);
}
