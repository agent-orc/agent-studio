using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Management;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StructureLibraryDeclarationTests
{
    private const string Head = """
        schemaVersion: 2
        stack: [node]
        toolVersions:
        commands:
          prepare: .agent-studio/prepare
          build: [npm run build]
          test: [npm test]
          lint: []
        testSuites:
        cachePaths: []
        capabilities: [git]
        environment:
        project:
          id: library
        """;

    [Fact]
    public void Registry_declarations_read_artifact_destinations_and_secret_references()
    {
        var result = ProjectDefinitionReader.Parse(Head + "\n" + """
            structure:
              registries:
                - artifact: chat-package
                  provider: jfrog
                  package: coding-agent-chat
                  repository: npm-internal
                  secretRef: secret://Library/registry-read
                - artifact: chat-package
                  provider: npm
                  package: coding-agent-chat
                - artifact: demo-image
                  provider: docker-hub
                  package: chat-demo
            """);
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal(3, result.Definition!.Structure!.Registries.Count);
        Assert.Equal("secret://Library/registry-read", result.Definition.Structure.Registries[0].SecretRef);
    }

    [Fact]
    public void Raw_credential_field_is_rejected()
    {
        var result = ProjectDefinitionReader.Parse(Head + "\n" + """
            structure:
              registries:
                - artifact: chat-package
                  provider: jfrog
                  package: coding-agent-chat
                  credential: plain-value
            """);
        Assert.Contains(result.Issues, issue => issue.Code == "unknown-field");
    }

    [Fact]
    public void Private_registry_requires_a_secret_reference_shape()
    {
        var result = ProjectDefinitionReader.Parse(Head + "\n" + """
            structure:
              registries:
                - artifact: chat-package
                  provider: jfrog
                  package: coding-agent-chat
                  secretRef: plain-value
            """);
        Assert.Contains(result.Issues, issue => issue.Code == "registry-secret-invalid");
    }

    [Fact]
    public void Studio_chat_pin_matches_its_lock_file()
    {
        var root = RepositoryRoot.Find();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "frontend", "package.json")));
        using var locked = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "frontend", "package-lock.json")));
        var pinned = manifest.RootElement.GetProperty("dependencies").GetProperty("coding-agent-chat").GetString();
        var resolved = locked.RootElement.GetProperty("packages").GetProperty("node_modules/coding-agent-chat")
            .GetProperty("version").GetString();
        Assert.Equal(pinned, resolved);
    }

    [Fact]
    public void Registry_response_does_not_serialize_secret_reference_or_credential()
    {
        var row = new LibraryRegistry("library", "chat-package", "jfrog", "coding-agent-chat",
            "npm-internal", true, "unavailable", null, null, []);
        var json = JsonSerializer.Serialize(row);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Consumer_lag_counts_stable_releases_after_the_pinned_version()
    {
        Assert.Equal(2, StructureLibraryService.ReleasesBehind(["0.3.2", "0.4.0", "0.4.1"], "0.3.2"));
        Assert.Equal(0, StructureLibraryService.ReleasesBehind(["0.3.2", "0.4.0", "0.4.1"], "0.4.1"));
        Assert.Null(StructureLibraryService.ReleasesBehind(["0.3.2", "0.4.0", "0.4.1"], "0.5.0-rc.1"));
    }
}
