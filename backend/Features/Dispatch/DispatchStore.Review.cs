namespace AgentStudio.Dispatch;

public sealed record DispatchExplanationRating(string EntryId, string Persona, double Score, string Author, DateTimeOffset At);

public sealed partial class DispatchStore
{
    internal DispatchReviewWindow? ReadAlternatingWindow(string project, string key)
    {
        lock (PersonaGate(project)) return Read<DispatchReviewWindow>(Path.Combine(Project(project), "alternating-windows", Key(key) + ".json"));
    }
    internal void WriteAlternatingWindow(string project, string key, DispatchReviewWindow window)
    {
        lock (PersonaGate(project)) Write(Path.Combine(Project(project), "alternating-windows", Key(key) + ".json"), window);
    }
    public IReadOnlyList<DispatchCompetitionEvidence> ReadCompetitionEvidence(string project, DateTimeOffset from, DateTimeOffset to)
    {
        lock (PersonaGate(project)) return Read<DispatchCompetitionEvidence[]>(ReviewPath(project, from, to) + ".competition") ?? [];
    }
    public void WriteCompetitionEvidence(string project, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<DispatchCompetitionEvidence> evidence)
    {
        lock (PersonaGate(project)) Write(ReviewPath(project, from, to) + ".competition", evidence);
    }
    public DispatchReviewWindow? ReadReviewWindow(string project, DateTimeOffset from, DateTimeOffset to)
    {
        lock (PersonaGate(project)) return Read<DispatchReviewWindow>(ReviewPath(project, from, to));
    }
    public void WriteReviewWindow(DispatchReviewWindow window)
    {
        lock (PersonaGate(window.Project)) Write(ReviewPath(window.Project, window.From, window.To), window);
    }
    private string ReviewPath(string project, DateTimeOffset from, DateTimeOffset to)
        => Path.Combine(Project(project), "review", Key($"{from.UtcDateTime:O}/{to.UtcDateTime:O}") + ".json");
    public IReadOnlyList<DispatchExplanationRating> ReadExplanationRatings(string project)
    {
        lock (PersonaGate(project)) return Read<DispatchExplanationRating[]>(Path.Combine(Project(project), "explanation-ratings.json")) ?? [];
    }
    public void RateExplanation(string project, DispatchExplanationRating rating)
    {
        if (!double.IsFinite(rating.Score) || rating.Score is < 0 or > 100) throw new ArgumentException("Use an owner rating from 0 to 100.");
        lock (PersonaGate(project))
        {
            if (!ReadDispatcherLog(project).Any(e => e.Id == rating.EntryId && e.PersonaId == rating.Persona))
                throw new ArgumentException("Unknown persona explanation entry.");
            var entry = ReadDispatcherLog(project).Single(e => e.Id == rating.EntryId);
            var folder = Path.Combine(Project(project), "review");
            if (Directory.Exists(folder))
                foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
                {
                    var window = Read<DispatchReviewWindow>(path);
                    if (window is not null && window.From <= entry.At && entry.At < window.To) File.Delete(path);
                }
            Write(Path.Combine(Project(project), "explanation-ratings.json"), ReadExplanationRatings(project)
                .Where(r => r.EntryId != rating.EntryId).Append(rating).ToArray());
        }
    }
}
