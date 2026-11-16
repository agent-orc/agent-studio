namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Delimits the published Dossier snapshot inside a frozen concept-fit prompt.
/// The plan builder bounds the snapshot itself; the review runner keeps the
/// delimited span whole and applies its authored-context budget only to the
/// text around it, so the aspect never judges a Dossier with a cut middle.
/// </summary>
public static class ConceptDossierSnapshotSpan
{
    public const string Start = "<!-- concept-dossier-snapshot:start -->";
    public const string End = "<!-- concept-dossier-snapshot:end -->";

    /// <summary>Upper bound the runner protects; the snapshot budget plus file headings.</summary>
    public const int MaximumCharacters = 540_000;

    public static string Wrap(string snapshot) => Start + "\n" + snapshot.TrimEnd() + "\n" + End;

    /// <summary>Splits a prompt around its first complete snapshot span.</summary>
    public static bool TrySplit(string prompt, out string before, out string span, out string after)
    {
        before = prompt;
        span = string.Empty;
        after = string.Empty;
        var start = prompt.IndexOf(Start, StringComparison.Ordinal);
        if (start < 0) return false;
        var end = prompt.IndexOf(End, start + Start.Length, StringComparison.Ordinal);
        if (end < 0) return false;
        end += End.Length;
        before = prompt[..start];
        span = prompt[start..end];
        after = prompt[end..];
        return true;
    }
}
