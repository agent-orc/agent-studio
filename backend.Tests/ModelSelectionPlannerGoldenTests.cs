using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every planner call receives the fixed Now below.

/// <summary>
/// Golden matrix for the model-selection planner (MS-5, Dossier
/// model-selection-and-fallback). The matrix crosses quota, pin, operator
/// preference and host facts. <c>Golden/model-selection-planner.jsonl</c> was
/// recorded from <see cref="QuotaAdmissionPlanner"/> and
/// <see cref="CliQuotaFallbackService.Resolve"/> before their rules moved into
/// the shared planner. Every route through the new code must reproduce it
/// line for line. Regenerate it only for an intended behaviour change, with
/// <c>MODEL_SELECTION_GOLDEN_UPDATE=1</c>.
/// </summary>
/// <remarks>
/// The matrix crosses six routes (Opus xhigh, Claude default, Sol xhigh and
/// medium, a Sonnet operator override, Gemini without a partner), seven
/// primary quota states (none, healthy, capped, capped with a nearby reset
/// under the wait policy, projected breach idle and busy, suspicious), two
/// fallback quota states, three operator states (off, switch on the primary,
/// fallback provider conserved), three host states (none, rejects the
/// primary, rejects the fallback) and the review same-class requirement.
/// </remarks>
[Collection(CodexDetectedDefaultCollection.Name)]
public sealed class ModelSelectionPlannerGoldenTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions LineJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-golden-" + Guid.NewGuid().ToString("N"));
    private readonly CliQuotaCapsService _caps;
    private readonly CliQuotaFallbackService _catalogue;
    private readonly CliQuotaFallbackService _override;

    public ModelSelectionPlannerGoldenTests()
    {
        ResetDetectedModels();
        _caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, Config("caps"));
        _catalogue = Fallback("catalogue");
        _override = Fallback("override");
        _override.Set(new CliModelRouteProfile
        {
            CliType = CliTypes.Claude,
            PrimaryModel = ModelIds.ClaudeOpus5,
            FallbackCliType = CliTypes.Codex,
            FallbackModel = ModelIds.Gpt56Sol,
            FallbackThinkingLevel = "high",
        });
    }

    public void Dispose()
    {
        ResetDetectedModels();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Legacy_admission_planner_and_resolve_match_the_recorded_matrix()
        => AssertGolden(PlanLegacy, ResolveLegacy);

    [Fact]
    public void Shared_planner_matches_the_recorded_matrix()
        => AssertGolden(PlanShared, ResolveShared);

    internal static readonly GoldenRoute[] Routes =
    [
        new("opus-xhigh", CliTypes.Claude, ModelIds.ClaudeOpus5, "xhigh", Override: false),
        new("claude-default", CliTypes.Claude, null, null, Override: false),
        new("sol-xhigh", CliTypes.Codex, ModelIds.Gpt6Sol, "xhigh", Override: false),
        new("sol-medium", CliTypes.Codex, ModelIds.Gpt6Sol, "medium", Override: false),
        new("sonnet-override", CliTypes.Claude, ModelIds.ClaudeSonnet5, "high", Override: true),
        new("gemini", CliTypes.Gemini, null, null, Override: false),
    ];

    internal static readonly string[] QuotaStates =
        ["none", "healthy", "capped", "capped-nearby-wait", "projected-idle", "projected-busy", "suspicious"];

    internal static readonly string[] FallbackQuotaStates = ["healthy", "capped"];
    internal static readonly string[] PreferenceStates = ["off", "switch-primary", "conserve-fallback"];
    internal static readonly string[] HostStates = ["none", "rejects-primary", "rejects-fallback"];

    internal static IEnumerable<GoldenPlanCase> PlanCases()
    {
        foreach (var route in Routes)
        foreach (var quota in QuotaStates)
        foreach (var fallbackQuota in FallbackQuotaStates)
        foreach (var preference in PreferenceStates)
        foreach (var host in HostStates)
        foreach (var sameClass in new[] { false, true })
        {
            // Review consumers never claim through a host; keep the matrix bounded.
            if (sameClass && host != "none") continue;
            yield return new GoldenPlanCase(route, quota, fallbackQuota, preference, host, sameClass);
        }
    }

    internal static IEnumerable<GoldenResolveCase> ResolveCases()
    {
        foreach (var route in Routes)
        foreach (var primaryBlocked in new[] { false, true })
        foreach (var fallbackBlocked in new[] { false, true })
        foreach (var prefer in new[] { false, true })
        foreach (var acceptance in new[] { "none", "rejects-fallback", "conserve-fallback" })
        foreach (var sameClass in new[] { false, true })
            yield return new GoldenResolveCase(route, primaryBlocked, fallbackBlocked, prefer, acceptance, sameClass);
    }

    internal void AssertGolden(
        Func<GoldenPlanCase, GoldenFacts, QuotaAdmissionPlan> plan,
        Func<GoldenResolveCase, GoldenFacts, CliRouteDecision> resolve)
    {
        var actual = new List<(string Id, string Value)>();
        foreach (var testCase in PlanCases())
            actual.Add((testCase.Id, Value(Project(plan(testCase, FactsFor(testCase))))));
        foreach (var testCase in ResolveCases())
            actual.Add((testCase.Id, Value(Project(resolve(testCase, FactsFor(testCase))))));

        var path = GoldenPath();
        if (Environment.GetEnvironmentVariable("MODEL_SELECTION_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Serialize(actual));
        }

        Assert.True(File.Exists(path), $"Golden file missing: {path}");
        var expected = Deserialize(File.ReadAllText(path));
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Select(item => item.Id).Order(StringComparer.Ordinal));
        var drift = actual
            .Where(item => !string.Equals(expected[item.Id], item.Value, StringComparison.Ordinal))
            .Take(5)
            .Select(item => $"{item.Id}\n  expected {expected[item.Id]}\n  actual   {item.Value}")
            .ToArray();
        Assert.True(drift.Length == 0, "Model selection drifted from the recorded matrix:\n" + string.Join("\n", drift));
    }

    /// <summary>One line per distinct outcome, followed by the cases that produce it.</summary>
    private static string Serialize(IEnumerable<(string Id, string Value)> cases)
        => string.Join("\n", cases
            .GroupBy(item => item.Value, StringComparer.Ordinal)
            .Select(group => $"{{\"value\":{group.Key},\"cases\":{JsonSerializer.Serialize(group.Select(item => item.Id), LineJson)}}}")) + "\n";

    private static Dictionary<string, string> Deserialize(string text)
    {
        var cases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            var value = Value(document.RootElement.GetProperty("value"));
            foreach (var id in document.RootElement.GetProperty("cases").EnumerateArray())
                cases.Add(id.GetString()!, value);
        }
        return cases;
    }

    internal GoldenFacts FactsFor(GoldenPlanCase testCase)
    {
        var primary = testCase.Route.CliType;
        var other = OtherCli(primary);
        var snapshots = new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (PrimarySnapshot(primary, testCase.Quota) is { } snapshot) snapshots[primary] = snapshot;
        if (other is not null)
            snapshots[other] = Snapshot(other, "Weekly", testCase.FallbackQuota == "capped" ? 99 : 10, Now.AddDays(3));

        var waitPolicy = testCase.Quota == "capped-nearby-wait"
            ? new ResolvedCliQuotaWaitPolicy(true, 30, "global", null, null, true, 30)
            : new ResolvedCliQuotaWaitPolicy(false, 30, "global", null, null, false, 30);
        var preference = new CliFallbackPreference(
            primary, testCase.Preference == "switch-primary",
            testCase.Preference == "switch-primary" ? Now.AddHours(-1) : null,
            testCase.Preference == "switch-primary" ? Now.AddDays(2) : null);
        Func<string, bool> providerAllowed = cli => testCase.Preference != "conserve-fallback"
            || string.Equals(cli, primary, StringComparison.OrdinalIgnoreCase);
        Func<string, string?, bool>? modelAccepted = testCase.Host switch
        {
            "rejects-primary" => (cli, _) => !string.Equals(cli, primary, StringComparison.OrdinalIgnoreCase),
            "rejects-fallback" => (cli, _) => string.Equals(cli, primary, StringComparison.OrdinalIgnoreCase),
            _ => null,
        };
        return new GoldenFacts(
            testCase.Route.Override ? _override : _catalogue,
            _caps,
            cli => cli is not null && snapshots.TryGetValue(cli, out var found) ? found : null,
            Now,
            testCase.Quota == "projected-busy" ? 1 : 0,
            waitPolicy,
            preference,
            modelAccepted,
            testCase.Host == "none" ? null : "golden-host",
            modelAccepted,
            providerAllowed);
    }

    internal GoldenFacts FactsFor(GoldenResolveCase testCase)
    {
        var primary = testCase.Route.CliType;
        Func<string, string?, bool>? accepted = testCase.Acceptance == "rejects-fallback"
            ? (cli, _) => string.Equals(cli, primary, StringComparison.OrdinalIgnoreCase)
            : null;
        Func<string, bool>? allowed = testCase.Acceptance == "conserve-fallback"
            ? cli => string.Equals(cli, primary, StringComparison.OrdinalIgnoreCase)
            : null;
        return new GoldenFacts(
            testCase.Route.Override ? _override : _catalogue,
            _caps, _ => null, Now, 0, null, null, null, null, accepted, allowed);
    }

    internal static CapEvaluation ResolveCap(GoldenResolveCase testCase, string? cli)
    {
        var primary = string.Equals(cli, testCase.Route.CliType, StringComparison.OrdinalIgnoreCase);
        var blocked = primary ? testCase.PrimaryBlocked : testCase.FallbackBlocked;
        return blocked
            ? new CapEvaluation(true, cli, "Weekly", 95, primary ? 97 : 99)
            : CapEvaluation.NotBlocked;
    }

    private static QuotaAdmissionPlan PlanLegacy(GoldenPlanCase testCase, GoldenFacts facts)
        => QuotaAdmissionPlanner.Plan(
            testCase.Route.CliType, testCase.Route.Model, testCase.Route.ThinkingLevel,
            facts.Fallback, facts.Caps, facts.SnapshotFor, facts.NowUtc, facts.OccupiedSlots,
            facts.WaitPolicy, facts.Preference, facts.ModelAccepted, facts.ClaimingHost,
            facts.FallbackModelAccepted, testCase.SameClassOnly, facts.FallbackProviderAllowed);

    private static CliRouteDecision ResolveLegacy(GoldenResolveCase testCase, GoldenFacts facts)
        => facts.Fallback.Resolve(
            testCase.Route.CliType, testCase.Route.Model, testCase.Route.ThinkingLevel,
            cli => ResolveCap(testCase, cli), testCase.PreferFallback,
            facts.FallbackModelAccepted, testCase.SameClassOnly, facts.FallbackProviderAllowed);

    /// <summary>The same facts as a selection request, straight into the contracts-package planner.</summary>
    private static QuotaAdmissionPlan PlanShared(GoldenPlanCase testCase, GoldenFacts facts)
    {
        var request = new SelectionRequest
        {
            Consumer = testCase.SameClassOnly ? SelectionConsumers.ReviewAspect : SelectionConsumers.Coding,
            Pins = new SelectionPins(testCase.Route.CliType, testCase.Route.Model, testCase.Route.ThinkingLevel),
            Required = new SelectionRequirements(SameClassOnly: testCase.SameClassOnly),
            Host = facts.ClaimingHost is null ? null : new SelectionHost(facts.ClaimingHost),
        };
        var selectionFacts = new SelectionFacts
        {
            NowUtc = facts.NowUtc,
            Caps = facts.Caps,
            SnapshotFor = facts.SnapshotFor,
            Routing = facts.Fallback.RoutingFacts(),
            OccupiedSlots = facts.OccupiedSlots,
            WaitPolicy = facts.WaitPolicy,
            OperatorSwitch = facts.Preference,
            FallbackProviderAllowed = facts.FallbackProviderAllowed,
            HostAccepts = facts.ModelAccepted,
            HostAcceptsFallback = facts.FallbackModelAccepted,
        };
        return QuotaAdmissionPlan.From(ModelSelectionPlanner.Plan(request, selectionFacts));
    }

    private static CliRouteDecision ResolveShared(GoldenResolveCase testCase, GoldenFacts facts)
        => ModelSelectionPlanner.ResolveRoute(
            testCase.Route.CliType, testCase.Route.Model, testCase.Route.ThinkingLevel,
            facts.Fallback.RoutingFacts(), cli => ResolveCap(testCase, cli), testCase.PreferFallback,
            facts.FallbackModelAccepted, testCase.SameClassOnly, facts.FallbackProviderAllowed);

    private static QuotaSnapshot? PrimarySnapshot(string cli, string state) => state switch
    {
        "healthy" => Snapshot(cli, "Weekly", 30, Now.AddDays(3)),
        "capped" => Snapshot(cli, "Weekly", 97, Now.AddDays(3)),
        "capped-nearby-wait" => Snapshot(cli, "5-hour", 100, Now.AddMinutes(12)),
        "projected-idle" or "projected-busy" => Snapshot(cli, "Weekly", 70, Now.AddDays(4)),
        "suspicious" => Snapshot(cli, "Weekly", 20, Now.AddDays(3)) with
        {
            Suspicious = true,
            SuspiciousReason = "downward glitch awaiting confirmation",
        },
        _ => null,
    };

    private static QuotaSnapshot Snapshot(string cli, string label, double used, DateTime reset) => new()
    {
        CliType = cli,
        FetchedAt = Now,
        Windows = [new QuotaWindow { Label = label, UsedPct = used, ResetAt = reset }],
    };

    private static string? OtherCli(string cli) => cli switch
    {
        CliTypes.Claude => CliTypes.Codex,
        CliTypes.Codex => CliTypes.Claude,
        _ => null,
    };

    private static object Project(QuotaAdmissionPlan plan) => new
    {
        outcome = plan.Outcome.ToString(),
        cli = plan.CliType,
        model = plan.Model,
        thinking = plan.ThinkingLevel,
        fallback = plan.IsFallback,
        reason = plan.Reason,
        nextResetAt = plan.NextResetAt,
        nearby = plan.NearbyResetWait ? true : (bool?)null,
        projection = plan.Projection is { } p
            ? $"{p.WindowLabel} {p.ProjectedUsedPct:0.###} cap {p.CapPct} breach {p.BreachesBeforeReset}"
            : null,
        warning = plan.ProjectionWarning?.Reason,
        receipt = plan.ModelFallback,
    };

    private static object Project(CliRouteDecision route) => new
    {
        cli = route.CliType,
        model = route.Model,
        thinking = route.ThinkingLevel,
        fallback = route.IsFallback,
        reason = route.Reason,
        primaryCap = route.PrimaryCap.Blocked ? route.PrimaryCap.DescribeReason() : null,
        fallbackReason = route.FallbackReason,
        source = route.FallbackSource,
        catalogue = route.CatalogueVersion,
    };

    /// <summary>Canonical JSON of one outcome. The catalogue version is a placeholder so a Token Economy bump is not a planner change.</summary>
    private static string Value(object value)
        => JsonSerializer.Serialize(value, LineJson).Replace(CatalogueVersion, "{catalogue}", StringComparison.Ordinal);

    private static string Value(JsonElement value) => JsonSerializer.Serialize(value, LineJson);

    private static readonly string CatalogueVersion = new ModelEquivalenceCatalog(TokenTestServices.Pricing()).Version;

    private static string GoldenPath()
        => Path.Combine(AgentStudio.TestSupport.RepositoryRoot.Find(), "backend.Tests", "Golden", "model-selection-planner.jsonl");

    private IConfiguration Config(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = root })
            .Build();
    }

    private CliQuotaFallbackService Fallback(string name)
        => new(Config(name), NullLogger<CliQuotaFallbackService>.Instance,
            new ModelEquivalenceCatalog(TokenTestServices.Pricing()));

    private static void ResetDetectedModels()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        ModelMetadataRegistry.SetDetectedCodexLadders(null);
    }
}

