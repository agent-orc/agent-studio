namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Recognises verification commands that died because the host environment the
/// worker was started in disappeared underneath it, not because the reviewed
/// change is broken.
/// </summary>
/// <remarks>
/// The observed cause is a runner unit that owns a lifecycle-bound private
/// <c>/tmp</c>. A daemon restart unmounts that namespace while the detached
/// worker keeps the deleted mount, so MSBuild can no longer bind its node pipe
/// under <c>/tmp/MSBuild&lt;pid&gt;</c> and NuGet can no longer create its
/// migrations mutex directory. The command exits non-zero without producing a
/// single test result, which previously graded as a new test failure and sent
/// the card to human review. Such a run is always infrastructure.
/// </remarks>
public static class ReviewHostEnvironmentFailurePolicy
{
    /// <summary>Failure classification reported for a lost host temp namespace.</summary>
    public const string Classification = "HostTempUnavailable";

    /// <summary>
    /// Sentinel that <c>RemoteReviewWorkspace</c> substitutes when a failing
    /// command produced no parseable test result at all.
    /// </summary>
    public const string UnparsedFailurePrefix = "<unparsed failure";

    // Each entry is a conjunction: every needle must appear in the same command
    // output. Keeping the socket error paired with its errno spelling stops a
    // product test that legitimately fails to bind a port from matching.
    private static readonly string[][] Signatures =
    [
        // MSB1025: an internal failure occurred while running MSBuild. Emitted
        // when the node pipe under the vanished /tmp cannot be created. Left as
        // a lone needle on purpose: MSBuild reporting that IT crashed is never a
        // statement about the reviewed code, so infrastructure is the honest
        // grade even for the other causes it covers, such as an out-of-memory
        // build node. The cost is bounded by the infrastructure retry budget.
        ["MSB1025"],
        // System.Net.Sockets.SocketException (99): Cannot assign requested address
        ["SocketException (99)", "Cannot assign requested address"],
        // mkdtemp("/tmp/.dotnet.XXXXXX") == nullptr; errno == ENOENT
        ["mkdtemp", "ENOENT"],
    ];

    /// <summary>
    /// True when <paramref name="output"/> carries a host-environment signature.
    /// </summary>
    public static bool MatchesSignature(string? output)
        => !string.IsNullOrEmpty(output)
           && Signatures.Any(needles => needles.All(needle =>
               output.Contains(needle, StringComparison.Ordinal)));

    /// <summary>
    /// True when <paramref name="failures"/> proves that no test result was
    /// parsed at all.
    /// </summary>
    /// <remarks>
    /// An EMPTY list is not that proof and must stay false. It means the parser
    /// ran and found no new failure, which is the ordinary shape of a candidate
    /// command whose failures are all pre-existing. Treating it as "nothing
    /// parsed" would let a repository with a pre-existing test that prints one
    /// of the signatures flip a passing review to an infrastructure failure on
    /// every attempt. A null list carries no parse record at all, which happens
    /// when the command was not compared to a baseline.
    /// </remarks>
    public static bool NoParsedTestResult(IReadOnlyList<string>? failures)
        => failures is null
           || (failures.Count > 0
               && failures.All(failure =>
                   failure.StartsWith(UnparsedFailurePrefix, StringComparison.Ordinal)));

    /// <summary>
    /// True when a reported verification command failed with a host-environment
    /// signature and without a single parsed test result.
    /// </summary>
    /// <remarks>
    /// Deliberately scoped to the verification phase. A preparation command that
    /// hits the same signature already settles as
    /// <c>ReviewInfra / PreparationFailed</c> and retries with a rebuild, which
    /// is the correct remediation for a broken preparation; reclassifying it
    /// here would only trade an accurate retry plan for a more precise label.
    /// </remarks>
    public static bool IsHostEnvironmentLoss(
        IReadOnlyList<ReviewCommandEvidenceDto> commands,
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts)
        => commands.Any(command =>
            command.Phase == "verification"
            && command.ExitCode != 0
            && NoParsedTestResult(command.NewFailures)
            && MatchesSignature(ReviewCommandOutputEvidence.Decode(artifacts, command)));
}

/// <summary>
/// Reads the captured stdout and stderr of a reported command back out of the
/// inline artifact evidence.
/// </summary>
internal static class ReviewCommandOutputEvidence
{
    public static string Decode(
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts,
        ReviewCommandEvidenceDto command)
    {
        var text = new System.Text.StringBuilder();
        foreach (var artifact in artifacts)
        {
            if (artifact.ContentBase64 is null
                || (!string.Equals(artifact.Sha256, command.StdoutSha256, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(artifact.Sha256, command.StderrSha256, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                text.Append(System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(artifact.ContentBase64)));
                text.Append('\n');
            }
            catch (FormatException)
            {
                // Malformed inline evidence is handled by ArtifactEvidenceInvalid.
            }
        }
        return text.ToString();
    }
}
