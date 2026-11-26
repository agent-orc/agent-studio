using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>Source waves wait for a whole-wave plan before entering Ready.</summary>
public static class DispatchWavePolicy
{
    public static int InitialOrder(int requested, int allocated, string? wave)
        => requested != 999 || !string.IsNullOrWhiteSpace(wave) ? requested : allocated;

    public static bool NeedsWave(int sourceCards) => sourceCards > 5;

    public static string Landing(string requested, string? wave, string mode, bool decision)
        => !decision && !string.IsNullOrWhiteSpace(wave) && mode is "shadow" or "live"
            ? TaskStates.Backlog : requested;

    public static DispatchPickupDecision Pickup(DispatchPickupDecision planned, string? wave, bool complete)
        => wave is null || complete ? planned : new(false, "source-wave-releasing", "the complete source wave " + wave);

    public static bool MayRelease(string mode, bool currentLease, bool wholeWavePlanned, bool packerSettled)
        => mode == "live" && currentLease && wholeWavePlanned && packerSettled;
}
