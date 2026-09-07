using Xunit;

namespace AgentStudio.Scenario.Tests;

public sealed class ScenarioDocumentTests
{
    private const string Minimal = """
        {
          "schemaVersion": 1,
          "id": "sample",
          "title": "Sample",
          "steps": [
            { "id": "one", "title": "First", "level": "smoke", "action": "topology-ready" }
          ]
        }
        """;

    [Fact]
    public void A_minimal_document_loads_with_defaults()
    {
        var step = Assert.Single(Load(Minimal).Steps);

        Assert.Equal("one", step.Id);
        Assert.Equal(ScenarioLevel.Smoke, step.Level);
        // A step without a targets list applies to every target.
        Assert.Equal(3, step.Targets.Count);
        Assert.Equal(0, step.Expect.Count);
    }

    [Fact]
    public void A_step_without_a_level_defaults_to_full_so_it_never_silently_joins_smoke()
    {
        var document = Load("""
            {
              "schemaVersion": 1, "id": "s", "title": "S",
              "steps": [{ "id": "one", "title": "First", "action": "topology-ready" }]
            }
            """);

        Assert.Equal(ScenarioLevel.Full, Assert.Single(document.Steps).Level);
    }

    [Fact]
    public void Typed_expectations_keep_their_json_kind()
    {
        var document = Load("""
            {
              "schemaVersion": 1, "id": "s", "title": "S",
              "steps": [{
                "id": "one", "title": "First", "action": "topology-ready",
                "expect": {
                  "facts": { "a.text": "x", "a.number": 3, "a.boolean": true },
                  "factsAtLeast": { "a.floor": 2 }
                }
              }]
            }
            """);

        var expect = Assert.Single(document.Steps).Expect;
        Assert.Equal(ScenarioFactKind.Text, expect.Exact["a.text"].Kind);
        Assert.Equal(ScenarioFactKind.Number, expect.Exact["a.number"].Kind);
        Assert.Equal(ScenarioFactKind.Boolean, expect.Exact["a.boolean"].Kind);
        Assert.Equal(2, expect.AtLeast["a.floor"]);
    }

    [Theory]
    [InlineData("not json at all", "not valid JSON")]
    [InlineData("""{"schemaVersion":2,"id":"s","title":"S","steps":[]}""", "schemaVersion")]
    [InlineData("""{"schemaVersion":1,"title":"S","steps":[]}""", "id")]
    [InlineData("""{"schemaVersion":1,"id":"s","steps":[]}""", "title")]
    [InlineData("""{"schemaVersion":1,"id":"s","title":"S","steps":[]}""", "steps")]
    public void An_invalid_document_is_refused_with_its_reason(string json, string reason)
    {
        var result = ScenarioDocumentLoader.Parse(json);

        Assert.Null(result.Document);
        Assert.Contains(reason, result.Error);
    }

    [Fact]
    public void Duplicate_step_ids_are_refused_because_a_report_addresses_steps_by_id()
    {
        var result = ScenarioDocumentLoader.Parse("""
            {
              "schemaVersion": 1, "id": "s", "title": "S",
              "steps": [
                { "id": "one", "title": "A", "action": "topology-ready" },
                { "id": "one", "title": "B", "action": "topology-ready" }
              ]
            }
            """);

        Assert.Contains("Duplicate scenario step id 'one'", result.Error);
    }

    [Fact]
    public void An_unknown_target_name_is_refused()
    {
        var result = ScenarioDocumentLoader.Parse("""
            {
              "schemaVersion": 1, "id": "s", "title": "S",
              "steps": [{
                "id": "one", "title": "A", "action": "topology-ready",
                "targets": ["kubernetes"]
              }]
            }
            """);

        Assert.Contains("targets", result.Error);
    }

    [Fact]
    public void An_unknown_level_name_is_refused()
    {
        var result = ScenarioDocumentLoader.Parse("""
            {
              "schemaVersion": 1, "id": "s", "title": "S",
              "steps": [{ "id": "one", "title": "A", "action": "x", "level": "deep" }]
            }
            """);

        Assert.Contains("level", result.Error);
    }

    [Fact]
    public void A_non_integer_expectation_is_refused_rather_than_coerced()
    {
        var result = ScenarioDocumentLoader.Parse("""
            {
              "schemaVersion": 1, "id": "s", "title": "S",
              "steps": [{
                "id": "one", "title": "A", "action": "topology-ready",
                "expect": { "facts": { "a": { "nested": 1 } } }
              }]
            }
            """);

        Assert.Contains("expect.facts.a", result.Error);
    }

    [Fact]
    public void A_floor_must_be_an_integer()
    {
        var result = ScenarioDocumentLoader.Parse("""
            {
              "schemaVersion": 1, "id": "s", "title": "S",
              "steps": [{
                "id": "one", "title": "A", "action": "topology-ready",
                "expect": { "factsAtLeast": { "a": "many" } }
              }]
            }
            """);

        Assert.Contains("expect.factsAtLeast.a", result.Error);
    }

    private static ScenarioDocument Load(string json)
    {
        var result = ScenarioDocumentLoader.Parse(json);
        Assert.Null(result.Error);
        return result.Document!;
    }
}
