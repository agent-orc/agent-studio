using System.Text.RegularExpressions;

namespace AgentRunner.RunTargets;

/// <summary>Small, masked Docker log tail suitable for a host observation.</summary>
public static partial class RunTargetCrashOutput
{
    public const int MaxLines = 20;
    public const int MaxLineLength = 400;

    [GeneratedRegex(@"(?i)\b(password|passwd|secret|token|api[_-]?key|authorization)\s*[:=]\s*([^\s,;]+)")]
    private static partial Regex Credential();

    public static IReadOnlyList<string> Sanitize(string output, IEnumerable<string>? secretValues = null)
    {
        foreach (var secret in (secretValues ?? []).SelectMany(Fragments).Where(value => value.Length >= 4)
                     .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
            output = output.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return output.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0).TakeLast(MaxLines)
            .Select(line => Credential().Replace(line, "$1=[redacted]"))
            .Select(line => new string(line.Where(ch => !char.IsControl(ch) || ch == '\t').Take(MaxLineLength).ToArray()))
            .ToArray();
    }

    private static IEnumerable<string> Fragments(string value)
    {
        yield return value;
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) yield return line;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Contains(':'))
            yield return Uri.UnescapeDataString(uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..]);
    }
}
