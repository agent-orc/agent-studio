using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>The review CLI starts in the full checkout of the immutable delivery.</summary>
public static class ConceptDossierReviewMaterial
{
    public static string Render(string taskFolder, string? changedDirectory)
    {
        var entry = new[]
        {
            ConceptWorkbenchStore.Read(taskFolder)?.RepoRelativeEntrypoint,
            ConceptDossierContract.Read(taskFolder).RepoRelativePath,
            changedDirectory is null ? null : changedDirectory + "/index.html",
        }.Select(ConceptDossierContract.NormalizePath).FirstOrDefault(ConceptDossierContract.IsDossierPath);
        var location = entry is null
            ? "Locate the delivered index.html and workbench.json using the task brief and changed paths."
            : $"Start with `{entry}` and `{entry[..entry.LastIndexOf('/')]}/workbench.json`.";
        return ConceptDossierSnapshotSpan.Wrap(location + "\n" + ConceptRemoteReviewPolicy.WorkspaceInstructions);
    }
}
