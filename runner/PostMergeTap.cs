using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Node TAP points and their first diagnostic, read before truncating command output.</summary>
internal sealed partial class PostMergeTap(string output)
{
    private sealed record Point(string Name, string Result, int Document, string Number,
        bool HasFailedChild, string? Error = null, string? Location = null, string? FailureType = null)
    {
        public bool IsFailureSummary => FailureType switch
        {
            "subtestsFailed" => true,
            null => HasFailedChild && (Error is null || SubtestCountError().IsMatch(Error)),
            _ => false,
        };
    }
    private sealed record Subtest(int Indent, string Name)
    {
        public bool HasFailedChild { get; set; }
    }
    internal sealed record Test(string Name, string Initial, string Repeat, string? Error);

    [GeneratedRegex(@"^\s*(?<result>not ok|ok)\s+(?<number>\d+)\s+-\s+(?<name>.+?)\s*$")]
    private static partial Regex TestPoint();
    [GeneratedRegex(@"\s+#\s*(?:SKIP|TODO)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Directive();
    [GeneratedRegex(@"^[0-9]+ subtests? failed$")]
    private static partial Regex SubtestCountError();

    private readonly Point[] _points = Read(output).Where(point => !point.IsFailureSummary).ToArray();
    internal bool HasFailures => _points.Any(point => point.Result == PostMergeTestResults.Fail);

    // Node emits a source location on failure, but normally omits it for passes and skips.
    // A duplicate title without a unique location cannot establish a cross-attempt result.
    internal IReadOnlyList<Test> Compare(PostMergeTap repeat, string checkout)
    {
        var initialByName = _points.ToLookup(point => point.Name);
        var repeatByName = repeat._points.ToLookup(point => point.Name);
        var checkoutPrefix = Path.TrimEndingDirectorySeparator(checkout) + Path.DirectorySeparatorChar;
        var tests = new List<Test>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in _points.Concat(repeat._points).Where(point => point.Result == PostMergeTestResults.Fail)
                     .Select(point => point.Name).Distinct(StringComparer.Ordinal))
        {
            var initial = initialByName[name].ToArray();
            var retried = repeatByName[name].ToArray();
            if (initial.Length <= 1 && retried.Length <= 1 && Compatible(initial.SingleOrDefault(), retried.SingleOrDefault()))
            {
                Add(name, initial.SingleOrDefault(), retried.SingleOrDefault());
                continue;
            }

            var firstLocations = initial.ToLookup(point => point.Location);
            var retryLocations = retried.ToLookup(point => point.Location);
            foreach (var location in firstLocations.Concat(retryLocations).Select(group => group.Key).Distinct())
            {
                var first = firstLocations[location].ToArray();
                var second = retryLocations[location].ToArray();
                var source = location?.StartsWith(checkoutPrefix, StringComparison.Ordinal) == true
                    ? location[checkoutPrefix.Length..] : location;
                var label = source is null ? name : $"{name} [{source}]";
                // Skipped and passing namesakes can come from the same parameterized declaration.
                // Without their locations, even two failures at that declaration may be different cases.
                if (location is not null && first.Length <= 1 && second.Length <= 1
                    && (first.Length == 0 || second.Length == 0 || !firstLocations.Contains(null) && !retryLocations.Contains(null)))
                {
                    Add(label, first.SingleOrDefault(), second.SingleOrDefault());
                    continue;
                }

                for (var i = 0; i < first.Length; i++)
                    Add($"{label} [initial {i + 1}]", first[i], null);
                for (var i = 0; i < second.Length; i++)
                    Add($"{label} [repeat {i + 1}]", null, second[i]);
            }
        }
        return tests;

        void Add(string label, Point? first, Point? second)
        {
            if (first?.Result != PostMergeTestResults.Fail && second?.Result != PostMergeTestResults.Fail) return;
            // Receipt test IDs allow 256 characters. Keep generated labels distinct from literal titles.
            var unique = ReceiptName(label);
            for (var suffix = 2; !usedNames.Add(unique); suffix++) unique = ReceiptName($"{label} #{suffix}");
            tests.Add(new(unique, first?.Result ?? PostMergeTestResults.Unknown,
                second?.Result ?? PostMergeTestResults.Unknown, second?.Error ?? first?.Error));
        }
    }

    private static string ReceiptName(string label)
    {
        const int maxLength = 256;
        if (label.Length <= maxLength) return label;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(label)))[..16];
        return $"{label[..(maxLength - digest.Length - 3)]} [{digest}]";
    }

    private static bool Compatible(Point? initial, Point? repeat)
    {
        if (initial is null || repeat is null) return true;
        if (initial.Location is not null && repeat.Location is not null) return initial.Location == repeat.Location;
        return initial.Document == repeat.Document && initial.Number == repeat.Number;
    }

    private static IEnumerable<Point> Read(string output)
    {
        using var reader = new StringReader(output);
        var parents = new List<Subtest>();
        Point? point = null;
        var document = 0;
        int? diagnosticIndent = null;
        int? pointIndent = null;
        while (reader.ReadLine() is { } line)
        {
            var field = line.TrimStart();
            var indent = line.Length - field.Length;
            if (diagnosticIndent is { } depth)
            {
                if (indent == depth && field == "...") diagnosticIndent = null;
                if (indent == depth && point is { Result: PostMergeTestResults.Fail }
                    && field.StartsWith("failureType:", StringComparison.Ordinal))
                {
                    var failureType = field["failureType:".Length..].Trim().Trim('\'', '"');
                    if (failureType.Length > 0) point = point with { FailureType = failureType };
                }
                if (indent == depth && point is not null && field.StartsWith("location:", StringComparison.Ordinal))
                {
                    var location = field[9..].Trim().Trim('\'', '"');
                    if (!string.IsNullOrWhiteSpace(location)) point = point with { Location = location };
                }
                if (indent == depth && point is { Error: null } && field.StartsWith("error:", StringComparison.Ordinal))
                {
                    var message = field[6..].Trim();
                    if (message is "|" or "|-" or "|+" or ">" or ">-" or ">+")
                        message = reader.ReadLine()?.Trim() ?? "";
                    else if (message.Length >= 2 && message[0] == message[^1])
                    {
                        if (message[0] == '\'') message = message[1..^1].Replace("''", "'", StringComparison.Ordinal);
                        else if (message[0] == '"') message = message[1..^1];
                    }
                    if (message.Length > 0) point = point with { Error = message[..Math.Min(message.Length, 2000)] };
                }
                continue;
            }
            if (field == "---" && indent > pointIndent)
            {
                diagnosticIndent = indent;
                continue;
            }
            if (field.StartsWith("# Subtest: ", StringComparison.Ordinal))
            {
                parents.RemoveAll(parent => parent.Indent >= indent);
                parents.Add(new(indent, field[11..]));
                continue;
            }
            if (field.StartsWith("TAP version ", StringComparison.Ordinal))
            {
                parents.Clear();
                document++;
            }
            var match = TestPoint().Match(line);
            if (!match.Success) continue;
            if (point is not null) yield return point;
            pointIndent = indent;
            var title = match.Groups["name"].Value;
            var directive = Directive().Match(title);
            var name = directive.Success ? title[..directive.Index] : title;
            var result = directive.Success ? PostMergeTestResults.Unknown :
                match.Groups["result"].Value == "ok" ? PostMergeTestResults.Pass : PostMergeTestResults.Fail;
            var failed = result == PostMergeTestResults.Fail;
            var hasFailedChild = failed && parents.Any(parent =>
                parent.Indent == indent && parent.Name == name && parent.HasFailedChild);
            parents.RemoveAll(parent => parent.Indent >= indent);
            if (failed && parents.Count > 0) parents[^1].HasFailedChild = true;
            point = new(string.Join(" > ", parents.Select(parent => parent.Name).Append(name)), result,
                document, match.Groups["number"].Value, hasFailedChild);
        }
        if (point is not null) yield return point;
    }
}
