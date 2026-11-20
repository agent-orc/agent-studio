namespace AgentStudio.Security;

/// <summary>Turns a stored user id into the name people know, for history rows that record who acted.</summary>
public interface IUserDisplayNames
{
    /// <returns>The display name, or null for an id that is not a Studio user (a service account or host).</returns>
    string? DisplayNameOf(string userId);
}
