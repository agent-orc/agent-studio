using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentStudio.Personas;

/// <summary>
/// One persona descriptor (persona team PT-D1). Mirrors
/// <c>docs/app/schemas/persona.schema.json</c>; <see cref="PersonaDescriptorValidator"/>
/// enforces the schema in code plus the rules a schema cannot state (focus
/// weights sum to 100, A2 and manager operations only for managers).
/// </summary>
public sealed record PersonaDescriptorV1(
    int SchemaVersion,
    string Id,
    string Revision,
    string Title,
    string Aspect,
    string Kind,
    string Charter,
    PersonaScopeV1 Scope,
    PersonaAttributesV1 Attributes,
    IReadOnlyList<string> Operations,
    PersonaModelV1 Model,
    PersonaCadenceV1 Cadence,
    IReadOnlyList<string> Triggers,
    PersonaBudgetV1 Budget,
    PersonaReportV1 Report,
    IReadOnlyList<string> Floors,
    bool? StartsActive = null);

public sealed record PersonaScopeV1(
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string>? ExcludedPaths = null);

public sealed record PersonaAttributesV1(
    string Autonomy,
    string RiskAppetite,
    int Thoroughness,
    int SignalThreshold,
    IReadOnlyDictionary<string, int>? Focus = null);

public sealed record PersonaModelV1(string CapabilityClass, string Thinking);

public sealed record PersonaCadenceV1(
    string Report,
    string Window,
    IReadOnlyList<string>? Days = null,
    string? DueAtLocal = null,
    string? Investigation = null,
    string? Review = null);

public sealed record PersonaBudgetV1(
    int TokensPerWeek,
    int SessionsPerWeek,
    int ProposalsPerWeek,
    int DraftCardsPerWeek,
    int MinutesPerSession,
    int? InvestigationsPerWeek = null);

public sealed record PersonaReportV1(
    string Template,
    int MaxItems,
    int? MaxWords = null,
    IReadOnlyList<string>? Sections = null);

public static class PersonaKinds
{
    public const string Persona = "persona";
    public const string Presenter = "presenter";
    public const string Manager = "manager";
}

public static class PersonaAutonomy
{
    public const string Observe = "A0";
    public const string Propose = "A1";
    public const string Decide = "A2";

    private static readonly string[] ObserveOperations = ["observe", "persona.item.publish", "persona.report.publish"];
    private static readonly string[] ProposeOperations = ["persona.proposal.draft", "persona.decision.request"];

    /// <summary>Operations only a manager at A2 may hold (PT-D6).</summary>
    public static readonly IReadOnlyList<string> ManagerOperations =
    [
        "persona.proposal.accept", "persona.importance.adjust", "persona.budget.rebalance", "persona.pause", "card.requeue",
    ];

    public static readonly IReadOnlyList<string> AllOperations = [.. ObserveOperations, .. ProposeOperations, .. ManagerOperations];

    /// <summary>
    /// The named effect of the autonomy attribute: A0 publishes findings and
    /// reports, A1 adds drafts and decision requests, A2 adds the manager
    /// operations. The result keeps the descriptor's order.
    /// </summary>
    public static IReadOnlyList<string> Grantable(PersonaDescriptorV1 descriptor, string autonomy)
    {
        IReadOnlyList<string> allowed = autonomy switch
        {
            Decide => AllOperations,
            Propose => [.. ObserveOperations, .. ProposeOperations],
            _ => ObserveOperations,
        };
        return descriptor.Operations.Where(allowed.Contains).ToList();
    }
}

public static class PersonaDescriptorJson
{
    /// <summary>Strict reading: a missing required field or an unknown field fails like the schema.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static PersonaDescriptorV1 Parse(string json)
        => JsonSerializer.Deserialize<PersonaDescriptorV1>(json, Options)
           ?? throw new JsonException("A persona descriptor is a JSON object.");
}

/// <summary>
/// The descriptor digest an activation records and occurrence receipts carry:
/// <c>sha256:</c> over the descriptor as compact JSON with object keys sorted
/// ordinally, so formatting and key order never change it but every value does.
/// </summary>
public static class PersonaDescriptorDigest
{
    public static string Compute(PersonaDescriptorV1 descriptor)
    {
        var node = JsonSerializer.SerializeToNode(descriptor, PersonaDescriptorJson.Options)!;
        var canonical = Canonical(node)!.ToJsonString();
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}

/// <summary>In-code validation mirroring persona.schema.json, plus the rules a schema cannot express.</summary>
public static partial class PersonaDescriptorValidator
{
    public const int MaxCharterLength = 600;
    public const int MaxTokensPerWeek = 1_200_000;

