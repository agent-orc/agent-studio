namespace AgentRunner.SourceFacts;

/// <summary>
/// Answers source fact demands for every repository whose shared checkout
/// lives on this host. A repository is known by the marker that the producer
/// writes beside the checkout at its first handoff, because the cache folder
/// name is a sanitised form of the repository id.
/// </summary>
public sealed class SourceFactsDemandSweep(
    RunnerOptions options,
    SourceFactsProducer producer,
    Action<string> log)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public async Task PollAsync(CancellationToken shutdown)
    {
        if (!options.SourceFactsProducerEnabled) return;
        while (!shutdown.IsCancellationRequested)
        {
            foreach (var (repositoryId, repositoryPath) in DiscoverRepositories(options.WorkDir))
            {
                try
                {
                    var answered = await producer.DrainDemandsAsync(repositoryId, repositoryPath, shutdown);
                    if (answered > 0)
                        log($"source-facts demands answered repository={repositoryId} count={answered}");
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    log($"source-facts demand sweep deferred repository={repositoryId} type={exception.GetType().Name}: {exception.Message}");
                }
            }
            try { await Task.Delay(Interval, shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
        }
    }

    internal static IReadOnlyList<(string RepositoryId, string RepositoryPath)> DiscoverRepositories(string workDir)
    {
        if (!Directory.Exists(workDir)) return [];
        var found = new List<(string, string)>();
        foreach (var project in Directory.EnumerateDirectories(workDir).Order(StringComparer.Ordinal))
        {
            var marker = Path.Combine(project, SourceFactsProducer.RepositoryMarkerFile);
            var repository = Path.Combine(project, "repo");
            if (!File.Exists(marker) || !Directory.Exists(Path.Combine(repository, ".git"))) continue;
            var repositoryId = File.ReadAllText(marker).Trim();
            if (repositoryId.Length > 0) found.Add((repositoryId, repository));
        }
        return found;
    }

    /// <summary>Records which repository a project cache folder holds.</summary>
    internal static void WriteMarker(string projectCachePath, string repositoryId)
    {
        var marker = Path.Combine(projectCachePath, SourceFactsProducer.RepositoryMarkerFile);
        if (File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), repositoryId, StringComparison.Ordinal))
            return;
        Directory.CreateDirectory(projectCachePath);
        File.WriteAllText(marker, repositoryId + "\n");
    }
}
