using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>
/// Applies the migration catalog's automatic updates at run admission.
///
/// <para>The decision is pure (<see cref="ModelMigrationAdmission.Decide"/>);
/// this class owns only the bounded side effects that follow it: persist the new
/// model on the card while leaving <c>modelExplicit=false</c>, write the
/// <c>model_migrated</c> timeline event, and put one line on the operator feed.
/// Keeping them here rather than inside the runner is what makes "a non-explicit
/// default migrates and leaves an audit trail" directly testable.</para>
///
/// <para>The timeline is a required collaborator, not an option: an automatic
/// route change without its audit event is exactly the silent behaviour this
/// feature exists to avoid.</para>
///
/// <para>Every step is best effort. A migration is a routing improvement, not a
/// precondition for running, so an unreadable catalog or an unwritable card logs
/// and keeps the original model instead of failing admission.</para>
/// </summary>
public sealed class ModelMigrationApplier
{
    private readonly ModelMigrationCatalogService _catalog;
    private readonly ModelRoutingPolicyStateStore _state;
    private readonly TimelineLog _timeline;
    private readonly AgentMessageBusBridge? _bus;
    private readonly ILogger<ModelMigrationApplier> _logger;

    public ModelMigrationApplier(
        ModelMigrationCatalogService catalog,
        ModelRoutingPolicyStateStore state,
        ILogger<ModelMigrationApplier> logger,
        TimelineLog timeline,
        AgentMessageBusBridge? bus = null)
    {
        _catalog = catalog;
        _state = state;
        _logger = logger;
        _timeline = timeline;
        _bus = bus;
    }

    /// <summary>
    /// Returns the task as the run should see it: migrated when the catalog says
    /// it is safe and the operator did not pin the model, otherwise unchanged.
    /// </summary>
    public TaskInfo Apply(TaskInfo info, string? project, IReadOnlyList<CliModelInfo>? liveCatalog = null)
    {
        try
        {
            var (catalog, catalogSource) = _catalog.Load();
            var migration = ModelMigrationAdmission.Decide(
                info.Model,
                info.ModelExplicit,
                _state.AutoApplyModelMigrations,
                catalog,
                liveCatalog);
            if (migration == null) return info;

            TaskJsonFile.UpdateField(info.FolderPath, "model", migration.To.ModelId, _logger);

            _timeline.Append(
                info.FolderPath,
                TimelineEventKinds.ModelMigrated,
                TimelineActors.Orchestrator,
                $"Model updated {migration.From.ModelId} -> {migration.To.ModelId} ({migration.Rule})",
                details: new Dictionary<string, string>
                {
                    ["from"] = migration.From.ModelId,
                    ["to"] = migration.To.ModelId,
                    ["rule"] = migration.Rule,
                    ["catalogVersion"] = migration.CatalogVersion,
                    ["catalogSource"] = catalogSource,
                    ["costClass"] = migration.CostClass,
                });

            _ = _bus?.EmitModelMigratedAsync(
                project, info.Id, migration.From.ModelId, migration.To.ModelId,
                migration.Rule, migration.CatalogVersion, catalogSource, migration.CostClass);

            _logger.LogInformation(
                "model-migrated jobId={JobId} from={From} to={To} rule={Rule} catalog={CatalogVersion} source={CatalogSource} costClass={CostClass}",
                info.Id, migration.From.ModelId, migration.To.ModelId, migration.Rule,
                migration.CatalogVersion, catalogSource, migration.CostClass);

            return info with { Model = migration.To.ModelId };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "model-migration check failed for {JobId}; card model retained", info.Id);
            return info;
        }
    }
}
