using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3877 managed databases in Studio: the project.yml schema 2
/// <c>databases</c> section and its JSON schema, attachment to run-zone
/// environments, the deploy job snapshot in the desired state, generated
/// credentials stored and bound through the secret provider and issued only to
/// the placed host, no value in logs or state, refused major version changes,
/// and deletion that waits for an owner and for the data to be undeclared.
/// </summary>
public sealed class ManagedDatabaseTests : IDisposable
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
          id: demo
        artifacts:
          - name: app
            type: container
            dockerfile: Dockerfile
            image: ghcr.io/example/app
        deploy:
          - step: container
            artifact: app
            target: run-host
            composeFile: /srv/apps/demo/compose.yml
            service: app
            healthUrl: https://stage.example.com/healthz
            healthTimeoutSeconds: 60
        """;

    private const string Orders = """

        databases:
          - name: orders
            engine: postgres
            version: "16.4"
            environments: [staging]
        """;

    private const string Reference = "secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "managed-database-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly string _artifact = "registry.example/app@sha256:" + new string('a', 64);
    private readonly string _next = "registry.example/app@sha256:" + new string('b', 64);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Schema_ParsesDatabasesWithDefaults()
    {
        var result = ProjectDefinitionReader.Parse(Head + Orders + "\n" + """
              - name: audit_log
                engine: postgres
                version: "17"
                size: large
                variable: AUDIT_DATABASE_URL
            """);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(item => $"{item.Path}: {item.Message}")));
        var databases = result.Definition!.Deployment!.Databases;
        Assert.Equal(("orders", "postgres", "16.4", "small", "DATABASE_URL"),
            (databases[0].Name, databases[0].Engine, databases[0].Version, databases[0].Size, databases[0].Variable));
        Assert.Equal(["staging"], databases[0].Environments);
        Assert.Equal(("17", "large", "AUDIT_DATABASE_URL"), (databases[1].Major, databases[1].Size, databases[1].Variable));
        Assert.Equal(new ProjectDatabaseResources(2.0, 4096, 1024), ProjectDatabaseVocabulary.ResourcesFor("large"));
    }

    [Theory]
    [InlineData("    engine: mysql\n", "database-engine-invalid")]
    [InlineData("    version: \"13\"\n", "database-version-unsupported")]
    [InlineData("    version: 16.x\n", "database-version-invalid")]
    [InlineData("    size: huge\n", "database-size-invalid")]
    [InlineData("    variable: lower_case\n", "database-variable-invalid")]
    [InlineData("    variable: LD_PRELOAD\n", "database-variable-reserved")]
    [InlineData("    environments: [qa]\n", "database-environment-unknown")]
    [InlineData("    replicas: 2\n", "unknown-field")]
    public void Schema_RejectsInvalidDatabaseFields(string field, string code)
    {
        var yaml = Head + "\ndatabases:\n  - name: orders\n" + (field.Contains("engine") ? "" : "    engine: postgres\n")
            + (field.Contains("version") ? "" : "    version: \"16\"\n") + field;

        var result = ProjectDefinitionReader.Parse(yaml);

        Assert.Contains(result.Issues, item => item.Code == code);
    }

    [Theory]
    [InlineData("  - name: Orders\n    engine: postgres\n    version: \"16\"\n", "database-name-invalid")]
    [InlineData("  - name: orders\n    engine: postgres\n    version: \"16\"\n  - name: orders\n    engine: postgres\n    version: \"16\"\n",
        "database-duplicate")]
    [InlineData("  - name: orders\n    engine: postgres\n    version: \"16\"\n  - name: audit\n    engine: postgres\n    version: \"16\"\n",
        "database-variable-duplicate")]
    public void Schema_RejectsNamesAndVariablesThatCollide(string entries, string code)
    {
        var result = ProjectDefinitionReader.Parse(Head + "\ndatabases:\n" + entries);

        Assert.Contains(result.Issues, item => item.Code == code);
    }

    [Fact]
    public void Schema_AllowsTheSameNamePerEnvironment_AndRefusesShadowedOrReservedVariables()
    {
        var perEnvironment = Head + """

            databases:
              - name: orders
                engine: postgres
                version: "16"
                environments: [staging]
              - name: orders
                engine: postgres
                version: "16"
                size: large
                environments: [production]
            """;
        Assert.True(ProjectDefinitionReader.Parse(perEnvironment).IsValid);

        var shadowed = ProjectDefinitionReader.Parse(Head + Orders + "\n" + """
            variables:
              - name: DATABASE_URL_FILE
                value: /tmp/elsewhere
              - name: OTHER
                secret: MANAGED_DB_ORDERS_PASSWORD
            """);
        Assert.Contains(shadowed.Issues, item => item.Code == "database-variable-shadowed");
        Assert.Contains(shadowed.Issues, item => item.Code == "variable-secret-reserved");

        var schemaOne = ProjectDefinitionReader.Parse(Head.Replace("schemaVersion: 2", "schemaVersion: 1", StringComparison.Ordinal)
            .Replace("project:\n  id: demo\n", "", StringComparison.Ordinal) + Orders);
        Assert.Contains(schemaOne.Issues, item => item.Code == "v1-does-not-support-deployment");
    }

    [Fact]
    public void JsonSchema_DeclaresTheDatabaseVocabulary()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "docs", "app", "schemas", "project-execution.schema.json")));
        var item = schema.RootElement.GetProperty("properties").GetProperty("databases").GetProperty("items");
        var properties = item.GetProperty("properties");

        Assert.Equal(ProjectDatabaseVocabulary.Keys, properties.EnumerateObject().Select(entry => entry.Name));
        Assert.Equal(ProjectDatabaseVocabulary.Engines,
            properties.GetProperty("engine").GetProperty("enum").EnumerateArray().Select(entry => entry.GetString()));
        Assert.Equal(ProjectDatabaseVocabulary.Sizes,
            properties.GetProperty("size").GetProperty("enum").EnumerateArray().Select(entry => entry.GetString()));
        Assert.Equal(ProjectDatabaseVocabulary.NamePattern, properties.GetProperty("name").GetProperty("pattern").GetString());
        Assert.Equal(ProjectDatabaseVocabulary.VersionPattern, properties.GetProperty("version").GetProperty("pattern").GetString());
        Assert.Contains(schema.RootElement.GetProperty("allOf")[0].GetProperty("then").GetProperty("not").GetProperty("anyOf")
            .EnumerateArray(), entry => entry.GetProperty("required")[0].GetString() == "databases");
    }

    [Fact]
    public void Declarations_AttachDatabasesToRunZoneEnvironments_AndRefuseOthers()
    {
        var service = Build(Orders);

        var stage = service.Declarations("demo")!.Single(item => item.Id == "stage");
        Assert.Equal("orders", Assert.Single(stage.Databases).Name);
        // The run zone executes the job, so schema 2 adds no SSH steps for the host to report on.
        Assert.Equal(("container", 0), (stage.Template, stage.SshSteps.Count));
        Assert.Empty(service.Declarations("demo")!.Single(item => item.Id == "production").Databases);

        WriteDefinition(Orders.Replace("\n    environments: [staging]", "", StringComparison.Ordinal));
        var production = service.DeclarationFiles("demo")!.Single(item => item.EnvironmentId == "production");
        Assert.Null(production.Declaration);
        Assert.Contains("not hosted in the run zone", production.Error);
    }

    [Fact]
    public async Task DesiredState_SendsTheJobSnapshot_WithACredentialReference_AndTheGeneratedValueResolvesOnlyForThePlacedHost()
    {
        var service = Build(Orders);
        var (provider, bindings) = Secrets(service);
        var logs = new ListLogger<ManagedDatabaseCredentials>();
        var credentials = new ManagedDatabaseCredentials(service, bindings, provider, logs);
        Queue(service, _artifact);
        // A later project.yml change waits for the next deploy.
        WriteDefinition(Orders.Replace("16.4", "16.6", StringComparison.Ordinal));

        await credentials.EnsureForHostAsync("run-host", CancellationToken.None);
        await credentials.EnsureForHostAsync("run-host", CancellationToken.None);
        var desired = service.DesiredState("run-host");

        var database = Assert.Single(Assert.Single(desired.Environments).Databases!);
        Assert.Equal(new RunTargetDatabaseSpec("orders", "postgres", "16.4", "small", new RunTargetResources(0.5, 512, 256),
            "DATABASE_URL", Reference), database);
        var binding = Assert.Single(bindings.List("demo", "stage"));
        Assert.Equal((Reference, ProjectSecretBindings.ManagedDatabaseActor), (binding.Reference, binding.ApprovedBy));
        Assert.Single((await provider.ListAsync(SecretScope.ForEnvironment("demo", "stage"))).Records);

        var lease = service.ManagedDatabaseCredentialLease("demo", "stage")!;
        var names = lease.Names.Select(ProjectDatabaseVocabulary.CredentialName).ToArray();
        var credentialLease = new RunTargetCredentialLease("demo", "stage", lease.HostId, names,
            bindings.ApprovedReferences("demo", "stage", names, lease.EnvironmentKind));
        var (workload, allowed) = SecretWorkloadBinding.DecideRunTarget(Runner("run-host"), credentialLease);
        Assert.True(allowed.Allowed);
        var issued = await provider.GetForWorkloadAsync(SecretReference.Parse(Reference), workload!, "run-target:managed-database");
        Assert.Equal(43, issued.Value.Length);
        Assert.Equal("workload-identity-denied", SecretWorkloadBinding.DecideRunTarget(Runner("other-host"), credentialLease).Decision.Code);
        Assert.Equal("runner-scope-denied", SecretWorkloadBinding.DecideRunTarget(
            Runner("run-host") with { Scopes = new HashSet<string> { RunnerScopes.Lease } }, credentialLease).Decision.Code);
        Assert.False(SecretAccessPolicy.ForWorkload(SecretReference.Parse("secret://demo/production/MANAGED_DB_ORDERS_PASSWORD"),
            workload!, "run-target:managed-database").Allowed);
        Assert.Null(service.ManagedDatabaseCredentialLease("demo", "production"));

        Assert.DoesNotContain(issued.Value, JsonSerializer.Serialize(desired));
        Assert.DoesNotContain(issued.Value, File.ReadAllText(StatePath));
        Assert.DoesNotContain(issued.Value, JsonSerializer.Serialize(service.ManagedDatabases("demo", "stage")));
        Assert.All(logs.Lines, line => Assert.DoesNotContain(issued.Value, line));
        Assert.Contains(logs.Lines, line => line.Contains(Reference, StringComparison.Ordinal));
    }

    [Fact]
    public void HostObservations_UpdateTheView_AndAMajorVersionChangeIsRefusedBeforeTheHostSeesIt()
    {
        var service = Build(Orders);
        Deploy(service, _artifact);
        Observe(service, "running", "16.4", 4096);

        var view = Assert.Single(service.ManagedDatabases("demo", "stage")!);
        Assert.Equal(("running", "16.4", 4096L, Reference), (view.State, view.RunningVersion, view.VolumeBytes, view.CredentialReference));
        Assert.Equal(512, view.Resources.MemoryMb);

        WriteDefinition(Orders.Replace("16.4", "17", StringComparison.Ordinal));
        Queue(service, _next);
        var desired = Assert.Single(service.DesiredState("run-host").Environments);

        Assert.Null(desired.JobId);
        Assert.Equal(_artifact, desired.Artifact);
        Assert.Equal("16.4", Assert.Single(desired.Databases!).Version);
        Assert.Equal("failed", Load().Jobs.Last().Status);
        Assert.Contains("major version change is refused", Assert.Single(service.ManagedDatabases("demo", "stage")!).Message);
    }

    [Fact]
    public void Deletion_WaitsUntilNoDeployDeclaresTheDatabase_ThenTheHostConfirms()
    {
        var service = Build(Orders);
        Deploy(service, _artifact);
        Observe(service, "running", "16.4", 4096);

        Assert.Equal(ManagedDatabaseDeletionOutcome.StillDeclared,
            service.RequestDatabaseDeletion("demo", "stage", "orders", "owner-1"));
        Assert.Equal(ManagedDatabaseDeletionOutcome.NotFound,
            service.RequestDatabaseDeletion("demo", "stage", "billing", "owner-1"));
        Assert.Empty(service.DesiredState("run-host").DatabaseDeletions!);

        // project.yml drops the database; until a deploy runs, the running release still declares it.
        WriteDefinition("");
        Assert.Equal(ManagedDatabaseDeletionOutcome.StillDeclared,
            service.RequestDatabaseDeletion("demo", "stage", "orders", "owner-1"));
        Deploy(service, _next);
        Observe(service, "retained", "16.4", 4096);
        var retained = Assert.Single(service.ManagedDatabases("demo", "stage")!);
        Assert.Equal(("retained", false), (retained.State, retained.Declared));
        Assert.Empty(Assert.Single(service.DesiredState("run-host").Environments).Databases!);

        Assert.Equal(ManagedDatabaseDeletionOutcome.Requested,
            service.RequestDatabaseDeletion("demo", "stage", "orders", "owner-1"));
        Assert.Equal(ManagedDatabaseDeletionOutcome.AlreadyRequested,
            service.RequestDatabaseDeletion("demo", "stage", "orders", "owner-1"));
        Assert.Equal("deleting", Assert.Single(service.ManagedDatabases("demo", "stage")!).State);
        Assert.Equal(new RunTargetDatabaseDeletion("demo", "stage", "orders"),
            Assert.Single(service.DesiredState("run-host").DatabaseDeletions!));
        Assert.Empty(service.DesiredState("other-host").DatabaseDeletions!);

        // An unrequested "deleted" report from another host changes nothing.
        service.RecordObserved("other-host", new([], [new("demo", "stage", "orders", "deleted", null, null, Now)]));
        Assert.Equal("deleting", Assert.Single(service.ManagedDatabases("demo", "stage")!).State);
        Observe(service, "deleted", null, null);

        var deleted = Assert.Single(service.ManagedDatabases("demo", "stage")!);
        Assert.Equal(("deleted", "owner-1"), (deleted.State, deleted.DeletionRequestedBy));
        Assert.NotNull(deleted.DeletedAt);
        Assert.Empty(service.DesiredState("run-host").DatabaseDeletions!);
        Assert.Equal(new ManagedDatabaseCredentialNeed("demo", "stage", "", "orders"),
            Assert.Single(service.RetiringDatabaseCredentials()));
        service.MarkDatabaseCredentialRetired("demo", "stage", "orders");
        Assert.Empty(service.RetiringDatabaseCredentials());
    }

    [Fact]
    public async Task RetiringADeletedDatabase_RemovesItsCredentialAndBinding()
    {
        var service = Build(Orders);
        var (provider, bindings) = Secrets(service);
        var credentials = new ManagedDatabaseCredentials(service, bindings, provider, NullLogger<ManagedDatabaseCredentials>.Instance);
        Queue(service, _artifact);
        await credentials.EnsureForHostAsync("run-host", CancellationToken.None);
        Report(service, healthy: true);
        WriteDefinition("");
        Deploy(service, _next);
        Assert.Equal(ManagedDatabaseDeletionOutcome.Requested, service.RequestDatabaseDeletion("demo", "stage", "orders", "owner-1"));
        Observe(service, "deleted", null, null);

        await credentials.RetireDeletedAsync(CancellationToken.None);

        Assert.Empty(bindings.List("demo", "stage"));
        Assert.Empty((await provider.ListAsync(SecretScope.ForEnvironment("demo", "stage"))).Records);
        Assert.Empty(service.RetiringDatabaseCredentials());
    }

    [Fact]
    public void HostReports_AreSanitized()
    {
        var hostile = new RunTargetDatabaseObservation("demo", "stage", "orders", "running", "16; rm -rf /", -5, Now.AddDays(1));

        var sanitized = ManagedDatabasePolicy.Sanitize(hostile, Now)!;

        Assert.Null(sanitized.Version);
        Assert.Equal(0, sanitized.VolumeBytes);
        Assert.Equal(Now, sanitized.ObservedAt);
        Assert.Null(ManagedDatabasePolicy.Sanitize(hostile with { State = "pwned" }, Now));
        Assert.Null(ManagedDatabasePolicy.Sanitize(hostile with { Name = "../etc" }, Now));
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private string StatePath => Path.Combine(_root, "workspace", "deployments", "demo.json");

    private ProjectEnvironmentService.State Load()
        => JsonSerializer.Deserialize<ProjectEnvironmentService.State>(File.ReadAllText(StatePath), JsonSerializerOptions.Web)!;

    private static RunnerPrincipal Runner(string id) => new(id, id, "credential-" + id, new HashSet<string> { RunnerScopes.Deploy });

    private void Queue(ProjectEnvironmentService service, string artifact)
        => Assert.NotNull(service.QueueDeployment("demo", service.Declarations("demo")!.Single(item => item.Id == "stage"),
            artifact, "operator", rollback: false));

    private void Report(ProjectEnvironmentService service, bool healthy)
    {
        var spec = Assert.Single(service.DesiredState("run-host").Environments);
        Assert.True(service.Report("demo", spec.JobId!, new(spec.LeaseToken!, healthy, healthy ? "health-ok" : "health-http-failed")));
    }

    private void Deploy(ProjectEnvironmentService service, string artifact)
    {
        Queue(service, artifact);
        Report(service, healthy: true);
    }

    private void Observe(ProjectEnvironmentService service, string state, string? version, long? bytes)
    {
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, service.RecordObserved("run-host", new([], [new("demo", "stage", "orders", state, version, bytes, Now)])));
    }

    private (SecretProvider Provider, ProjectSecretBindings Bindings) Secrets(ProjectEnvironmentService service)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek"),
        }).Build();
        var provider = new SecretProvider(
            new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { })),
            new SecretAuditLog(config), new SecretValueScrubber(_clock), _clock, NullLogger<SecretProvider>.Instance);
        return (provider, new ProjectSecretBindings(config, provider, service, _clock));
    }

    private string Repository => Path.Combine(_root, "repo");

    private void WriteDefinition(string databases)
    {
        Directory.CreateDirectory(Path.Combine(Repository, ".agent-studio"));
        File.WriteAllText(Path.Combine(Repository, ".agent-studio", "project.yml"), Head + databases);
    }

    private ProjectEnvironmentService Build(string databases)
    {
        var watch = Path.Combine(_root, "workspace", "projects", "demo");
        Directory.CreateDirectory(watch);
        Directory.CreateDirectory(Repository);
        Git(Repository, "init", "-b", "develop");
        Git(Repository, "config", "user.email", "test@example.com");
        Git(Repository, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(Repository, "initial"), "initial");
        Git(Repository, "add", ".");
        Git(Repository, "commit", "-m", "initial");
        var declarations = Path.Combine(Repository, "docs", "deployments", "environments");
        Directory.CreateDirectory(declarations);
        File.WriteAllText(Path.Combine(declarations, "stage.json"), JsonSerializer.Serialize(new
        {
            id = "stage", name = "Stage", kind = "stage", targetHostId = "run-host",
            address = "https://stage.example.com", template = "container", destination = "edge",
            healthUrl = "https://stage.example.com/healthz", secretRefs = Array.Empty<string>(),
            domains = new[] { "stage.example.com" },
        }));
        WriteDefinition(databases);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = watch,
            ["WatchPaths:0:RootPath"] = Repository, ["WatchPaths:0:RepositoryPath"] = Repository,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetRunTargetDomainSuffixes("demo", ["example.com"]);
        return new(config, scanner, git, settings, _clock);
    }

    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
