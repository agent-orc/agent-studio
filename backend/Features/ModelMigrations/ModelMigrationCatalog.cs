using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.ModelMigrations;

/// <summary>
/// Version 1 of the model migration catalog owned by the Token Economy project.
/// The catalog is read from that project's registered repository and is not
/// copied into Agent Studio.
/// </summary>
public sealed record ModelMigrationCatalog
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "";
    public int SchemaVersion { get; init; }
    public string CatalogVersion { get; init; } = "";
    public string EvidenceAsOfDate { get; init; } = "";
    public string DefaultStrategy { get; init; } = "";
    public JsonElement Authority { get; init; }
    public IReadOnlyList<string> CostClassOrder { get; init; } = [];
    public IReadOnlyList<ModelMigrationRule> Migrations { get; init; } = [];
    public JsonElement TaskClassRecommendations { get; init; }
}

public sealed record ModelMigrationRule
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Family { get; init; } = "";
    public string Vendor { get; init; } = "";
    public ModelMigrationGenerationOrder GenerationOrder { get; init; } = new();
    public string CostClassFrom { get; init; } = "";
    public string CostClassTo { get; init; } = "";
    public bool LadderCompatible { get; init; }
    public string ContextChange { get; init; } = "";
    public JsonElement Evidence { get; init; }
    public bool SafeAuto { get; init; }
    public string Since { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed record ModelMigrationGenerationOrder
{
    public int From { get; init; }
    public int To { get; init; }
}

/// <summary>
/// A catalog proposal for one superseded model. <see cref="SafeAutoCandidate"/>
/// covers catalog safety and current CLI availability only. Admission must
/// still honor explicit pins, the workspace switch, dated pricing, and the
/// model-routing correctness floor.
/// </summary>
public sealed record ModelMigrationProposal
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Family { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string CatalogVersion { get; init; } = "";
    public string Rule { get; init; } = "";
    public string CostClassFrom { get; init; } = "";
    public string CostClassTo { get; init; } = "";
    public bool LadderCompatible { get; init; }
    public IReadOnlyList<string> FromReasoningLevels { get; init; } = [];
    public IReadOnlyList<string> ToReasoningLevels { get; init; } = [];
    public string ContextChange { get; init; } = "";
    public bool SafeAuto { get; init; }
    public bool? TargetAvailable { get; init; }
    public bool SafeAutoCandidate { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>Pure validation and proposal selection for catalog version 1.</summary>
public static class ModelMigrationCatalogPolicy
{
    private static readonly string[] ExpectedCostClassOrder = ["economy", "standard", "premium"];
    private static readonly HashSet<string> AllowedFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude-opus", "claude-sonnet", "claude-haiku", "gpt", "gpt-flagship", "gpt-mini"
    };
    private static readonly HashSet<string> AllowedVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "anthropic", "openai"
    };
    private static readonly HashSet<string> AllowedCostClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "economy", "standard", "premium", "unknown"
    };
    private static readonly HashSet<string> AllowedContextChanges = new(StringComparer.OrdinalIgnoreCase)
    {
        "same", "increase", "decrease", "unknown"
    };
    private static readonly HashSet<string> AllowedEvidenceKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "controlledBenchmark", "abTest"
    };
    private static readonly HashSet<string> AllowedEvidenceConclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "noRegression", "regression", "inconclusive"
    };

    public static IReadOnlyList<string> Validate(ModelMigrationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var errors = new List<string>();
        if (!string.Equals(catalog.Schema, "model-migrations.v1.schema.json", StringComparison.Ordinal))
            errors.Add("$schema must be model-migrations.v1.schema.json.");
        if (catalog.SchemaVersion != 1)
            errors.Add("schemaVersion must be 1.");
        ValidateDate(catalog.CatalogVersion, "catalogVersion", errors);
        ValidateDate(catalog.EvidenceAsOfDate, "evidenceAsOfDate", errors);
        if (!string.Equals(catalog.DefaultStrategy, "latestInFamily", StringComparison.Ordinal))
            errors.Add("defaultStrategy must be latestInFamily.");
        ValidateAuthority(catalog.Authority, errors);

        if (!catalog.CostClassOrder.SequenceEqual(ExpectedCostClassOrder, StringComparer.Ordinal))
            errors.Add("costClassOrder must be economy, standard, premium.");
        if (catalog.Migrations.Count == 0)
            errors.Add("migrations must contain at least one rule.");
        if (catalog.TaskClassRecommendations.ValueKind != JsonValueKind.Array
            || catalog.TaskClassRecommendations.GetArrayLength() != 5)
        {
            errors.Add("taskClassRecommendations must contain the five task classes.");
        }

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var migration in catalog.Migrations)
            ValidateMigration(migration, catalog.CostClassOrder, sources, errors);

        return errors;
    }

    public static ModelMigrationProposal? FindProposal(
        ModelMigrationCatalog catalog,
        string? currentModel,
        Func<string, bool>? isTargetAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var normalizedCurrent = ModelMetadataRegistry.NormalizeId(currentModel);
        if (normalizedCurrent.Length == 0) return null;

        var migration = catalog.Migrations.FirstOrDefault(candidate =>
            string.Equals(
                ModelMetadataRegistry.NormalizeId(candidate.From),
                normalizedCurrent,
                StringComparison.OrdinalIgnoreCase));
        if (migration == null) return null;

        var normalizedTarget = ModelMetadataRegistry.NormalizeId(migration.To);
        if (string.Equals(normalizedCurrent, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            return null;

        var targetAvailable = isTargetAvailable?.Invoke(normalizedTarget);
        return new ModelMigrationProposal
        {
            From = migration.From,
            To = migration.To,
            Family = migration.Family,
            Vendor = migration.Vendor,
            CatalogVersion = catalog.CatalogVersion,
            Rule = $"{catalog.DefaultStrategy}:{migration.Family}:{migration.From}->{migration.To}",
            CostClassFrom = migration.CostClassFrom,
            CostClassTo = migration.CostClassTo,
            LadderCompatible = migration.LadderCompatible,
            FromReasoningLevels = ReasoningLevels(migration.Vendor, migration.From),
            ToReasoningLevels = ReasoningLevels(migration.Vendor, migration.To),
            ContextChange = migration.ContextChange,
            SafeAuto = migration.SafeAuto,
            TargetAvailable = targetAvailable,
            SafeAutoCandidate = migration.SafeAuto && targetAvailable == true,
            Note = migration.Note
        };
    }

    private static IReadOnlyList<string> ReasoningLevels(string vendor, string model)
    {
        var cliType = vendor.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
            ? CliTypes.Claude
            : vendor.Equals("openai", StringComparison.OrdinalIgnoreCase)
                ? CliTypes.Codex
                : null;
        return cliType == null ? [] : [.. ModelMetadataRegistry.ThinkingLevelsFor(cliType, model)];
    }

    private static void ValidateAuthority(JsonElement authority, ICollection<string> errors)
    {
        if (authority.ValueKind != JsonValueKind.Object)
        {
            errors.Add("authority must be an object.");
            return;
        }

        foreach (var property in new[] { "rules", "routingPolicy", "priceCatalog" })
        {
            if (!authority.TryGetProperty(property, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                errors.Add($"authority.{property} is required.");
            }
        }
    }

    private static void ValidateMigration(
        ModelMigrationRule migration,
        IReadOnlyList<string> costClassOrder,
        ISet<string> sources,
        ICollection<string> errors)
    {
        var label = string.IsNullOrWhiteSpace(migration.From) ? "<missing>" : migration.From;
        RequireText(migration.From, $"Migration '{label}' requires from.", errors);
        RequireText(migration.To, $"Migration '{label}' requires to.", errors);
        RequireText(migration.Note, $"Migration '{label}' requires note.", errors);
        ValidateDate(migration.Since, $"Migration '{label}' since", errors);

        if (!AllowedFamilies.Contains(migration.Family))
            errors.Add($"Migration '{label}' has unknown family '{migration.Family}'.");
        if (!AllowedVendors.Contains(migration.Vendor))
            errors.Add($"Migration '{label}' has unknown vendor '{migration.Vendor}'.");
        if (!AllowedCostClasses.Contains(migration.CostClassFrom))
            errors.Add($"Migration '{label}' has unknown source cost class '{migration.CostClassFrom}'.");
        if (!AllowedCostClasses.Contains(migration.CostClassTo))
            errors.Add($"Migration '{label}' has unknown target cost class '{migration.CostClassTo}'.");
        if (!AllowedContextChanges.Contains(migration.ContextChange))
            errors.Add($"Migration '{label}' has unknown context change '{migration.ContextChange}'.");
        if (migration.GenerationOrder.From < 0 || migration.GenerationOrder.To < 0)
            errors.Add($"Migration '{label}' generation order cannot be negative.");

        var normalizedSource = ModelMetadataRegistry.NormalizeId(migration.From);
        if (normalizedSource.Length > 0 && !sources.Add(normalizedSource))
            errors.Add($"Migration source '{migration.From}' is duplicated.");

        ValidateEvidence(migration, label, errors);
        if (!migration.SafeAuto) return;

        var normalizedTarget = ModelMetadataRegistry.NormalizeId(migration.To);
        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Migration '{label}' cannot set safeAuto on a retention rule.");
        if (!IsFamilyMember(normalizedSource, migration.Family)
            || !IsFamilyMember(normalizedTarget, migration.Family))
        {
            errors.Add($"Migration '{label}' cannot set safeAuto across model families.");
        }
        if (migration.GenerationOrder.To <= migration.GenerationOrder.From)
            errors.Add($"Migration '{label}' safeAuto target must have a newer generation order.");
        if (!migration.LadderCompatible)
            errors.Add($"Migration '{label}' cannot set safeAuto with an incompatible reasoning ladder.");
        if (!IsSameOrLowerCost(migration.CostClassFrom, migration.CostClassTo, costClassOrder))
            errors.Add($"Migration '{label}' safeAuto target must have the same or lower known cost class.");
        if (!HasNoRegressionEvidence(migration.Evidence))
            errors.Add($"Migration '{label}' safeAuto rule requires comparable noRegression evidence.");
    }

    private static void ValidateEvidence(
        ModelMigrationRule migration,
        string label,
        ICollection<string> errors)
    {
        if (migration.Evidence.ValueKind == JsonValueKind.String)
        {
            if (!string.Equals(migration.Evidence.GetString(), "none", StringComparison.Ordinal))
                errors.Add($"Migration '{label}' evidence string must be none.");
            return;
        }

        if (migration.Evidence.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Migration '{label}' evidence must be none or an evidence object.");
            return;
        }

        var kind = EvidenceString(migration.Evidence, "kind");
        var reference = EvidenceString(migration.Evidence, "reference");
        var conclusion = EvidenceString(migration.Evidence, "conclusion");
        var summary = EvidenceString(migration.Evidence, "summary");
        if (kind == null || !AllowedEvidenceKinds.Contains(kind))
            errors.Add($"Migration '{label}' has invalid evidence kind.");
        if (reference == null)
            errors.Add($"Migration '{label}' requires an evidence reference.");
        if (conclusion == null || !AllowedEvidenceConclusions.Contains(conclusion))
            errors.Add($"Migration '{label}' has invalid evidence conclusion.");
        if (summary == null)
            errors.Add($"Migration '{label}' requires an evidence summary.");
    }

    private static bool HasNoRegressionEvidence(JsonElement evidence)
        => evidence.ValueKind == JsonValueKind.Object
           && AllowedEvidenceKinds.Contains(EvidenceString(evidence, "kind") ?? "")
           && EvidenceString(evidence, "reference") != null
           && string.Equals(
               EvidenceString(evidence, "conclusion"),
               "noRegression",
               StringComparison.Ordinal)
           && EvidenceString(evidence, "summary") != null;

    private static string? EvidenceString(JsonElement evidence, string property)
        => evidence.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool IsSameOrLowerCost(
        string source,
        string target,
        IReadOnlyList<string> costClassOrder)
    {
        var sourceRank = IndexOf(costClassOrder, source);
        var targetRank = IndexOf(costClassOrder, target);
        return sourceRank >= 0 && targetRank >= 0 && targetRank <= sourceRank;
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private static bool IsFamilyMember(string model, string family)
    {
        if (model.Length == 0) return false;
        if (family.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            return model.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(family, "gpt-mini", StringComparison.OrdinalIgnoreCase))
            return model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                   && model.Contains("-mini", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(family, "gpt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(family, "gpt-flagship", StringComparison.OrdinalIgnoreCase))
        {
            return model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                   && !model.Contains("-mini", StringComparison.OrdinalIgnoreCase)
                   && !model.Contains("-nano", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void ValidateDate(string value, string property, ICollection<string> errors)
    {
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            errors.Add($"{property} must use yyyy-MM-dd.");
        }
    }

    private static void RequireText(string value, string error, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add(error);
    }
}

public sealed class ModelMigrationCatalogValidationException : Exception
{
    public ModelMigrationCatalogValidationException(IReadOnlyList<string> errors)
        : base("The Token Economy model migration catalog is invalid: " + string.Join(" ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
