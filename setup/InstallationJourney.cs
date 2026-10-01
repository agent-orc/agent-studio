namespace AgentStudio.Setup;

/// <summary>
/// The four journeys of the deployment ladder (Dossier AGT-W63, D1 option A):
/// install one box, join a runner host, attach Studio, relocate authority.
/// A workstation is a placement of the one-box journey, not a separate product.
/// </summary>
internal enum InstallationJourney
{
    OneBox,
    JoinHost,
    AttachStudio,
    RelocateAuthority,
}

/// <summary>One fact the administrator supplies or confirms for a journey.</summary>
internal sealed record JourneyFact(string Name, string Meaning);

internal sealed record JourneySteps(
    InstallationJourney Journey,
    string Mode,
    IReadOnlyList<JourneyFact> Facts,
    IReadOnlyList<string> Checkpoints);

internal static class JourneyPolicy
{
    public static InstallationJourney Parse(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "one-box" or "install" or "workstation" => InstallationJourney.OneBox,
            "join" or "join-host" => InstallationJourney.JoinHost,
            "attach" or "attach-studio" => InstallationJourney.AttachStudio,
            "relocate" or "relocate-authority" => InstallationJourney.RelocateAuthority,
            _ => throw new ArgumentException(
                "--journey must be one-box, join-host, attach-studio, or relocate-authority."),
        };

    public static string Name(InstallationJourney journey)
        => journey switch
        {
            InstallationJourney.OneBox => "one-box",
            InstallationJourney.JoinHost => "join-host",
            InstallationJourney.AttachStudio => "attach-studio",
            _ => "relocate-authority",
        };

    /// <summary>The installer mode that carries out each journey.</summary>
    public static string ModeFor(InstallationJourney journey)
        => journey switch
        {
            InstallationJourney.OneBox => "studio",
            InstallationJourney.JoinHost => "agent-host",
            InstallationJourney.AttachStudio => "connector",
            _ => "control-plane",
        };

    /// <summary>The journey an explicit or recorded mode belongs to.</summary>
    public static InstallationJourney ForMode(string mode)
        => mode switch
        {
            "agent-host" => InstallationJourney.JoinHost,
            "connector" => InstallationJourney.AttachStudio,
            "control-plane" => InstallationJourney.RelocateAuthority,
            _ => InstallationJourney.OneBox,
        };

    private static readonly JourneyFact Prerequisites = new("Prerequisites",
        "Run preflight; every failed check names its recovery action.");
    private static readonly JourneyFact Storage = new("Storage",
        "Free space for repositories, run worktrees and backups, plus an off-host recovery copy.");
    private static readonly JourneyFact ReleasePin = new("Release pin",
        "One verified X.Y.Z release for every component; latest is never accepted.");
    private static readonly JourneyFact HumanIdentity = new("Human identity",
        "A personal operator account signs in through the browser edge; service tokens never enter a browser.");
    private static readonly JourneyFact ProjectOrigin = new("Project origin",
        "The canonical Git repository registered in the Task Server project registry.");
    private static readonly JourneyFact FirstRunner = new("First runner",
        "One runner-host identity with probed coding and review capabilities.");
    private static readonly JourneyFact Budgets = new("Budgets",
        "A finite coding and review slot budget per host; budgets are never summed twice.");

    public static JourneySteps Steps(InstallationJourney journey)
        => journey switch
        {
            InstallationJourney.OneBox => new(journey, ModeFor(journey),
                [Prerequisites, Storage, ReleasePin, HumanIdentity, ProjectOrigin, FirstRunner, Budgets],
                ["preflight", "release-verified", "services-healthy", "identity-bootstrapped",
                 "authenticated-canary", "recovery-checkpoint"]),
            InstallationJourney.JoinHost => new(journey, ModeFor(journey),
                [Prerequisites, Storage, ReleasePin,
                 new("Join token", "A protected, single-purpose join token file from the existing authority."),
                 new("Project origin", "The same project id and repository; joining a host is not project migration."),
                 FirstRunner, Budgets],
                ["preflight", "release-verified", "host-enrolled", "repository-proof", "authenticated-canary"]),
            InstallationJourney.AttachStudio => new(journey, ModeFor(journey),
                [Prerequisites, ReleasePin, HumanIdentity,
                 new("Authority origin", "The private HTTPS Task Server origin; no silent local fallback."),
                 new("Studio token", "A protected token file; Studio observes and submits requests only.")],
                ["preflight", "release-verified", "authority-reachable", "identity-confirmed"]),
            _ => new(journey, ModeFor(journey),
                [Prerequisites, Storage, ReleasePin,
                 new("Verified recovery set", "A verified, empty-target-rehearsed recovery checkpoint of the current authority."),
                 new("Freeze", "The current authority is in Maintenance with resolved attempts."),
                 new("Connectivity", "WireGuard peers, private DNS and TLS trust for the new authority origin."),
                 ProjectOrigin, Budgets],
                ["preflight", "recovery-verified", "authority-frozen", "control-plane-installed",
                 "workspace-restored", "identity-matched", "authenticated-canary"]),
        };

    /// <summary>
    /// The relocation gate (D7/D8 option A): authority moves only from a verified
    /// recovery checkpoint and an explicit freeze, never by creating a second
    /// installation identity.
    /// </summary>
    public static string? RelocationBlocker(string? recoveryCheckpoint, bool authorityFrozen)
    {
        if (string.IsNullOrWhiteSpace(recoveryCheckpoint))
            return "Relocating authority requires --recovery-checkpoint with a verified backup id. " +
                   "Create and verify one with POST /api/v1/management/backups, then rehearse its restore " +
                   "into an empty target before migration.";
        if (!authorityFrozen)
            return "Relocating authority requires --authority-frozen after the current Task Server " +
                   "is in Maintenance with every attempt resolved.";
        return null;
    }
}
