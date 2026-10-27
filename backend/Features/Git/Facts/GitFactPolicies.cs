namespace AgentStudio.Git;

/// <summary>Why an index run resolves Git's configuration again (AGT-3322).</summary>
internal enum GitConfigCaptureReason
{
    /// <summary>The last capture still holds; the run starts no <c>git config</c>.</summary>
    None,
    /// <summary>No capture is known yet, or the last one failed.</summary>
    NoCapture,
    /// <summary>The watcher saw <c>config</c>, <c>config.worktree</c> or HEAD under an <c>onbranch</c> include.</summary>
    WatcherEvent,
    /// <summary>A contributing file changed or a recorded absent source appeared.</summary>
    SourceChanged,
}

/// <summary>
/// Decides when the Git fact producer pays for <c>git config --list</c>. Before
/// AGT-3322 every run, including every task event, started one. Now only a
/// configuration event or a changed configuration file does, and only a changed
/// resolved signature advances <c>config_generation</c>.
/// </summary>
internal static class GitConfigCapturePolicy
{
    internal static GitConfigCaptureReason Decide(bool hasCapture, bool watcherEvent, bool sourcesUnchanged)
        => !hasCapture ? GitConfigCaptureReason.NoCapture
            : watcherEvent ? GitConfigCaptureReason.WatcherEvent
            : !sourcesUnchanged ? GitConfigCaptureReason.SourceChanged
            : GitConfigCaptureReason.None;

    /// <summary>
    /// The configuration generation after a capture. It moves only when Git
    /// resolves a different configuration; the first capture starts at 1.
    /// </summary>
    internal static long NextGeneration(long current, string? previousSignature, string capturedSignature)
        => current > 0 && string.Equals(previousSignature, capturedSignature, StringComparison.Ordinal)
            ? current
            : current + 1;
}

/// <summary>The inputs a stored generation was computed from, as compared on start.</summary>
internal sealed record GitFactGenerationInput(
    string RefFingerprint,
    long ConfigGeneration,
    string SettingsVersion,
    string TaskInputSignature);

internal enum GitFactRestoreVerdict
{
    /// <summary>Every input matches: the restored generation becomes ready without Git.</summary>
    Ready,
    /// <summary>A stored card could not be read back; recompute.</summary>
    UnreadableCards,
    RefsMoved,
    ConfigChanged,
    SettingsChanged,
    TasksChanged,
}

/// <summary>
/// Cold start (docs/git-facts-architecture, section 03): the restored
/// generation is confirmed from files alone, or the producer runs a normal
/// delta run while readers keep the restored generation.
/// </summary>
internal static class GitFactRestorePolicy
{
    /// <summary>
    /// A stored generation is served only for the same repository and task
    /// roots. A relocated checkout or workspace is a different input.
    /// </summary>
    internal static bool CanServe(string? storedRepositoryRoot, string? storedWorkspaceRoot,
        string repositoryRoot, string workspaceRoot, Func<string, string, bool> samePath)
        => storedRepositoryRoot is not null && storedWorkspaceRoot is not null
            && samePath(storedRepositoryRoot, repositoryRoot)
            && samePath(storedWorkspaceRoot, workspaceRoot);

    internal static GitFactRestoreVerdict Confirm(GitFactGenerationInput stored, GitFactGenerationInput observed,
        bool cardsReadable)
        => !cardsReadable ? GitFactRestoreVerdict.UnreadableCards
            : !string.Equals(stored.RefFingerprint, observed.RefFingerprint, StringComparison.Ordinal)
                ? GitFactRestoreVerdict.RefsMoved
            : stored.ConfigGeneration != observed.ConfigGeneration ? GitFactRestoreVerdict.ConfigChanged
            : !string.Equals(stored.SettingsVersion, observed.SettingsVersion, StringComparison.Ordinal)
                ? GitFactRestoreVerdict.SettingsChanged
            : !string.Equals(stored.TaskInputSignature, observed.TaskInputSignature, StringComparison.Ordinal)
                ? GitFactRestoreVerdict.TasksChanged
            : GitFactRestoreVerdict.Ready;
}

/// <summary>
/// The single-writer write gate of the fencing model: a producer holds a lease
/// per repository with a monotonically increasing epoch, and a publish whose
/// epoch is lower than the stored one is rejected.
/// </summary>
internal static class GitFactPublishPolicy
{
    internal static bool Accepts(long publishingEpoch, long storedEpoch)
        => publishingEpoch > 0 && publishingEpoch >= storedEpoch;
}

/// <summary>
/// Repository health state written to <c>fact_producer_health</c> (decision D7:
/// serve the last good generation with its age, never blank known facts).
/// </summary>
internal static class GitRepositoryHealthPolicy
{
    internal const string Warming = "warming";
    internal const string Restored = "restored";
    internal const string Ready = "ready";
    /// <summary>The last run failed; the last good generation keeps serving.</summary>
    internal const string Degraded = "degraded";
    /// <summary>The last run failed and no generation exists to serve.</summary>
    internal const string Failed = "failed";
    /// <summary>A producer with a newer epoch owns the store.</summary>
    internal const string Fenced = "fenced";

    internal static string State(bool hasGeneration, bool restored, string? failureReason, bool fenced)
        => fenced ? Fenced
            : failureReason is not null ? hasGeneration ? Degraded : Failed
            : !hasGeneration ? Warming
            : restored ? Restored
            : Ready;
}
