using AgentStudio.ModelMigrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TokenEconomyMigrationCatalogServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-model-migrations-" + Guid.NewGuid().ToString("N"));

    public TokenEconomyMigrationCatalogServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void GetCatalog_ResolvesTheRegisteredTokenEconomyRepository()
    {
        var repository = CreateRepository("te-repository");
        WriteCatalog(repository, ValidCatalog());
        var registry = BuildRegistry(repository, useRootPath: false);

        var service = new TokenEconomyMigrationCatalogService(
            registry,
            NullLogger<TokenEconomyMigrationCatalogService>.Instance);

        var snapshot = service.GetCatalog();

        Assert.NotNull(snapshot.Catalog);
        Assert.Equal("2026-09-06", snapshot.Catalog.CatalogVersion);
        Assert.Equal(
            Path.Combine(repository, "src", "TokenEconomy", "catalog", "model-migrations.v1.json"),
            snapshot.Source);
        Assert.Null(snapshot.Error);
        Assert.False(snapshot.IsStale);
    }

    [Fact]
    public void GetCatalog_FallsBackToTheRegisteredRootPath()
    {
        var repository = CreateRepository("te-root");
        WriteCatalog(repository, ValidCatalog());
        var registry = BuildRegistry(repository, useRootPath: true);

        var service = new TokenEconomyMigrationCatalogService(
            registry,
            NullLogger<TokenEconomyMigrationCatalogService>.Instance);

        Assert.NotNull(service.GetCatalog().Catalog);
    }

    [Fact]
    public void GetCatalog_CachesThenRetainsTheLastGoodCatalogWhenRefreshFails()
    {
        var repository = CreateRepository("cached");
        WriteCatalog(repository, ValidCatalog());
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var resolutions = 0;
        var service = new TokenEconomyMigrationCatalogService(
            () =>
            {
                resolutions++;
                return repository;
            },
            NullLogger<TokenEconomyMigrationCatalogService>.Instance,
            time,
            TimeSpan.FromMinutes(5));

        var loaded = service.GetCatalog();
        WriteCatalog(repository, "{ not-json }");

        var cached = service.GetCatalog();
        Assert.Same(loaded, cached);
        Assert.Equal(1, resolutions);

        time.Advance(TimeSpan.FromMinutes(5));
        var stale = service.GetCatalog();

        Assert.Equal(2, resolutions);
        Assert.Same(loaded.Catalog, stale.Catalog);
        Assert.Equal(loaded.CapturedAtUtc, stale.CapturedAtUtc);
        Assert.NotNull(stale.Error);
        Assert.True(stale.IsStale);
    }

    [Fact]
    public void GetCatalog_WithoutAnyValidSourceFailsClosed()
    {
        var service = new TokenEconomyMigrationCatalogService(
            () => _root,
            NullLogger<TokenEconomyMigrationCatalogService>.Instance,
            new FakeTimeProvider(),
            TimeSpan.Zero);

        var snapshot = service.GetCatalog();

        Assert.Null(snapshot.Catalog);
        Assert.Null(snapshot.CapturedAtUtc);
        Assert.NotNull(snapshot.Error);
        Assert.False(snapshot.IsStale);
    }

    [Theory]
    [InlineData("claude-haiku", ModelFamilies.ClaudeHaiku)]
    [InlineData("claude-sonnet", ModelFamilies.ClaudeSonnet)]
    [InlineData("claude-opus", ModelFamilies.ClaudeOpus)]
    [InlineData("gpt-mini", ModelFamilies.GptMini)]
    [InlineData("gpt", ModelFamilies.GptFlagship)]
    public void FamilyMap_UsesStudioFamilyIds(string tokenEconomyFamily, string expected)
    {
        Assert.Equal(expected, ModelMigrationFamilyMap.ToStudioFamily(tokenEconomyFamily));
    }

    [Theory]
    [InlineData("unknown", true, 405, 500, true, "noRegression")]
    [InlineData("premium", true, 500, 405, true, "noRegression")]
    [InlineData("premium", true, 405, 500, false, "noRegression")]
    [InlineData("premium", true, 405, 500, true, "inconclusive")]
    public void GetCatalog_RejectsRulesThatMislabelUnsafeChangesAsSafe(
        string targetCost,
        bool safeAuto,
        int fromGeneration,
        int toGeneration,
        bool ladderCompatible,
        string conclusion)
    {
        var repository = CreateRepository(Guid.NewGuid().ToString("N"));
        WriteCatalog(repository, ValidCatalog(
            targetCost,
            safeAuto,
            fromGeneration,
            toGeneration,
            ladderCompatible,
            conclusion));
        var service = new TokenEconomyMigrationCatalogService(
            () => repository,
            NullLogger<TokenEconomyMigrationCatalogService>.Instance,
            new FakeTimeProvider(),
            TimeSpan.Zero);

        var snapshot = service.GetCatalog();

        Assert.Null(snapshot.Catalog);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public void GetCatalog_AcceptsProposalOnlyMigrationAcrossFamilies()
    {
        var repository = CreateRepository("proposal-across-families");
        WriteCatalog(
            repository,
            ValidCatalog(safeAuto: false)
                .Replace("claude-opus-5", "claude-sonnet-5", StringComparison.Ordinal));
        var service = new TokenEconomyMigrationCatalogService(
            () => repository,
            NullLogger<TokenEconomyMigrationCatalogService>.Instance,
            new FakeTimeProvider(),
            TimeSpan.Zero);

        var snapshot = service.GetCatalog();

        Assert.NotNull(snapshot.Catalog);
        Assert.False(snapshot.Catalog.Migrations[0].SafeAuto);
    }

    [Fact]
    public void GetCatalog_RejectsSafeMigrationAcrossFamilies()
    {
        var repository = CreateRepository("safe-across-families");
        WriteCatalog(
            repository,
            ValidCatalog()
                .Replace("claude-opus-5", "claude-sonnet-5", StringComparison.Ordinal));
        var service = new TokenEconomyMigrationCatalogService(
            () => repository,
            NullLogger<TokenEconomyMigrationCatalogService>.Instance,
            new FakeTimeProvider(),
            TimeSpan.Zero);

        var snapshot = service.GetCatalog();

        Assert.Null(snapshot.Catalog);
        Assert.NotNull(snapshot.Error);
    }

    private ProjectRegistry BuildRegistry(string repository, bool useRootPath)
    {
        var taskRepository = Path.Combine(_root, "workspace-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = taskRepository,
            })
            .Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var record = registry.EnsureProjectForStorage(
            Path.Combine(taskRepository, "projects", "PROJ-015", "tasks"),
            "Token Economy",
            "ws-default");
        if (useRootPath)
            registry.SetRootPath(record.Id, repository);
        else
            registry.SetRepositoryPath(record.Id, repository);
        return registry;
    }

    private string CreateRepository(string name)
    {
        var repository = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        return repository;
    }

    private static void WriteCatalog(string repository, string json)
    {
        var path = Path.Combine(repository, "src", "TokenEconomy", "catalog", "model-migrations.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static string ValidCatalog(
        string targetCost = "premium",
        bool safeAuto = true,
        int fromGeneration = 408,
        int toGeneration = 500,
        bool ladderCompatible = true,
        string conclusion = "noRegression") => $$"""
        {
          "$schema": "model-migrations.v1.schema.json",
          "schemaVersion": 1,
          "catalogVersion": "2026-09-06",
          "evidenceAsOfDate": "2026-09-06",
          "defaultStrategy": "latestInFamily",
          "authority": {
            "rules": "docs/model-migrations.md",
            "routingPolicy": "docs/system/domains/model-routing-policy.md",
            "priceCatalog": "src/TokenEconomy/catalog/model-prices.json"
          },
          "costClassOrder": ["economy", "standard", "premium"],
          "migrations": [
            {
              "from": "claude-opus-4-8",
              "to": "claude-opus-5",
              "family": "claude-opus",
              "vendor": "anthropic",
              "generationOrder": { "from": {{fromGeneration}}, "to": {{toGeneration}} },
              "costClassFrom": "premium",
              "costClassTo": "{{targetCost}}",
              "ladderCompatible": {{ladderCompatible.ToString().ToLowerInvariant()}},
              "contextChange": "increase",
              "evidence": {
                "kind": "controlledBenchmark",
                "reference": "benchmarks/results/example.json",
                "conclusion": "{{conclusion}}",
                "summary": "No regression on identical cases."
              },
              "safeAuto": {{safeAuto.ToString().ToLowerInvariant()}},
              "since": "2026-09-06",
              "note": "Latest same-family successor."
            }
          ],
          "taskClassRecommendations": [{}, {}, {}, {}, {}]
        }
        """;
}
