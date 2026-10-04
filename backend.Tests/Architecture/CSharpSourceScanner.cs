using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Lexical C# source helpers for the analyzer-style guard tests
/// (<see cref="PromptLineEndingGuardTests"/>, <see cref="TestClockGuardTests"/>).
/// Masking keeps offsets and line breaks, so a match in masked text maps to the
/// same line of the original file.
/// </summary>
internal static partial class CSharpSourceScanner
{
    /// <summary>A method or local function: its name, declaration line and body span.</summary>
    internal sealed record MethodSpan(string Name, int Line, int Start, int End);

    /// <summary>Reads a source file with CRLF folded to LF, so line numbers are host-independent.</summary>
    public static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Blanks comments only; string literals stay visible.</summary>
    public static string MaskComments(string source) => Mask(source, maskLiterals: false);

    /// <summary>
    /// Blanks comments and the text of string and char literals. Interpolation
    /// holes stay visible, so <c>$"{Environment.NewLine}"</c> is still code.
    /// </summary>
    public static string MaskCommentsAndLiterals(string source) => Mask(source, maskLiterals: true);

    public static int LineAt(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    public static IEnumerable<MethodSpan> Methods(string code)
    {
        foreach (Match match in MethodDeclaration().Matches(code))
        {
            var name = match.Groups["name"].Value;
            if (Keywords.Contains(name)) continue;
            var close = FindBalancedEnd(code, match.Index + match.Length - 1, '(', ')');
            if (close < 0) continue;
            var index = SkipWhitespace(code, close + 1);
            if (code.AsSpan(index).StartsWith("where"))
                while (index < code.Length && code[index] is not ('{' or ';') && !code.AsSpan(index).StartsWith("=>")) index++;

            int end;
            if (index < code.Length && code[index] == '{')
                end = FindBalancedEnd(code, index, '{', '}');
            else if (code.AsSpan(index).StartsWith("=>"))
                end = ExpressionBodyEnd(code, index);
            else
                continue;
            if (end < 0) continue;
            yield return new MethodSpan(name, LineAt(code, match.Index), match.Index, end + 1);
        }
    }

    public static IEnumerable<string> SourceFiles(string root, string relativeDirectory)
    {
        var directory = Path.Combine(root, relativeDirectory);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .OrderBy(file => file, StringComparer.Ordinal);
    }

    public static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    public static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
                current = Path.GetDirectoryName(current);
            }
        }
        throw new InvalidOperationException(
            "agent-taskboard.sln not found above the guard source file or the test base directory.");
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "using", "return", "catch", "lock", "new", "await",
        "nameof", "when", "fixed", "else", "throw", "yield", "case", "var", "typeof", "sizeof", "default",
    };

    private static string Mask(string source, bool maskLiterals)
    {
        var chars = source.ToCharArray();
        var index = 0;
        while (index < chars.Length)
        {
            if (At(source, index, "//"))
            {
                var end = source.IndexOf('\n', index + 2);
                end = end < 0 ? source.Length : end;
                Blank(chars, index, end);
                index = end;
            }
            else if (At(source, index, "/*"))
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                Blank(chars, index, end);
                index = end;
            }
            else if (source[index] == '"')
            {
                index = StringLiteral(source, chars, index, maskLiterals);
            }
            else if (source[index] == '\'')
            {
                var end = index + 1;
                while (end < source.Length && source[end] is not ('\'' or '\n'))
                    end += source[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, source.Length);
                if (maskLiterals) Blank(chars, index, end);
                index = end;
            }
            else
            {
                index++;
            }
        }
        return new string(chars);
    }

    /// <summary>Scans one string literal starting at its first quote and returns the index after it.</summary>
    private static int StringLiteral(string source, char[] chars, int quote, bool maskLiterals)
    {
        var dollars = 0;
        var verbatim = false;
        for (var prefix = quote - 1; prefix >= 0 && source[prefix] is '$' or '@'; prefix--)
        {
            if (source[prefix] == '$') dollars++;
            else verbatim = true;
        }

        var run = 0;
        while (quote + run < source.Length && source[quote + run] == '"') run++;
        if (run >= 3)
        {
            var close = source.IndexOf(new string('"', run), quote + run, StringComparison.Ordinal);
            var end = close < 0 ? source.Length : close + run;
            if (maskLiterals) BlankOutsideHoles(source, chars, quote, end, dollars);
            return end;
        }

        var textStart = quote;
        var index = quote + 1;
        while (index < source.Length)
        {
            var current = source[index];
            if (dollars > 0 && current == '{')
            {
                if (At(source, index, "{{")) { index += 2; continue; }
                if (maskLiterals) Blank(chars, textStart, index + 1);
                index = InterpolationHoleEnd(source, chars, index, maskLiterals);
                textStart = index - 1;
                continue;
            }
            if (!verbatim && current == '\\') { index += 2; continue; }
            if (current == '"')
            {
                if (verbatim && At(source, index, "\"\"")) { index += 2; continue; }
                index++;
                break;
            }
            index++;
        }
        index = Math.Min(index, source.Length);
        if (maskLiterals) Blank(chars, textStart, index);
        return index;
    }

    private static int InterpolationHoleEnd(string source, char[] chars, int open, bool maskLiterals)
    {
        var depth = 0;
        var index = open;
        while (index < source.Length)
        {
            var current = source[index];
            if (current == '"') { index = StringLiteral(source, chars, index, maskLiterals); continue; }
            if (current == '{') depth++;
            else if (current == '}' && --depth == 0) return index + 1;
            index++;
        }
        return source.Length;
    }

    private static void BlankOutsideHoles(string source, char[] chars, int start, int end, int dollars)
    {
        if (dollars == 0) { Blank(chars, start, end); return; }
        var open = new string('{', dollars);
        var textStart = start;
        var index = start;
        while (index < end)
        {
            if (At(source, index, open) && !At(source, index, open + "{"))
            {
                Blank(chars, textStart, index + dollars);
                var depth = 0;
                var hole = index + dollars - 1;
                for (; hole < end; hole++)
                {
                    if (source[hole] == '{') depth++;
                    else if (source[hole] == '}' && --depth == 0) break;
                }
                index = hole;
                textStart = hole;
            }
            index++;
        }
        Blank(chars, textStart, end);
    }

    private static int ExpressionBodyEnd(string code, int arrow)
    {
        var depth = 0;
        for (var index = arrow; index < code.Length; index++)
        {
            if (code[index] is '(' or '{' or '[') depth++;
            else if (code[index] is ')' or '}' or ']') depth--;
            else if (code[index] == ';' && depth <= 0) return index;
        }
        return -1;
    }

    private static int FindBalancedEnd(string code, int open, char opening, char closing)
    {
        var depth = 0;
        for (var index = open; index < code.Length; index++)
        {
            if (code[index] == opening) depth++;
            else if (code[index] == closing && --depth == 0) return index;
        }
        return -1;
    }

    private static int SkipWhitespace(string code, int from)
    {
        while (from < code.Length && char.IsWhiteSpace(code[from])) from++;
        return from;
    }

    private static bool At(string source, int index, string token)
        => index + token.Length <= source.Length && string.CompareOrdinal(source, index, token, 0, token.Length) == 0;

    private static void Blank(char[] chars, int start, int end)
    {
        for (var index = Math.Max(start, 0); index < Math.Min(end, chars.Length); index++)
            if (chars[index] != '\n') chars[index] = ' ';
    }

    [GeneratedRegex(
        @"^[ \t]*(?:(?:public|private|internal|protected|static|async|override|sealed|virtual|partial|new|unsafe|extern)\s+)*"
        + @"(?:\([^;{}]*?\)|[\w.]+(?:\s*<[^;{}()]*>)?(?:\[\])*)\??\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^;{}()]*>)?\s*\(",
        RegexOptions.Multiline)]
    private static partial Regex MethodDeclaration();
}
