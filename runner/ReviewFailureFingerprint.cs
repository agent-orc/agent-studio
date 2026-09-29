using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Normalised identity of a failed deterministic review command (AGT-2916).
/// The diagnosis contract compares the first run against the clean repeat,
/// the baseline and the Task Server history by this value, so it must not
/// carry anything that differs between two runs of the same failure.
/// </summary>
internal static class ReviewFailureFingerprint
{
    internal const string Prefix = "review:";

    /// <summary>
    /// Parsed test names identify a failure on their own. Without them (build,
    /// lint, a preparation step) the identity is the command's diagnostic
    /// lines with every attempt-local path and timing removed; when the output
    /// carries no diagnostic line, the exit status is the identity.
    /// </summary>
    internal static string Compute(
        string attemptRoot, string stepId, IReadOnlyList<string> failures, ProcessResult? process)
    {
        var specific = failures.Count > 0
                       && !failures.All(failure => failure.StartsWith("<unparsed failure", StringComparison.Ordinal));
        var identity = specific
            ? string.Join("\n", failures.Select(failure => failure.Trim().ToLowerInvariant())
                .Order(StringComparer.Ordinal))
            : process is null ? string.Empty : OutputIdentity(attemptRoot, process);
        var normalized = stepId.Trim().ToLowerInvariant() + "\n" + identity;
        return Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    internal static string OutputIdentity(string attemptRoot, ProcessResult process)
        => FailureOutputNormalizer.Identity(
            NormalizePaths(attemptRoot, process.StdOut + "\n" + process.StdErr), process.ExitCode);

    /// <summary>
    /// The candidate runs in <c>repository</c>, the clean repeat in
    /// <c>clean-repeat-&lt;hash&gt;-&lt;guid&gt;</c> with its own
    /// <c>baseline-runtime-&lt;hash&gt;</c> home, temp and cache directories.
    /// All of them collapse to the same placeholders.
    /// </summary>
    internal static string NormalizePaths(string attemptRoot, string output)
    {
        if (string.IsNullOrEmpty(attemptRoot)) return output;
        var root = Regex.Escape(attemptRoot.TrimEnd('/', '\\'));
        output = Regex.Replace(output,
            root + @"[/\\](?:baseline-runtime-[0-9a-f]+[/\\])?(home|tmp|cache)(?![\w-])",
            "<$1>", RegexOptions.IgnoreCase);
        output = Regex.Replace(output,
            root + @"[/\\](?:repository|clean-repeat-[0-9a-f]+(?:-[0-9a-f]+)?|baseline-[0-9a-f]+)(?![\w-])",
            "<workspace>", RegexOptions.IgnoreCase);
        output = Regex.Replace(output,
            root + @"[/\\]clean-dependency-cache(?![\w-])",
            "<dependency-cache>", RegexOptions.IgnoreCase);
        return Regex.Replace(output, root, "<attempt>", RegexOptions.IgnoreCase);
    }
}
