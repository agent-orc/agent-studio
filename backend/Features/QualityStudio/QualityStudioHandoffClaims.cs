namespace AgentStudio.QualityStudio;

public sealed record QualityStudioHandoffClaims(
    string iss, string aud, string sub, string sid, string role, IReadOnlyList<string> repos,
    string target, string jti, long iat, long exp, int ver)
{
    public static QualityStudioHandoffClaims Issue(string issuer, string subject, string sessionId,
        string role, string repositoryId, string target, DateTimeOffset now, string jti)
    {
        var issued = now.ToUnixTimeSeconds();
        return new QualityStudioHandoffClaims(issuer, "quality-studio", subject, sessionId,
            role, [repositoryId], target, jti, issued, issued + 60, 1);
    }
}
