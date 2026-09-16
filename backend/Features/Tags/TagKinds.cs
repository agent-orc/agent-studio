namespace AgentStudio.Tags;

/// <summary>
/// The two tag kinds of the classification vocabulary (Dossier AGT-W55 §5).
/// An <see cref="Area"/> tag names the bounded part of the application a card,
/// Dossier, or wiki article belongs to and owns a glossary; a
/// <see cref="Facet"/> tag names a cross-cutting aspect (decision, incident,
/// security review, ...). Unknown or missing values read as a facet so a
/// registry row written before the field existed stays usable.
/// </summary>
public static class TagKinds
{
    public const string Area = "area";
    public const string Facet = "facet";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Area, StringComparison.OrdinalIgnoreCase)
            ? Area
            : Facet;
}
