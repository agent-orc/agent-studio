namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2817 - one definition of "this card names its deliverable", shared by
/// the completion contract and the delivery-claim sweep so the lane change and
/// the sweep cannot disagree about the same card.
///
/// <para>
/// A code-free card names its deliverable through the concept Dossier path, an
/// explicit no-Dossier declaration, a workbench reference key, or a concrete
/// artifact under <c>results/</c> - <c>deliverables.md</c> and the research
/// convention <c>report.html</c> first, then any other produced result file.
/// The check exists for the empty case: a card that expects no code, produced
/// no artifact, and references no Dossier has nothing to claim.
/// </para>
/// </summary>
public static class NamedDeliverableReader
{
    private static readonly string[] PreferredArtifacts = ["deliverables.md", "report.html"];

    public static (string? Path, string? Key) Read(TaskInfo task)
    {
        var key = task.References?.Workbenches
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return (ReadDossierPath(task) ?? FirstResultArtifact(task.FolderPath), key);
    }

    public static bool Exists(TaskInfo task)
    {
        var (path, key) = Read(task);
        return path is not null || key is not null;
    }

    private static string? ReadDossierPath(TaskInfo task)
    {
        try
        {
            var dossier = ConceptDossierContract.Read(task.FolderPath);
            if (dossier.RepoRelativePath is not null) return dossier.RepoRelativePath;
            return dossier.NoDossierNeeded ? "declared: no dossier needed" : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "NamedDeliverableReader: unreadable concept dossier reference");
            return null;
        }
    }

    private static string? FirstResultArtifact(string folderPath)
    {
        try
        {
            var results = TaskPaths.ResultsDir(folderPath);
            if (!Directory.Exists(results)) return null;
            foreach (var preferred in PreferredArtifacts)
            {
                var candidate = Path.Combine(results, preferred);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                    return $"results/{preferred}";
            }

            var any = Directory
                .EnumerateFiles(results, "*", SearchOption.AllDirectories)
                .FirstOrDefault(file => new FileInfo(file).Length > 0);
            return any is null
                ? null
                : "results/" + Path.GetRelativePath(results, any).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "NamedDeliverableReader: unreadable results directory");
            return null;
        }
    }
}
