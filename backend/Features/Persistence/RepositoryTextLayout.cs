using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Persistence;

/// <summary>
/// Line-ending convention and final newline of a text file in a project
/// repository. A managed rewrite of a tracked file must keep both, otherwise
/// every line of the diff changes: the serializer emits
/// <see cref="Environment.NewLine"/> (CRLF on Windows) and no final newline,
/// which turned a one-key Dossier descriptor edit into a whole-file rewrite
/// (AGT-3217). A new or empty file gets LF with a final newline.
/// </summary>
public readonly record struct RepositoryTextLayout(string NewLine, bool FinalNewline)
{
    public static RepositoryTextLayout Default { get; } = new("\n", true);

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    /// <summary>
    /// Detects the dominant line ending and the final newline. A file with
    /// more CRLF than bare LF breaks is CRLF.
    /// </summary>
    public static RepositoryTextLayout Detect(string? existing)
    {
        if (string.IsNullOrEmpty(existing)) return Default;
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < existing.Length; i++)
        {
            if (existing[i] != '\n') continue;
            if (i > 0 && existing[i - 1] == '\r') crlf++;
            else lf++;
        }
        if (crlf + lf == 0) return Default with { FinalNewline = false };
        return new(crlf > lf ? "\r\n" : "\n", existing.EndsWith('\n'));
    }

    /// <summary>Reads the layout of an existing file; a missing file gets <see cref="Default"/>.</summary>
    public static RepositoryTextLayout ForFile(string path) =>
        File.Exists(path) ? Detect(File.ReadAllText(path)) : Default;

    /// <summary>Indented JSON for <paramref name="node"/> in the layout of the file at <paramref name="path"/>.</summary>
    public static string IndentedJsonFor(string path, JsonNode node) =>
        ForFile(path).Apply(node.ToJsonString(IndentedJson));

    /// <summary>Re-emits freshly generated <paramref name="content"/> in this layout.</summary>
    public string Apply(string content)
    {
        var body = content.ReplaceLineEndings("\n").TrimEnd('\n');
        if (FinalNewline) body += "\n";
        return NewLine == "\n" ? body : body.Replace("\n", NewLine, StringComparison.Ordinal);
    }
}
