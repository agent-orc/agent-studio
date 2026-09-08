namespace AgentStudio.Retention;

public enum ArtifactClass
{
    Authority,
    Evidence,
    HeavyWorkingData,
    Runtime,
}

public sealed record RetentionRule
{
    public required string Id { get; init; }
    public required ArtifactClass ArtifactClass { get; init; }
    public long HotCapBytesPerFile { get; init; }
    public long HotBudgetBytesPerTask { get; init; }
    public long RefuseAboveBytes { get; init; }
    public int? ArchiveAfterDaysTerminal { get; init; }
    public int? ArchiveTaskAfterDaysTerminal { get; init; }
    public int? DeleteAfterDays { get; init; }
    public int? DeleteArchiveAfterDaysTerminal { get; init; }
    public bool DeleteArchiveEnabled { get; init; }
    public IReadOnlySet<string> NeverArchiveLanes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ProjectRetentionOverride
{
    public IReadOnlyDictionary<ArtifactClass, RetentionRule> Rules { get; init; }
        = new Dictionary<ArtifactClass, RetentionRule>();
}

public sealed record FullBackupRetentionPolicy
{
    public int Daily { get; init; } = 7;
    public int Weekly { get; init; } = 4;
    public int Monthly { get; init; } = 12;

    public void Validate()
    {
        if (Daily is < 1 or > 366 || Weekly is < 1 or > 260 || Monthly is < 1 or > 120)
            throw new InvalidOperationException("Full backup retention must keep 1..366 daily, 1..260 weekly, and 1..120 monthly sets.");
    }
}

public enum ArchiveTargetKind
{
    Local,
    S3,
}

public sealed record ArchiveStoragePolicy
{
    public ArchiveTargetKind ArchiveTarget { get; init; } = ArchiveTargetKind.Local;
    public bool CopyToSecondary { get; init; }
    public bool DeleteLocalAfterVerification { get; init; }
    public int? DeleteArchivedAfterYears { get; init; }

    public void Validate()
    {
        if (ArchiveTarget == ArchiveTargetKind.S3 && CopyToSecondary)
            throw new InvalidOperationException("copyToSecondary is only valid when the primary archive target is local.");
        if (DeleteLocalAfterVerification && ArchiveTarget == ArchiveTargetKind.Local && !CopyToSecondary)
            throw new InvalidOperationException("Deleting the local archive requires an S3 copy.");
        if (DeleteArchivedAfterYears is < 1 or > 100)
            throw new InvalidOperationException("deleteArchivedAfterYears must be between 1 and 100 years, or null.");
    }
}

public sealed record RetentionPolicy
{
    public int Version { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "built-in-default";
    public required IReadOnlyDictionary<ArtifactClass, RetentionRule> WorkspaceDefaults { get; init; }
    public IReadOnlyDictionary<string, ProjectRetentionOverride> ProjectOverrides { get; init; }
        = new Dictionary<string, ProjectRetentionOverride>(StringComparer.OrdinalIgnoreCase);
    public FullBackupRetentionPolicy FullBackups { get; init; } = new();
    public ArchiveStoragePolicy ArchiveStorage { get; init; } = new();

    public RetentionRule RuleFor(string project, ArtifactClass artifactClass)
    {
        if (ProjectOverrides.TryGetValue(project, out var projectOverride)
            && projectOverride.Rules.TryGetValue(artifactClass, out var rule))
        {
            var workspace = WorkspaceDefaults[artifactClass];
            if (!workspace.NeverArchiveLanes.IsSubsetOf(rule.NeverArchiveLanes))
                throw new InvalidOperationException($"Project override '{project}' cannot remove never-archive lanes.");
            ValidateRule(rule);
            return rule;
        }

        var resolved = WorkspaceDefaults[artifactClass];
        ValidateRule(resolved);
        return resolved;
    }

    public void Validate()
    {
        FullBackups.Validate();
        ArchiveStorage.Validate();
        foreach (var artifactClass in Enum.GetValues<ArtifactClass>())
            _ = RuleFor(string.Empty, artifactClass);
        foreach (var project in ProjectOverrides)
            foreach (var artifactClass in project.Value.Rules.Keys)
                _ = RuleFor(project.Key, artifactClass);
    }

    public static RetentionPolicy Default(DateTimeOffset? updatedAt = null) => new()
    {
        UpdatedAt = updatedAt ?? DateTimeOffset.UnixEpoch,
        WorkspaceDefaults = new Dictionary<ArtifactClass, RetentionRule>
        {
            [ArtifactClass.Authority] = new()
            {
                Id = "authority-keep",
                ArtifactClass = ArtifactClass.Authority,
                NeverArchiveLanes = DefaultNeverArchiveLanes(),
            },
            [ArtifactClass.Evidence] = new()
            {
                Id = "evidence-stage-2",
                ArtifactClass = ArtifactClass.Evidence,
                ArchiveTaskAfterDaysTerminal = 180,
                NeverArchiveLanes = DefaultNeverArchiveLanes(),
            },
            [ArtifactClass.HeavyWorkingData] = new()
            {
                Id = "heavy-stage-1",
                ArtifactClass = ArtifactClass.HeavyWorkingData,
                HotCapBytesPerFile = 10L * 1024 * 1024,
                HotBudgetBytesPerTask = 64L * 1024 * 1024,
                RefuseAboveBytes = 50L * 1024 * 1024,
                ArchiveAfterDaysTerminal = 30,
                ArchiveTaskAfterDaysTerminal = 180,
                DeleteArchiveAfterDaysTerminal = 730,
                DeleteArchiveEnabled = false,
                NeverArchiveLanes = DefaultNeverArchiveLanes(),
            },
            [ArtifactClass.Runtime] = new()
            {
                Id = "runtime-delete",
                ArtifactClass = ArtifactClass.Runtime,
                DeleteAfterDays = 30,
                NeverArchiveLanes = DefaultNeverArchiveLanes(),
            },
        },
    };

    private static HashSet<string> DefaultNeverArchiveLanes() => new(StringComparer.OrdinalIgnoreCase)
    {
        "0-backlog", "0-inbox", "0-concept", "1-preparation", "1a-orchestrator-prep",
        "2-ready", "3-progress", "3a-failed-pickup", "3b-code-not-complete",
        "4-auto-review", "4-review", "5-human-review", "5e-escalated",
    };

    private static void ValidateRule(RetentionRule rule)
    {
        foreach (var days in new[]
                 {
                     rule.ArchiveAfterDaysTerminal,
                     rule.ArchiveTaskAfterDaysTerminal,
                     rule.DeleteAfterDays,
                     rule.DeleteArchiveAfterDaysTerminal,
                 })
            if (days is > 0 and < 7)
                throw new InvalidOperationException($"Rule '{rule.Id}' has a retention period below 7 days.");
        if (rule.HotCapBytesPerFile > 0 && rule.HotBudgetBytesPerTask > 0
            && rule.HotBudgetBytesPerTask < rule.HotCapBytesPerFile)
            throw new InvalidOperationException($"Rule '{rule.Id}' has a hot budget below its file cap.");
        if (rule.RefuseAboveBytes > 95L * 1024 * 1024)
            throw new InvalidOperationException($"Rule '{rule.Id}' has a refusal limit above 95 MiB.");
    }
}
