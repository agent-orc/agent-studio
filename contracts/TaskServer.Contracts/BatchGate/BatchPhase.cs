using System.Text.Json.Serialization;

namespace AgentStudio.Pipeline;

/// <summary>
/// Phase of one closed batch manifest. The numeric values are persisted in the
/// batch state history and the phase record, so append new phases at the end.
/// The legal transitions are <c>BatchPhaseTransitionPolicy</c> (AGT-3563).
/// <see cref="Unknown"/> is never stored: it is how a manifest without any
/// state reads before the next tick writes its first record.
/// </summary>
public enum BatchPhase { Formed, Assembling, Assembled, Running, Green, Publishing, Published, Red, Paused, Abandoned, Unknown }

/// <summary>
/// What a batch did with one member it claimed, keyed by the member's
/// immutable review attempt in the batch phase record (AGT-3563).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BatchMemberDisposition>))]
public enum BatchMemberDisposition
{
    /// <summary>The batch owns the member; its ownership marker names the batch.</summary>
    Claimed,

    /// <summary>The batch returned the member to the queue without a verdict.</summary>
    Released,

    /// <summary>The batch published the member; it leaves through its batch evidence.</summary>
    Published,

    /// <summary>Replay ejected the member (conflict or no attribution); another route settles it.</summary>
    Ejected,
}
