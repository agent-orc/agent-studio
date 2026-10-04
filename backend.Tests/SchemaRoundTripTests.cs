using System.Text.Json;

using Xunit;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the round-trip between the JSON schemas under
/// <c>docs/app/schemas/</c> and the C# records that flow through
/// <c>AgentStudio.State</c>: every required field listed in the
/// schema appears as a serialised property on a fresh canonical example, and
/// every documented enum value parses back into its C# enum without loss.
/// </summary>
/// <remarks>
/// We do not pull in a full JSON-Schema validator here. The repository's
/// validation policy is "in-code, mirroring the schema" (see
/// <c>backend/Services/Bus/AgentMessageValidator.cs</c> and
/// <c>backend/Services/State/SupervisorRecordValidator.cs</c>). These tests
/// pin the alignment by serialising a canonical record, parsing the schema,
/// and asserting that every <c>required</c> property the schema lists is
/// present in the serialised JSON.
/// </remarks>
public class SchemaRoundTripTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SupervisorAdvisory_CanonicalExample_ContainsEverySchemaRequiredProperty()
    {
        var advisory = new SupervisorAdvisory(
            CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Severity: SupervisorSeverity.High,
            Source: SupervisorSource.HardCheck,
            Topic: "no-progress",
            Message: "No log line in 12 minutes while job is running.",
            JobId: "refactor-job-service");

        var json = JsonSerializer.SerializeToElement(advisory, WebOptions);
        AssertEveryRequiredPropertyPresent(LoadSchema("supervisor-advisory.schema.json"), json);
    }

    [Fact]
    public void SupervisorIntervention_CanonicalExample_ContainsEverySchemaRequiredProperty()
    {
        var intervention = new SupervisorIntervention(
            CreatedAt: new DateTime(2026, 5, 5, 10, 1, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Kind: SupervisorInterventionKind.PausePickup,
            Source: SupervisorSource.AutoIntervention,
            Reason: "tool-call repeat threshold reached",
            JobId: null,
            PauseTtl: TimeSpan.FromMinutes(30));

        var json = JsonSerializer.SerializeToElement(intervention, WebOptions);
        AssertEveryRequiredPropertyPresent(LoadSchema("supervisor-intervention.schema.json"), json);
    }

    [Fact]
    public void TokenAggregateSchema_PublishesPerCliBudgetWindowShape()
    {
        // No C# record yet for token-aggregate (the in-memory consumer lands
        // with the token-spend timeline work). Lock the schema's published
        // shape so a future refactor cannot quietly drop a required field.
        var schema = LoadSchema("token-aggregate.schema.json");
        var required = ReadStringArray(schema, "required");

        Assert.Contains("project", required);
        Assert.Contains("windowStart", required);
        Assert.Contains("windowEnd", required);
        Assert.Contains("cli", required);
        Assert.Contains("model", required);
        Assert.Contains("tokens", required);

        var properties = schema.GetProperty("properties");
        var cliEnum = ReadStringArray(properties.GetProperty("cli"), "enum");
        Assert.Contains("claude", cliEnum);
        Assert.Contains("codex", cliEnum);
        Assert.Contains("gemini", cliEnum);
        Assert.DoesNotContain("copilot", cliEnum);   // Copilot driver removed — locked out of the schema

        var tokens = properties.GetProperty("tokens");
        var tokensRequired = ReadStringArray(tokens, "required");
        Assert.Contains("input", tokensRequired);
        Assert.Contains("output", tokensRequired);
    }

    [Fact]
    public void SupervisorAdvisorySchema_ListsTheFourSourcesAndThreeSeverities()
    {
        var schema = LoadSchema("supervisor-advisory.schema.json");
        var properties = schema.GetProperty("properties");

        var severities = ReadStringArray(properties.GetProperty("severity"), "enum");
        Assert.Equal(new[] { "Info", "Warn", "High" }, severities);

        var sources = ReadStringArray(properties.GetProperty("source"), "enum");
        Assert.Equal(new[] { "HardCheck", "SoftReasoning", "User", "AutoIntervention" }, sources);

        // Every source spelled in the schema must round-trip through the C# enum.
        foreach (var name in sources)
        {
            Assert.True(Enum.TryParse<SupervisorSource>(name, ignoreCase: false, out _),
                $"Schema source '{name}' has no matching C# enum value.");
        }
        foreach (var name in severities)
        {
            Assert.True(Enum.TryParse<SupervisorSeverity>(name, ignoreCase: false, out _),
                $"Schema severity '{name}' has no matching C# enum value.");
        }
    }

    [Fact]
    public void ClientIdentitySchema_PublishesTheRegistrationBoundaryShape()
    {
        var schema = LoadSchema("client-identity.schema.json");
        var required = ReadStringArray(schema, "required");
        Assert.Contains("id", required);
        Assert.Contains("displayName", required);
        Assert.Contains("kind", required);
        Assert.Contains("registeredAt", required);

        var properties = schema.GetProperty("properties");
        var kinds = ReadStringArray(properties.GetProperty("kind"), "enum");
        Assert.Equal(new[] { "human", "agent-instance", "external-tool", "service", "retired" }, kinds);

        // Every documented kind round-trips through the C# parser.
        Assert.Equal(ClientIdentityKind.Human, ClientIdentityKinds.Parse("human"));
        Assert.Equal(ClientIdentityKind.AgentInstance, ClientIdentityKinds.Parse("agent-instance"));
        Assert.Equal(ClientIdentityKind.ExternalTool, ClientIdentityKinds.Parse("external-tool"));
        Assert.Equal(ClientIdentityKind.Service, ClientIdentityKinds.Parse("service"));
        Assert.Equal(ClientIdentityKind.Retired, ClientIdentityKinds.Parse("retired"));
    }

    [Fact]
    public void TokenAggregateByClientSchema_KeysOnClientIdInAdditionToProject()
    {
        var schema = LoadSchema("token-aggregate-by-client.schema.json");
        var required = ReadStringArray(schema, "required");
        Assert.Contains("clientId", required);
        Assert.Contains("project", required);
        Assert.Contains("windowStart", required);
        Assert.Contains("windowEnd", required);
        Assert.Contains("cli", required);
        Assert.Contains("model", required);
        Assert.Contains("tokens", required);
    }

    [Fact]
    public void AnalysisReport_CanonicalExample_ContainsEverySchemaRequiredProperty()
    {
        var report = new AnalysisReport(
            ReportId: "01HX0000000000000000000001",
            CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            Scope: new AnalysisReportScope(AnalysisReportScopeKind.Project, Project: "agent-taskboard"),
            Producer: new AnalysisReportProducer(AnalysisReportProducerKind.Manual, Agent: "user"),
            Trigger: AnalysisReportTrigger.Manual,
            Topic: "are-we-on-track",
            Summary: "On track with two follow-up suggestions.",
            Severity: AnalysisReportSeverity.Warn,
            ParseStatus: AnalysisReportParseStatus.Structured,
            References: new[]
            {
                new AnalysisReportReference(AnalysisReportReferenceKind.Job, "agent-taskboard/3-progress/sample"),
            },
            FollowUpTaskSuggestions: new[]
            {
                new AnalysisReportFollowUpTaskSuggestion(
                    Title: "Resync ROADMAP.md with queue",
                    Summary: "Two themes drifted.",
                    Priority: AnalysisReportFollowUpPriority.Normal,
                    RelatedTopic: AnalysisReportFollowUpRelatedTopic.RoadmapAlignment),
            });

        // The on-disk format is what consumers compare against.
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var json = JsonSerializer.SerializeToElement(report, serializerOptions);
        AssertEveryRequiredPropertyPresent(LoadSchema("analysis-report.schema.json"), json);
    }

    [Fact]
    public void AnalysisReportSchema_ListsTheFiveScopesProducersAndParseStates()
    {
        var schema = LoadSchema("analysis-report.schema.json");
        var properties = schema.GetProperty("properties");

        var scopes = ReadStringArray(properties.GetProperty("scope").GetProperty("properties").GetProperty("kind"), "enum");
        Assert.Equal(new[] { "Workspace", "Project", "Task", "Run", "TimeWindow" }, scopes);
        foreach (var name in scopes)
        {
            Assert.True(Enum.TryParse<AnalysisReportScopeKind>(name, ignoreCase: false, out _),
                $"Schema scope kind '{name}' has no matching C# enum value.");
        }

        var producerKinds = ReadStringArray(
            properties.GetProperty("producer").GetProperty("properties").GetProperty("kind"), "enum");
        Assert.Equal(new[] { "Manual", "Scheduled", "MetaCycle", "SupportingAgent", "ExternalMonitor" }, producerKinds);
        foreach (var name in producerKinds)
        {
            Assert.True(Enum.TryParse<AnalysisReportProducerKind>(name, ignoreCase: false, out _),
                $"Schema producer kind '{name}' has no matching C# enum value.");
        }

        var triggers = ReadStringArray(properties.GetProperty("trigger"), "enum");
        Assert.Equal(new[] { "Manual", "Scheduled", "MetaCycle", "SupportingAgent", "ExternalMonitor" }, triggers);
        foreach (var name in triggers)
        {
            Assert.True(Enum.TryParse<AnalysisReportTrigger>(name, ignoreCase: false, out _),
                $"Schema trigger '{name}' has no matching C# enum value.");
        }

        var severities = ReadStringArray(properties.GetProperty("severity"), "enum");
        Assert.Equal(new[] { "Info", "Warn", "High", "Critical" }, severities);
        foreach (var name in severities)
        {
            Assert.True(Enum.TryParse<AnalysisReportSeverity>(name, ignoreCase: false, out _),
                $"Schema severity '{name}' has no matching C# enum value.");
        }

        var parseStates = ReadStringArray(properties.GetProperty("parseStatus"), "enum");
        Assert.Equal(new[] { "Structured", "Unstructured", "MalformedJson" }, parseStates);
        foreach (var name in parseStates)
        {
            Assert.True(Enum.TryParse<AnalysisReportParseStatus>(name, ignoreCase: false, out _),
                $"Schema parseStatus '{name}' has no matching C# enum value.");
        }

        // Reference kinds in the schema must each round-trip through the C# enum.
        var refKinds = ReadStringArray(
            properties.GetProperty("references")
                .GetProperty("items")
                .GetProperty("properties")
                .GetProperty("kind"),
            "enum");
        Assert.Equal(
            new[] { "Job", "Run", "Commit", "Screenshot", "BusMessage", "RuntimeEvent", "PreviousReport", "LogSlice", "Doc" },
            refKinds);
        foreach (var name in refKinds)
        {
            Assert.True(Enum.TryParse<AnalysisReportReferenceKind>(name, ignoreCase: false, out _),
                $"Schema reference kind '{name}' has no matching C# enum value.");
        }
    }

    [Fact]
    public void ProtocolHeaderSchema_PublishesPhaseEnumAndRequiredFields()
    {
        var schema = LoadSchema("protocol-header.schema.json");
        var required = ReadStringArray(schema, "required");
        Assert.Contains("phase", required);
        Assert.Contains("summary", required);

        var properties = schema.GetProperty("properties");
        var phases = ReadStringArray(properties.GetProperty("phase"), "enum");
        Assert.Equal(
            new[] { "analysis", "plan", "implementing", "testing", "review", "blocked", "done" },
            phases);

        // summary has a 240-char cap.
        Assert.Equal(240, properties.GetProperty("summary").GetProperty("maxLength").GetInt32());

        // Optional, nullable fields advertised in the schema.
        foreach (var nullableField in new[] { "nextAction", "lastDecisionAt", "correlationId", "agent", "model", "runs" })
        {
            var typeArray = properties.GetProperty(nullableField).GetProperty("type");
            Assert.Equal(JsonValueKind.Array, typeArray.ValueKind);
            var types = new List<string>();
            foreach (var t in typeArray.EnumerateArray()) types.Add(t.GetString() ?? "");
            Assert.Contains("null", types);
        }
    }

    [Fact]
    public void ExecutiveSummarySchema_PublishesWindowHeadlineAndAggregateShape()
    {
        var schema = LoadSchema("executive-summary.schema.json");
        var required = ReadStringArray(schema, "required");
        Assert.Equal(
            new[]
            {
                "windowStart",
                "windowEnd",
                "headline",
                "byProject",
                "crashes",
                "topDecisions",
                "openHumanDecisions",
            },
            required);

        var properties = schema.GetProperty("properties");
        Assert.Equal(240, properties.GetProperty("headline").GetProperty("maxLength").GetInt32());

        // byProject items.
        var byProjectItem = properties.GetProperty("byProject").GetProperty("items");
        var byProjectRequired = ReadStringArray(byProjectItem, "required");
        Assert.Equal(
            new[] { "project", "jobsMoved", "decisionsMade", "advisoriesRaised", "commits" },
            byProjectRequired);

        // topDecisions severity matches the AnalysisReport severity ladder.
        var topDecisionsItem = properties.GetProperty("topDecisions").GetProperty("items");
        var sevs = ReadStringArray(topDecisionsItem.GetProperty("properties").GetProperty("severity"), "enum");
        Assert.Equal(new[] { "Info", "Warn", "High", "Critical" }, sevs);
    }

    [Fact]
    public void DriftReport_CanonicalExample_ContainsEverySchemaRequiredProperty()
    {
        var report = new DriftReport(
            ReportId: "01HX0000000000000000000DRFT",
            Project: "agent-taskboard",
            CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            Trigger: DriftReportTrigger.Manual,
            Scope: new DriftReportScope(DriftReportScopeKind.Project),
            OverallScore: 72,
            ScoreBand: DriftScoreBand.Watch,
            Dimensions: new[]
            {
                new DriftDimension(
                    Type: DriftDimensionType.Architecture,
                    Score: 64,
                    Severity: DriftSeverity.Warn,
                    Confidence: 0.78,
                    SourceCoverage: 0.7,
                    Status: DriftFindingStatus.New,
                    Summary: "Two ADR assumptions are not reflected in the source layout.",
                    EvidenceRefs: new[] { "docs/system/architecture/decisions/adr-archive.md", "backend/Services/Runner/" },
                    RecommendedActions: new[] { "Create architecture follow-up task" },
                    ScoreInputs: new DriftScoreInputs(
                        FindingsBySeverity: new DriftFindingSeverityCounts(Warn: 2),
                        AffectedSurfaces: new[] { "docs/system/architecture/decisions/adr-archive.md", "backend/Services/Runner/" },
                        RecurrenceCount: 1,
                        OldestFindingAgeDays: 14,
                        TrackedFindings: 0,
                        TotalFindings: 2),
                    Findings: new[]
                    {
                        new DriftFinding(
                            FindingId: "01HX-FIND-1",
                            Severity: DriftSeverity.Warn,
                            Summary: "ADR-0017 assumption no longer holds.",
                            Status: DriftFindingStatus.New,
                            FirstSeenAt: new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc),
                            LastSeenAt: new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc),
                            EvidenceRefs: new[] { "docs/system/architecture/decisions/adr-archive.md#adr-0017" }),
                    }),
            },
            Summary: "Two ADR assumptions drifted; queue an architecture follow-up.",
            FollowUpTaskSuggestions: new[]
            {
                new DriftFollowUpSuggestion(
                    Title: "Sync ADR-0017 with current runner code",
                    Summary: "ADR-0017's load-bearing rule no longer matches the runner.",
                    Priority: DriftFollowUpPriority.Normal,
                    RelatedDimension: DriftDimensionType.Architecture),
            },
            Producer: new DriftReportProducer(DriftReportProducerKind.Manual, Agent: "claude"),
            ParseStatus: DriftReportParseStatus.Structured);

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var json = JsonSerializer.SerializeToElement(report, serializerOptions);
        AssertEveryRequiredPropertyPresent(LoadSchema("drift-report.schema.json"), json);
    }

    [Fact]
    public void DriftReportSchema_ListsTheTwelveDimensionsAndFiveStatusStates()
    {
        var schema = LoadSchema("drift-report.schema.json");
        var properties = schema.GetProperty("properties");
        var dimensionItems = properties.GetProperty("dimensions").GetProperty("items").GetProperty("properties");

        var dimensionTypes = ReadStringArray(dimensionItems.GetProperty("type"), "enum");
        Assert.Equal(
            new[]
            {
                "Intent", "Spec", "TaskJob", "Architecture", "Documentation", "Marketing",
                "Design", "Test", "Runtime", "Process", "Schema", "Token",
            },
            dimensionTypes);
        foreach (var name in dimensionTypes)
        {
            Assert.True(Enum.TryParse<DriftDimensionType>(name, ignoreCase: false, out _),
                $"Schema dimension type '{name}' has no matching C# enum value.");
        }

        var statuses = ReadStringArray(dimensionItems.GetProperty("status"), "enum");
        Assert.Equal(new[] { "New", "Accepted", "Ignored", "Tracked", "Resolved" }, statuses);
        foreach (var name in statuses)
        {
            Assert.True(Enum.TryParse<DriftFindingStatus>(name, ignoreCase: false, out _),
                $"Schema status '{name}' has no matching C# enum value.");
        }

        var severities = ReadStringArray(dimensionItems.GetProperty("severity"), "enum");
        Assert.Equal(new[] { "Info", "Warn", "High", "Critical" }, severities);
        foreach (var name in severities)
        {
            Assert.True(Enum.TryParse<DriftSeverity>(name, ignoreCase: false, out _),
                $"Schema severity '{name}' has no matching C# enum value.");
        }
    }

    [Fact]
    public void DriftReportSchema_PublishesProducerTriggerScopeBandAndParseStatusEnums()
    {
        var schema = LoadSchema("drift-report.schema.json");
        var properties = schema.GetProperty("properties");

        var producerKinds = ReadStringArray(
            properties.GetProperty("producer").GetProperty("properties").GetProperty("kind"), "enum");
        Assert.Equal(new[] { "Manual", "Scheduled", "MetaCycle", "SupportingAgent", "ExternalMonitor" }, producerKinds);
        foreach (var name in producerKinds)
        {
            Assert.True(Enum.TryParse<DriftReportProducerKind>(name, ignoreCase: false, out _),
                $"Schema producer kind '{name}' has no matching C# enum value.");
        }

        var triggers = ReadStringArray(properties.GetProperty("trigger"), "enum");
        Assert.Equal(new[] { "Manual", "Scheduled", "MetaCycle", "SupportingAgent", "ExternalMonitor" }, triggers);
        foreach (var name in triggers)
        {
            Assert.True(Enum.TryParse<DriftReportTrigger>(name, ignoreCase: false, out _),
                $"Schema trigger '{name}' has no matching C# enum value.");
        }

        var scopes = ReadStringArray(
            properties.GetProperty("scope").GetProperty("properties").GetProperty("kind"), "enum");
        Assert.Equal(new[] { "Workspace", "Project", "Task", "Run", "TimeWindow" }, scopes);
        foreach (var name in scopes)
        {
            Assert.True(Enum.TryParse<DriftReportScopeKind>(name, ignoreCase: false, out _),
                $"Schema scope kind '{name}' has no matching C# enum value.");
        }

        var bands = ReadStringArray(properties.GetProperty("scoreBand"), "enum");
        Assert.Equal(new[] { "Healthy", "Watch", "Warn", "Critical", "Unknown" }, bands);
        foreach (var name in bands)
        {
            Assert.True(Enum.TryParse<DriftScoreBand>(name, ignoreCase: false, out _),
                $"Schema score band '{name}' has no matching C# enum value.");
        }

        var parseStates = ReadStringArray(properties.GetProperty("parseStatus"), "enum");
        Assert.Equal(new[] { "Structured", "Unstructured", "MalformedJson" }, parseStates);
        foreach (var name in parseStates)
        {
            Assert.True(Enum.TryParse<DriftReportParseStatus>(name, ignoreCase: false, out _),
                $"Schema parseStatus '{name}' has no matching C# enum value.");
        }
    }

    [Fact]
    public void DriftReportSchema_ArchitectureModelHardLimitsTenElements()
    {
        var schema = LoadSchema("drift-report.schema.json");
        var elements = schema.GetProperty("properties")
            .GetProperty("architectureModel")
            .GetProperty("properties")
            .GetProperty("elements");
        Assert.Equal(1, elements.GetProperty("minItems").GetInt32());
        Assert.Equal(10, elements.GetProperty("maxItems").GetInt32());

        var elementProps = elements.GetProperty("items").GetProperty("properties");
        var elementStatuses = ReadStringArray(elementProps.GetProperty("status"), "enum");
        Assert.Equal(new[] { "New", "Accepted", "Ignored", "Tracked", "Resolved" }, elementStatuses);
        var elementSeverities = ReadStringArray(elementProps.GetProperty("severity"), "enum");
        Assert.Equal(new[] { "Info", "Warn", "High", "Critical" }, elementSeverities);
    }

    [Fact]
    public void SupervisorInterventionSchema_ListsTheFourPreEmptiveKinds()
    {
        var schema = LoadSchema("supervisor-intervention.schema.json");
        var kinds = ReadStringArray(schema.GetProperty("properties").GetProperty("kind"), "enum");
        Assert.Equal(new[] { "CancelRun", "PausePickup", "ForceFail", "Resume" }, kinds);

        foreach (var name in kinds)
        {
            Assert.True(Enum.TryParse<SupervisorInterventionKind>(name, ignoreCase: false, out _),
                $"Schema kind '{name}' has no matching C# enum value.");
        }
    }

    private static JsonElement LoadSchema(string fileName)
    {
        var path = ResolveSchemaPath(fileName);
        Assert.True(File.Exists(path), $"Schema not found at {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private static string ResolveSchemaPath(string fileName)
    {
        return Path.Combine(RepositoryRoot.Find(), "docs", "app", "schemas", fileName);
    }

    private static string[] ReadStringArray(JsonElement element, string property)
    {
        var array = element.GetProperty(property);
        var list = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray()) list.Add(item.GetString() ?? "");
        return list.ToArray();
    }

    private static void AssertEveryRequiredPropertyPresent(JsonElement schema, JsonElement instance)
    {
        var required = ReadStringArray(schema, "required");
        foreach (var name in required)
        {
            Assert.True(instance.TryGetProperty(name, out _),
                $"Required property '{name}' missing from serialised instance.");
        }
    }
}
