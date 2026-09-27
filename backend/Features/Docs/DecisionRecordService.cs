using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Docs;

/// <summary>Host-independent decision record formatting and project wiki storage.</summary>
public sealed class DecisionRecordService
{
    private readonly ProjectDocsService _docs;
    private readonly GitService? _git;
    private readonly ManagedRepositoryMutationService? _repositoryMutations;

    public DecisionRecordService(ProjectDocsService docs, GitService? git = null,
        ManagedRepositoryMutationService? repositoryMutations = null)
    {
        _docs = docs;
        _git = git;
        _repositoryMutations = repositoryMutations;
    }

    private static readonly JsonSerializerOptions ReceiptJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The receipt shape shared by Dossier and card decisions.</summary>
    public static JsonObject Receipt(DecisionReceiptInput input) => new()
    {
        ["outcome"] = input.Outcome,
        ["action"] = input.Action,
        ["state"] = input.State,
        ["operationId"] = input.OperationId,
        ["sourceRevision"] = input.SourceRevision,
        ["sourceFingerprint"] = input.SourceFingerprint,
        ["sourceEntryFingerprint"] = input.SourceEntryFingerprint,
        ["preparedAt"] = input.At,
        ["preparedBy"] = input.Actor,
        ["confirmedAt"] = input.At,
        ["confirmedBy"] = input.Actor,
        ["decidedAt"] = input.DecidedAt,
        ["spawnedTaskKeys"] = new JsonArray(input.SpawnedTaskKeys.Select(key => (JsonNode)key).ToArray()),
        ["responses"] = JsonSerializer.SerializeToNode(input.Responses, ReceiptJson),
        ["cliType"] = input.CliType,
        ["model"] = input.Model,
        ["thinkingLevel"] = input.ThinkingLevel,
    };

    /// <summary>Shared history entry used by the Dossier binding and decision records.</summary>
    public static JsonObject HistoryEntry(string state, string actor, string at, string? note) => new()
    {
        ["state"] = state,
        ["editedBy"] = actor,
        ["editedAt"] = at,
        ["note"] = note,
    };

    public DecisionRecordWriteResult Write(string projectName, string path, DecisionRecord record)
    {
        var content = Render(record);
        var full = _docs.ResolveWikiNodeFullPath(projectName, path);
        if (full is null) return new(false, path, "Project wiki path could not be resolved.");
        if (_git?.ResolveRepoRootForProject(projectName) is { } repoRoot
            && _repositoryMutations is not null)
        {
            var rel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
            var sidecar = Path.GetRelativePath(repoRoot, full + ".meta.json").Replace('\\', '/');
            if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                return new(false, path, "Decision record is outside the project repository.");
            var result = _repositoryMutations.Execute(projectName, repoRoot,
                $"decision-{record.Id}-{record.Content.History.LastOrDefault()?.At.Ticks ?? DateTime.UtcNow.Ticks}",
                $"docs(decision): record {record.Id}", [rel, sidecar],
                () => WriteWikiPage(projectName, path, full, content));
            if (!result.Success) _docs.InvalidateWikiContent(projectName);
            return new(result.Success, path, result.Error);
        }
        try
        {
            WriteWikiPage(projectName, path, full, content);
            return new(true, path, null);
        }
        catch (Exception ex)
        {
            return new(false, path, ex.Message);
        }
    }

    private void WriteWikiPage(string projectName, string path, string full, string content)
    {
        if (File.Exists(full))
        {
            var save = _docs.WriteWikiFile(projectName, path, content);
            if (!save.Success) throw new InvalidOperationException(save.Error);
            return;
        }
        var create = _docs.CreateWikiPage(projectName, path, content);
        if (!create.Success) throw new InvalidOperationException(create.Error);
    }

