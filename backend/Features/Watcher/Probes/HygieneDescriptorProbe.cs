using AgentStudio.Docs;

namespace AgentStudio.Watcher;

/// <summary>
/// Hygiene detector (§10.2): invalid Wiki/dossier descriptors that
/// <see cref="WorkbenchCatalogueService"/> already computes on every list
/// call, older than a grace period. This is the real wiring for the
/// "Fifteen invalid dossier descriptors" finding of §10.1 - no new
/// validation logic, just a threshold read on an existing, already-computed
/// projection.
/// </summary>
public sealed class HygieneDescriptorProbe : IWatcherSignalProbe
{
    public string Name => "hygiene-descriptor";

    private readonly WorkbenchCatalogueService _workbench;
    private readonly IConfiguration _configuration;

    public HygieneDescriptorProbe(WorkbenchCatalogueService workbench, IConfiguration configuration)
    {
        _workbench = workbench;
        _configuration = configuration;
    }

    public IReadOnlyList<WatcherSignalObservation> Collect(string workspaceRoot, DateTime nowUtc)
    {
        var graceHours = _configuration.GetValue("Watcher:HygieneGraceHours", 24);
        var cutoff = nowUtc - TimeSpan.FromHours(graceHours);
        var results = new List<WatcherSignalObservation>();

        foreach (var projectName in _workbench.ListProjectNames())
        {
            WorkbenchCatalogue? catalogue;
            try
            {
                catalogue = _workbench.List(projectName, includeHistory: false);
            }
            catch (Exception ex)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, $"HygieneDescriptorProbe: '{projectName}' Wiki source unresolved, not this probe's finding");
                continue;
            }
            if (catalogue == null) continue;

            foreach (var item in catalogue.Items.Where(i => !i.Valid && i.UpdatedAtUtc <= cutoff))
            {
                results.Add(new WatcherSignalObservation
                {
                    DetectorClass = WatcherDetectorClasses.Hygiene,
                    Project = projectName,
                    FingerprintKey = item.EntryPath,
                    Summary = $"Invalid dossier descriptor `{item.EntryPath}` has stood unrepaired for over {graceHours}h: {item.Error}",
                    ObservedAtUtc = nowUtc,
                    AffectedCards = item.SourceTaskKeys.ToList(),
                    Details = new Dictionary<string, string>
                    {
                        ["descriptorId"] = item.Id,
                        ["error"] = item.Error ?? "(no error text)",
                        ["updatedAtUtc"] = item.UpdatedAtUtc.ToString("O"),
                    },
                    SourcePaths = [item.EntryPath],
                });
            }
        }

        return results;
    }
}
