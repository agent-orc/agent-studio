using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskServer.Tests;

public sealed record ScenarioFixtureFile(string Path, string Content, bool Executable = false);

public sealed record ScenarioFixtureRepository(
    IReadOnlyList<ScenarioFixtureFile> SeedFiles,
    string DefaultBranch);

public sealed record ScenarioFixtureProject(string WorkspaceName, string ProjectName, string TaskKeyPrefix);

public sealed record ScenarioFixtureTask(string Title, string Body, string InitialState);

/// <summary>Deserializes `testsupport/scenario/fixture.json`. Only the sections typed scenario steps actually read are modeled here; the file also carries forward-looking notes (epic/dossier) for future growth.</summary>
public sealed record ScenarioFixture(
    string Description,
    ScenarioFixtureRepository Repository,
    ScenarioFixtureProject Project,
    ScenarioFixtureTask Task)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ScenarioFixture Load(string path)
        => JsonSerializer.Deserialize<ScenarioFixture>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Scenario fixture at '{path}' deserialized to null.");
}

