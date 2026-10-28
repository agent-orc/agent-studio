using AgentStudio.Pipeline;
using AgentStudio.Tasks;

namespace AgentStudio.Deploy;

/// <summary>A card with a verified integration record and the commits that record names.</summary>
public sealed record IntegratedCardRecord(string Key, string Title, IReadOnlyList<string> Shas, string IntegrationBranch);

/// <summary>The configured attribution source could not answer; the release is refused rather than attributed from another source.</summary>
public sealed class ReleaseAttributionUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Input of release card attribution (pipeline delivery target, section 05
/// "Release card attribution", AGT-3320). With <c>TaskArtifacts:Source =
/// task-server</c> one Task Server query over the integration records answers;
/// no task folder is read and there is no fallback to the folder scan, so a
/// Task Server outage refuses the release instead of attributing from a second
/// authority. Otherwise the archive-inclusive task index is read as before.
/// </summary>
public sealed class ReleaseCardAttributionSource(
    TaskScannerService scanner,
    TaskArtifactsSourceSettings? settings = null,
    TaskServerBoardClient? client = null)
{
    internal static readonly TimeSpan TaskServerTimeout = TimeSpan.FromSeconds(10);

    public bool ReadsTaskServer => settings?.AttributesFromTaskServer == true;

    public IReadOnlyList<IntegratedCardRecord> IntegratedCards(string project)
        => ReadsTaskServer ? FromTaskServer(project) : FromTaskFolders(project);

    private IReadOnlyList<IntegratedCardRecord> FromTaskServer(string project)
    {
        if (client is not { IsConfigured: true })
            throw new ReleaseAttributionUnavailableException(
                "Release card attribution reads the Task Server, but Board:TaskServer:BaseUrl is not configured.");
        using var timeout = new CancellationTokenSource(TaskServerTimeout);
        try
        {
            // Release creation is synchronous end to end (it also spawns git); this bounded read is one more step of it.
            var response = client.GetIntegratedCardsAsync(project, timeout.Token).GetAwaiter().GetResult()
                ?? throw new ReleaseAttributionUnavailableException($"The Task Server does not know project {project}.");
            return response.Cards
                .Select(card => new IntegratedCardRecord(card.TaskKey, card.Title, card.Shas, card.IntegrationBranch))
                .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new ReleaseAttributionUnavailableException(
                $"Release card attribution could not read the Task Server: {exception.Message}", exception);
        }
    }

    private IReadOnlyList<IntegratedCardRecord> FromTaskFolders(string project)
        => scanner.ScanAllJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .Select(task => (Task: task, Record: IntegrationVerificationStore.Read(task.FolderPath)))
            .Where(item => item.Record is { State: IntegrationVerificationStates.Verified })
            .Select(item => new IntegratedCardRecord(
                string.IsNullOrWhiteSpace(item.Task.Key) ? item.Task.Id : item.Task.Key,
                item.Task.Title,
                [.. item.Record!.Sha is { } sha ? [sha] : Array.Empty<string>(), .. item.Record.DeliveryShas],
                item.Record.IntegrationBranch))
            .ToArray();
}