internal sealed record GoldenRoute(string Id, string CliType, string? Model, string? ThinkingLevel, bool Override);

internal sealed record GoldenPlanCase(
    GoldenRoute Route, string Quota, string FallbackQuota, string Preference, string Host, bool SameClassOnly)
{
    public string Id => $"plan/{Route.Id}/{Quota}/fallback-{FallbackQuota}/{Preference}/host-{Host}{(SameClassOnly ? "/same-class" : "")}";
}

internal sealed record GoldenResolveCase(
    GoldenRoute Route, bool PrimaryBlocked, bool FallbackBlocked, bool PreferFallback, string Acceptance, bool SameClassOnly)
{
    public string Id => $"resolve/{Route.Id}/{(PrimaryBlocked ? "primary-capped" : "primary-ok")}/{(FallbackBlocked ? "fallback-capped" : "fallback-ok")}{(PreferFallback ? "/prefer" : "")}/{Acceptance}{(SameClassOnly ? "/same-class" : "")}";
}

internal sealed record GoldenFacts(
    CliQuotaFallbackService Fallback,
    CliQuotaCapsService Caps,
    Func<string?, QuotaSnapshot?> SnapshotFor,
    DateTime NowUtc,
    int OccupiedSlots,
    ResolvedCliQuotaWaitPolicy? WaitPolicy,
    CliFallbackPreference? Preference,
    Func<string, string?, bool>? ModelAccepted,
    string? ClaimingHost,
    Func<string, string?, bool>? FallbackModelAccepted,
    Func<string, bool>? FallbackProviderAllowed);