    public static string Render(DecisionRecord record)
    {
        var key = record.Id;
        var title = record.Title;
        var decision = record.Content;
        var sb = new StringBuilder();
        sb.Append("# Decision ").Append(key).Append(": ").Append(title).Append("\n\n");
        sb.Append("- Status: ").Append(decision.Status).Append("\n");
        sb.Append("- Decider: ").Append(decision.Decider).Append("\n");
        if (decision.DueDate is { } due)
            sb.Append("- Due: ").Append(due.ToUniversalTime().ToString("yyyy-MM-dd")).Append("\n");
        if (decision.Dependants is { Count: > 0 } dependants)
            sb.Append("- Dependants: ").Append(string.Join(", ", dependants)).Append("\n");
        sb.Append("\n## Question\n\n").Append(decision.Question.Trim()).Append("\n\n");
        sb.Append("## Options\n\n");
        foreach (var option in decision.Options)
        {
            sb.Append("### ").Append(option.Id).Append(": ").Append(option.Label).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(option.Consequences))
                sb.Append("- Consequences: ").Append(option.Consequences.Trim()).Append("\n");
            if (!string.IsNullOrWhiteSpace(option.Effort))
                sb.Append("- Effort: ").Append(option.Effort.Trim()).Append("\n");
            if (!string.IsNullOrWhiteSpace(option.Risk))
                sb.Append("- Risk: ").Append(option.Risk.Trim()).Append("\n");
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(decision.RecommendedOptionId))
        {
            sb.Append("## Recommendation\n\n").Append(decision.RecommendedOptionId).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(decision.RecommendationReason))
                sb.Append(decision.RecommendationReason.Trim()).Append("\n\n");
        }
        sb.Append("## Record\n\n");
        foreach (var entry in decision.History)
        {
            sb.Append("### ").Append(entry.Status).Append(" at ")
                .Append(entry.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
                .Append("\n\n");
            sb.Append("- Actor: ").Append(entry.Actor).Append("\n");
            if (!string.IsNullOrWhiteSpace(entry.OptionId))
                sb.Append("- Chosen option: ").Append(entry.OptionId).Append("\n");
            if (!string.IsNullOrWhiteSpace(entry.Rationale))
                sb.Append("- Rationale: ").Append(entry.Rationale).Append("\n");
            if (!string.IsNullOrWhiteSpace(entry.Note))
                sb.Append("- Note: ").Append(entry.Note).Append("\n");
            sb.Append('\n');
        }
        sb.Append("## Receipts\n\n```json\n");
        var receipts = decision.History.Select(entry => Receipt(new DecisionReceiptInput
        {
            Outcome = "card-decision",
            Action = entry.Status == DecisionStatuses.Reopened ? "reopened" : "recorded-choice",
            State = entry.Status == DecisionStatuses.Reopened ? "pending" : "succeeded",
            OperationId = $"{key}-{entry.At.ToUniversalTime().Ticks}",
            At = entry.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Actor = entry.Actor,
            DecidedAt = entry.Status == DecisionStatuses.Reopened ? null
                : entry.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Responses = entry.OptionId is null ? [] :
                [new WorkbenchDecisionResponse
                {
                    DecisionId = key,
                    Kind = "single",
                    SelectedOptionIds = [entry.OptionId],
                    Comment = entry.Rationale,
                }],
        })).ToArray();
        sb.Append(JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }));
        sb.Append("\n```\n");
        return sb.ToString();
    }
}

public sealed record DecisionRecordWriteResult(bool Success, string Path, string? Error);

/// <summary>Decision facts without a Dossier or card lifecycle binding.</summary>
public sealed record DecisionRecord(string Id, string Title, DecisionContent Content);

public sealed record DecisionReceiptInput
{
    public string Outcome { get; init; } = "";
    public string Action { get; init; } = "";
    public string State { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string? SourceRevision { get; init; }
    public string? SourceFingerprint { get; init; }
    public string? SourceEntryFingerprint { get; init; }
    public string At { get; init; } = "";
    public string Actor { get; init; } = "";
    public string? DecidedAt { get; init; }
    public string[] SpawnedTaskKeys { get; init; } = [];
    public IReadOnlyList<WorkbenchDecisionResponse> Responses { get; init; } = [];
    public string? CliType { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
}
