namespace AgentStudio.Git;

/// <summary>
/// The author and committer of platform-owned document commits (Dossier
/// decisions, document moves and removals). Read once from
/// <c>Git:StudioCommitIdentity</c>; a native install's service user may have
/// no git identity of its own, so these commits never fall back to the host's
/// git config (AGT-3654).
/// </summary>
public sealed record StudioCommitIdentity(string Name, string Email)
{
    public const string Section = "Git:StudioCommitIdentity";

    public static readonly StudioCommitIdentity Default =
        new("Agent Studio", "studio@agent-studio.invalid");

    public static StudioCommitIdentity From(IConfiguration config)
    {
        var name = config[$"{Section}:Name"]?.Trim();
        var email = config[$"{Section}:Email"]?.Trim();
        return new StudioCommitIdentity(
            string.IsNullOrEmpty(name) ? Default.Name : name,
            string.IsNullOrEmpty(email) ? Default.Email : email);
    }

    /// <summary>
    /// The git environment for one commit. The GIT_AUTHOR_* and
    /// GIT_COMMITTER_* variables take precedence over every config level, so
    /// this is the single place the identity is applied.
    /// </summary>
    public IReadOnlyDictionary<string, string?> GitEnvironment() => new Dictionary<string, string?>
    {
        ["GIT_AUTHOR_NAME"] = Name,
        ["GIT_AUTHOR_EMAIL"] = Email,
        ["GIT_COMMITTER_NAME"] = Name,
        ["GIT_COMMITTER_EMAIL"] = Email,
    };
}
