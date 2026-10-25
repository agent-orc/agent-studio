namespace AgentStudio.Deploy;

public sealed record ReleaseNote(string CardKey, string Text);
public sealed record ReleaseNotesResponse(string ReleaseId, string Version, bool Preview,
    IReadOnlyList<ReleaseNote> Notes);

/// <summary>Uses card titles as product copy, without inferring a change from commit text.</summary>
public static class ReleaseNotesProjection
{
    public static ReleaseNotesResponse Project(Release release, IReadOnlyDictionary<string, string> titles,
        bool preview = true)
        => new(release.Id, release.Version, preview,
            release.CardKeys.Select(key => new ReleaseNote(key,
                titles.TryGetValue(key, out var title) && !string.IsNullOrWhiteSpace(title)
                    ? title.Trim() : key)).ToArray());
}