    private static readonly string[] Aspects =
    [
        "security", "operations", "reliability", "quality", "design", "product", "cost", "documentation",
        "improvement", "presentation", "management",
    ];
    private static readonly string[] Kinds = [PersonaKinds.Persona, PersonaKinds.Presenter, PersonaKinds.Manager];
    private static readonly string[] Autonomies = [PersonaAutonomy.Observe, PersonaAutonomy.Propose, PersonaAutonomy.Decide];
    private static readonly string[] RiskAppetites = ["low", "medium", "high"];
    private static readonly string[] CapabilityClasses = ["balanced", "frontier"];
    private static readonly string[] ThinkingLevels = ["low", "medium", "high", "xhigh"];
    private static readonly string[] Weekdays = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")] public static partial Regex Slug();
    [GeneratedRegex("^[0-9]{4}\\.(1[0-2]|[1-9])\\.[1-9][0-9]*$")] public static partial Regex RevisionPattern();
    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z0-9-]+)+$")] private static partial Regex DottedName();
    [GeneratedRegex("^P[1-9][0-9]?D$")] private static partial Regex Days();
    [GeneratedRegex("^P[1-9][0-9]?[DM]$")] private static partial Regex Period();
    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$")] private static partial Regex LocalTime();
    [GeneratedRegex("^[a-z][a-z0-9-]*-v[0-9]+$")] private static partial Regex Template();
    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z0-9-]+)*([<>]=?[0-9]+[hdm]?)?:(100|[1-9]?[0-9])$")] private static partial Regex Floor();

    public static IReadOnlyList<string> Validate(PersonaDescriptorV1 d)
    {
        var errors = new List<string>();
        void Check(bool ok, string message) { if (!ok) errors.Add(message); }

        Check(d.SchemaVersion == 1, "schemaVersion must be 1.");
        Check(d.Id is { Length: <= 64 } && Slug().IsMatch(d.Id), "id must be a lowercase slug of at most 64 characters.");
        Check(d.Revision is not null && RevisionPattern().IsMatch(d.Revision), "revision must be year.month.sequence.");
        Check(d.Title is { Length: >= 1 and <= 80 }, "title must have 1 to 80 characters.");
        Check(Aspects.Contains(d.Aspect), "aspect is not a catalogued aspect.");
        Check(Kinds.Contains(d.Kind), "kind must be persona, presenter or manager.");
        Check(d.Kind != PersonaKinds.Manager || d.Aspect == "management", "a manager has aspect management.");
        Check(d.Kind != PersonaKinds.Presenter || d.Aspect == "presentation", "a presenter has aspect presentation.");
        Check(d.Charter is { Length: >= 1 and <= MaxCharterLength }, $"charter must have 1 to {MaxCharterLength} characters.");
        ValidateScope(d.Scope, errors);
        ValidateAttributes(d.Kind, d.Attributes, errors);
        Check(d.Operations is { Count: > 0 } && d.Operations.Distinct().Count() == d.Operations.Count
              && d.Operations.All(PersonaAutonomy.AllOperations.Contains), "operations must be distinct catalogued persona operations.");
        Check(d.Kind == PersonaKinds.Manager || d.Operations?.Any(PersonaAutonomy.ManagerOperations.Contains) != true,
            "only a manager holds manager operations.");
        Check(d.Model is not null && CapabilityClasses.Contains(d.Model.CapabilityClass) && ThinkingLevels.Contains(d.Model.Thinking),
            "model needs capability class balanced or frontier and a thinking level.");
        ValidateCadence(d.Cadence, errors);
        Check(d.Triggers is not null && d.Triggers.Distinct().Count() == d.Triggers.Count
              && d.Triggers.All(t => t.Length <= 80 && DottedName().IsMatch(t)), "triggers must be distinct dotted event names.");
        ValidateBudget(d.Budget, errors);
        Check(d.Report is not null && Template().IsMatch(d.Report.Template) && d.Report.MaxItems is >= 1 and <= 20
              && d.Report.MaxWords is null or (>= 50 and <= 1200)
              && (d.Report.Sections is null || (d.Report.Sections.Distinct().Count() == d.Report.Sections.Count
                  && d.Report.Sections.All(Slug().IsMatch))), "report needs a versioned template, 1 to 20 items and 50 to 1200 words.");
        Check(d.Floors is not null && d.Floors.Distinct().Count() == d.Floors.Count && d.Floors.All(Floor().IsMatch),
            "floors must be distinct '<fact>[<op><threshold>]:<0-100>' rules.");
        return errors;
    }

    public static void ValidateScope(PersonaScopeV1? scope, List<string> errors)
    {
        if (scope is null) { errors.Add("scope is required."); return; }
        if (scope.Projects is not { Count: > 0 } || scope.Projects.Distinct().Count() != scope.Projects.Count
            || scope.Projects.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200))
            errors.Add("scope.projects must name '*' or distinct projects.");
        if (scope.Areas is not { Count: > 0 } || scope.Areas.Distinct().Count() != scope.Areas.Count
            || !scope.Areas.All(DottedName().IsMatch))
            errors.Add("scope.areas must name distinct source families such as operations.backups.");
        if (scope.ExcludedPaths is not null && (scope.ExcludedPaths.Distinct().Count() != scope.ExcludedPaths.Count
            || scope.ExcludedPaths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 260)))
            errors.Add("scope.excludedPaths must be distinct paths.");
    }

    public static void ValidateAttributes(string kind, PersonaAttributesV1? a, List<string> errors)
    {
        if (a is null) { errors.Add("attributes are required."); return; }
        if (!Autonomies.Contains(a.Autonomy)) errors.Add("attributes.autonomy must be A0, A1 or A2.");
        if (a.Autonomy == PersonaAutonomy.Decide && kind != PersonaKinds.Manager)
            errors.Add("autonomy A2 is for managers only.");
        if (!RiskAppetites.Contains(a.RiskAppetite)) errors.Add("attributes.riskAppetite must be low, medium or high.");
        if (a.Thoroughness is < 1 or > 5) errors.Add("attributes.thoroughness must be 1 to 5.");
        if (a.SignalThreshold is < 0 or > 100) errors.Add("attributes.signalThreshold must be 0 to 100.");
        if (a.Focus is not null && (a.Focus.Count == 0 || a.Focus.Keys.Any(key => key.Length > 64 || !Slug().IsMatch(key))
            || a.Focus.Values.Any(weight => weight is < 0 or > 100) || a.Focus.Values.Sum() != 100))
            errors.Add("attributes.focus weights must be 0 to 100 per slug area and sum to 100.");
    }

    public static void ValidateBudget(PersonaBudgetV1? b, List<string> errors)
    {
        if (b is null) { errors.Add("budget is required."); return; }
        if (b.TokensPerWeek is < 0 or > MaxTokensPerWeek || b.SessionsPerWeek is < 0 or > 50
            || b.ProposalsPerWeek is < 0 or > 20 || b.DraftCardsPerWeek is < 0 or > 10
            || b.MinutesPerSession is < 1 or > 15 || b.InvestigationsPerWeek is < 0 or > 1)
            errors.Add($"budget must stay within 0 to {MaxTokensPerWeek} tokens, 50 sessions, 20 proposals, 10 draft cards, "
                + "1 investigation and 1 to 15 minutes per session.");
    }

    private static void ValidateCadence(PersonaCadenceV1? c, List<string> errors)
    {
        if (c is null) { errors.Add("cadence is required."); return; }
        if (c.Report is null || !Days().IsMatch(c.Report)) errors.Add("cadence.report must be a day duration such as P1D.");
        if (c.Window is null || !Days().IsMatch(c.Window)) errors.Add("cadence.window must be a day duration such as P7D.");
        if (c.Days is not null && (c.Days.Count == 0 || c.Days.Distinct().Count() != c.Days.Count || !c.Days.All(Weekdays.Contains)))
            errors.Add("cadence.days must name distinct weekdays.");
        if (c.DueAtLocal is not null && !LocalTime().IsMatch(c.DueAtLocal)) errors.Add("cadence.dueAtLocal must be HH:mm.");
        if (c.Investigation is not null && !Days().IsMatch(c.Investigation)) errors.Add("cadence.investigation must be a day duration.");
        if (c.Review is not null && !Period().IsMatch(c.Review)) errors.Add("cadence.review must be a day or month duration.");
    }

    /// <summary>The length of one report period, which is also the shadow period after activation.</summary>
    public static TimeSpan ReportPeriod(PersonaCadenceV1 cadence)
        => TimeSpan.FromDays(int.Parse(cadence.Report[1..^1], System.Globalization.CultureInfo.InvariantCulture));
}
