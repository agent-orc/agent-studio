namespace AgentStudio.Deploy;

/// <summary>
/// One release note per card. <see cref="Maintenance"/> marks chore cards
/// (internal or technical work) so product notes can leave them out;
/// <see cref="Edited"/> says the text is a person's wording, not the card title.
/// </summary>
public sealed record ReleaseNote(string CardKey, string Text, bool Maintenance = false, bool Edited = false);
public sealed record ReleaseNotesResponse(string ReleaseId, string Version, bool Preview,
    IReadOnlyList<ReleaseNote> Notes);
public sealed record ReleaseNoteEditRequest(string? Text);

/// <summary>Uses an edited note, else the card title, without inferring a change from commit text.</summary>
public static class ReleaseNotesProjection
{
    public const int MaxNoteLength = 500;

    public static ReleaseNotesResponse Project(Release release, IReadOnlyDictionary<string, string> titles,
        bool preview = true, IReadOnlyDictionary<string, string>? edits = null,
        IReadOnlySet<string>? maintenance = null)
        => new(release.Id, release.Version, preview,
            release.CardKeys.Select(key => edits?.TryGetValue(key, out var edited) == true
                    && !string.IsNullOrWhiteSpace(edited)
                    ? new ReleaseNote(key, edited.Trim(), maintenance?.Contains(key) == true, Edited: true)
                    : new ReleaseNote(key,
                        titles.TryGetValue(key, out var title) && !string.IsNullOrWhiteSpace(title) ? title.Trim() : key,
                        maintenance?.Contains(key) == true))
                .OrderBy(note => note.Maintenance).ToArray());
}
