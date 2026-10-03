namespace AgentStudio.Shared;

/// <summary>
/// Read-time projection of a card that waits on a cause card (AGT-W57). The
/// board renders it as "waiting for &lt;key&gt;" in the card's own lane instead
/// of an escalation.
/// </summary>
public sealed record CauseWaitStatus(
    string CauseKey,
    string Fingerprint,
    string FailureClass,
    DateTime Since,
    string Reason);
