using System.Text;

namespace AgentStudio.Git;

/// <summary>One conflict hunk that was resolved by keeping both sides.</summary>
/// <param name="Path">Repository path of the file.</param>
/// <param name="Line">First line of the kept block in the resolved file (1-based).</param>
/// <param name="OursLines">Lines the integration side added at this place.</param>
/// <param name="TheirsLines">Lines the delivery added at this place.</param>
public sealed record UnionResolvedHunk(string Path, int Line, int OursLines, int TheirsLines);

/// <summary>The outcome of resolving the conflicts of one file.</summary>
public sealed record AddAddUnionResult(
    bool Resolved,
    string? Content,
    IReadOnlyList<UnionResolvedHunk> Hunks,
    string? Refusal);

/// <summary>
/// AGT-3968: resolves a conflicted file only when every conflict hunk is an
/// add/add hunk, that is when both sides only added lines at the same place
/// relative to the merge base. The union keeps both blocks, the integration
/// side first. A hunk where either side changed or removed a base line (two
/// different wraps of one import line, for example) is refused, so the whole
/// file goes to the agent as before.
/// <para>The input is <c>git merge-file --diff3</c> output written with
/// <see cref="MarkerSize"/>-character markers, so a conflict marker cannot be
/// confused with content.</para>
/// </summary>
public static class AddAddUnionResolution
{
    public const int MarkerSize = 31;

    private static readonly string Start = new('<', MarkerSize);
    private static readonly string Base = new('|', MarkerSize);
    private static readonly string Separator = new('=', MarkerSize);
    private static readonly string End = new('>', MarkerSize);

    private enum Section { Outside, Ours, Base, Theirs }

    public static AddAddUnionResult Resolve(string path, string diff3Text)
    {
        ArgumentNullException.ThrowIfNull(diff3Text);
        var output = new StringBuilder(diff3Text.Length);
        var hunks = new List<UnionResolvedHunk>();
        var ours = new List<string>();
        var theirs = new List<string>();
        var baseLines = 0;
        var outputLine = 1;
        var section = Section.Outside;
        foreach (var line in Lines(diff3Text))
        {
            var text = line.TrimEnd('\r', '\n');
            switch (section)
            {
                case Section.Outside when IsMarker(text, Start):
                    section = Section.Ours;
                    ours.Clear();
                    theirs.Clear();
                    baseLines = 0;
                    continue;
                case Section.Outside:
                    output.Append(line);
                    if (line.EndsWith('\n')) outputLine++;
                    continue;
                case Section.Ours when IsMarker(text, Base):
                    section = Section.Base;
                    continue;
                case Section.Ours when text == Separator:
                    return Refuse(path, outputLine, "the conflict has no merge-base section");
                case Section.Ours:
                    ours.Add(line);
                    continue;
                case Section.Base when text == Separator:
                    if (baseLines > 0)
                        return Refuse(path, outputLine,
                            $"both sides changed {baseLines} merge-base {(baseLines == 1 ? "line" : "lines")}");
                    section = Section.Theirs;
                    continue;
                case Section.Base:
                    baseLines++;
                    continue;
                case Section.Theirs when IsMarker(text, End):
                    if (ours.Count == 0 || theirs.Count == 0)
                        return Refuse(path, outputLine, "one side of the conflict is empty");
                    hunks.Add(new UnionResolvedHunk(path, outputLine, ours.Count, theirs.Count));
                    foreach (var kept in ours.Concat(theirs))
                    {
                        output.Append(kept);
                        if (!kept.EndsWith('\n')) output.Append('\n');
                        outputLine++;
                    }
                    section = Section.Outside;
                    continue;
                case Section.Theirs:
                    theirs.Add(line);
                    continue;
            }
        }
        if (section != Section.Outside)
            return Refuse(path, outputLine, "a conflict marker is not closed");
        if (hunks.Count == 0)
            return Refuse(path, 1, "the file has no conflict hunk");
        return new AddAddUnionResult(true, output.ToString(), hunks, null);
    }

    private static bool IsMarker(string text, string marker)
        => text == marker || text.StartsWith(marker + " ", StringComparison.Ordinal);

    private static AddAddUnionResult Refuse(string path, int line, string reason)
        => new(false, null, [], $"{path}:{line}: {reason}");

    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return text[start..];
                yield break;
            }
            yield return text[start..(newline + 1)];
            start = newline + 1;
        }
    }
}
