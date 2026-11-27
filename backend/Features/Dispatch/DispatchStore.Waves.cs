namespace AgentStudio.Dispatch;

public sealed record DispatchWaveReleaseV1(string DecisionId, string Persona, long Fence,
    IReadOnlyList<string> Cards, bool Complete);

public sealed partial class DispatchStore
{
    public DispatchWaveReleaseV1? ReadWaveRelease(string project, string wave)
    {
        lock (PersonaGate(project))
        {
            try
            {
                var release = Read<DispatchWaveReleaseV1>(Path.Combine(Project(project), "waves", Key(wave) + ".json"));
                return release is { Fence: > 0, Cards: not null } && !string.IsNullOrWhiteSpace(release.DecisionId)
                    && !string.IsNullOrWhiteSpace(release.Persona) && release.Cards.Count > 0
                    && release.Cards.All(card => !string.IsNullOrWhiteSpace(card)) ? release : null;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "Dispatch wave release is unreadable; pickup remains blocked");
                return null;
            }
        }
    }

    public bool WriteWaveRelease(string project, string wave, DispatchWaveReleaseV1 release, DateTimeOffset now)
    {
        lock (PersonaGate(project))
        {
            var lease = ReadLease(project);
            if (lease is null || lease.Fence != release.Fence || lease.Persona != release.Persona || lease.ExpiresAt <= now)
                return false;
            if (release.Complete && ReadWaveRelease(project, wave)?.DecisionId != release.DecisionId) return false;
            Write(Path.Combine(Project(project), "waves", Key(wave) + ".json"), release);
            return true;
        }
    }
}
