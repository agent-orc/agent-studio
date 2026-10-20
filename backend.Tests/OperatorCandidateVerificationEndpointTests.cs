using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OperatorCandidateVerificationEndpointTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-candidate-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishedCandidate_RecordsContainedCards_RefusesMissing_AndRepostsIdempotently()
    {
        var repository = Path.Combine(_root, "repository");
        var remote = Path.Combine(_root, "origin.git");
        var watchPath = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(watchPath);
        Git(_root, "init", "--bare", "-q", remote);
        Git(repository, "init", "-q", "-b", "develop");
        Git(repository, "config", "user.email", "test@example.invalid");
        Git(repository, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(repository, "README.md"), "base\n");
        Git(repository, "add", ".");
        Git(repository, "commit", "-q", "-m", "base");
        var ancestor = Git(repository, "rev-parse", "HEAD");
        Git(repository, "remote", "add", "origin", remote);
        Git(repository, "checkout", "-q", "-b", "delivery");
        File.WriteAllText(Path.Combine(repository, "delivered.txt"), "delivered\n");
        Git(repository, "add", ".");
        Git(repository, "commit", "-q", "-m", "delivery");
        var delivery = Git(repository, "rev-parse", "HEAD");
        Git(repository, "checkout", "-q", "develop");
        Git(repository, "cherry-pick", "-x", delivery);
        var candidate = Git(repository, "rev-parse", "HEAD");
        Git(repository, "push", "-q", "origin", "develop");
        Git(repository, "fetch", "-q", "origin", "develop");
        Git(repository, "checkout", "-q", "-b", "missing", ancestor);
        File.WriteAllText(Path.Combine(repository, "missing.txt"), "missing\n");
        Git(repository, "add", ".");
        Git(repository, "commit", "-q", "-m", "missing");
        var missing = Git(repository, "rev-parse", "HEAD");
        Git(repository, "checkout", "-q", "develop");

        WriteCard(watchPath, "AGT-3166", delivery);
        WriteCard(watchPath, "AGT-3171", ancestor);
        WriteCard(watchPath, "AGT-3185", missing);
        WriteCard(watchPath, "AGT-3187", delivery);
        var legacyFolder = Path.Combine(watchPath, "5-human-review", "AGT-3187");
        ReviewSubjectStore.Write(legacyFolder, ReviewSubjectStore.Read(legacyFolder)! with { RunAttemptId = "" });
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["WatchPaths:0:Name"] = "candidate-test",
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = repository,
                ["WatchPaths:0:RepositoryPath"] = repository,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<IHostedService>();
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"), HandleCookies = false,
        });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", null));
        var token = store.CreateApiToken(new CreateApiTokenRequest("candidate operator", StudioRoles.Operator,
            ["*"], _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.User.Id);
        var project = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindByStorageLocation(watchPath)!;
        factory.Services.GetRequiredService<ProjectSettingsService>()
            .SetIntegrationBranch(project.DisplayName, "develop");
        var path = $"/api/projects/{project.Id}/operator-candidate-verifications";
        var summary = new RecordOperatorCandidateRequest(candidate,
            ["AGT-3166", "AGT-3171", "AGT-3185", "AGT-3187"], "gate-host",
            [new OperatorCandidateSuite("dotnet test", 120, 0, 2)],
            _clock.GetUtcNow().AddMinutes(-40), _clock.GetUtcNow(), new string('a', 64));

        var runner = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("coding-agent", null, null, null)).Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runner.Secret);
        using (var refused = await client.PostAsJsonAsync(path, summary))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);
        using var first = await client.PostAsJsonAsync(path, summary);
        first.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var results = body.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.True(results[0].GetProperty("recorded").GetBoolean());
        Assert.True(results[1].GetProperty("recorded").GetBoolean());
        Assert.False(results[2].GetProperty("recorded").GetBoolean());
        Assert.Contains("not contained", results[2].GetProperty("reason").GetString());
        Assert.False(results[3].GetProperty("recorded").GetBoolean());
        Assert.Contains("current review attempt", results[3].GetProperty("reason").GetString());

        using var repeated = await client.PostAsJsonAsync(path, summary with { Cards = ["AGT-3166"] });
        repeated.EnsureSuccessStatusCode();
        using var replay = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.Equal("Already recorded.", replay.RootElement.GetProperty("results")[0].GetProperty("reason").GetString());
        using var changed = await client.PostAsJsonAsync(path, summary with
        {
            Cards = ["AGT-3166"], VerificationHost = "other-host",
        });
        changed.EnsureSuccessStatusCode();
        using var conflict = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        Assert.False(conflict.RootElement.GetProperty("results")[0].GetProperty("recorded").GetBoolean());

        var cardFolder = Path.Combine(watchPath, "5-human-review", "AGT-3166");
        using var task = JsonDocument.Parse(File.ReadAllText(Path.Combine(cardFolder, "task.json")));
        var record = Assert.Single(task.RootElement.GetProperty("integrationRecords").EnumerateArray());
        Assert.Equal(candidate, record.GetProperty("integrationSha").GetString());
        Assert.Equal(delivery, record.GetProperty("sourceSha").GetString());
        Assert.Equal("gate-host", record.GetProperty("verificationHost").GetString());
        Assert.False(factory.Services.GetRequiredService<TaskMutationService>()
            .RemoveIntegrationRecordOnFolder(cardFolder, record.GetProperty("id").GetString()!));
        var timeline = factory.Services.GetRequiredService<TimelineLog>().ReadAll(cardFolder);
        Assert.Single(timeline, item => item.Details?.GetValueOrDefault("recordId") == record.GetProperty("id").GetString());

        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var patchCard = Assert.Single(scanner.ScanAllJobsRaw(), item => item.Key == "AGT-3166");
        var statuses = factory.Services.GetRequiredService<TaskIntegrationStatusService>();
        var status = statuses.BuildLookup([patchCard])[patchCard.TaskKey];
        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(IntegrationVerificationStates.Verified, status.Verification?.State);
        Assert.Contains("operator candidate", status.Verification?.Reason, StringComparison.OrdinalIgnoreCase);
        await factory.Services.GetRequiredService<DeliveryChainReconciler>().RunOnceAsync();
        Assert.Equal(TaskStates.HumanReview,
            Assert.Single(scanner.ScanAllJobsRaw(), item => item.Key == "AGT-3166").State);

        WriteCard(watchPath, "AGT-3190", delivery);
        scanner.InvalidateCache();

        var mutations = factory.Services.GetRequiredService<TaskMutationService>();
        var parallelRequest = summary with { Cards = ["AGT-3190"] };
        var posts = Enumerable.Range(0, 16).Select(async index =>
        {
            var host = index % 2 == 0 ? "gate-host" : "different-host";
            using var response = await client.PostAsJsonAsync(path, parallelRequest with { VerificationHost = host });
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = payload.RootElement.GetProperty("results")[0];
            return (Host: host, Recorded: item.GetProperty("recorded").GetBoolean(),
                Reason: item.GetProperty("reason").GetString());
        }).ToArray();
        var titleMutation = Task.Run(() =>
        {
            for (var index = 0; index < 16; index++)
                Assert.True(mutations.SetJobTitle("AGT-3190", $"Title {index}", watchPath));
        });
        var posted = await Task.WhenAll(posts);
        await titleMutation;
        var concurrentFolder = Path.Combine(watchPath, "5-human-review", "AGT-3190");
        using var concurrentTask = JsonDocument.Parse(File.ReadAllText(Path.Combine(concurrentFolder, "task.json")));
        var concurrentRecord = Assert.Single(concurrentTask.RootElement.GetProperty("integrationRecords").EnumerateArray());
        var winningHost = concurrentRecord.GetProperty("verificationHost").GetString();
        Assert.Single(posted, item => item.Reason == "Recorded.");
        Assert.All(posted.Where(item => item.Recorded), item => Assert.Equal(winningHost, item.Host));
        Assert.All(posted.Where(item => !item.Recorded), item =>
            Assert.Contains("different evidence", item.Reason));
        Assert.Equal("Title 15", concurrentTask.RootElement.GetProperty("title").GetString());
        Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(concurrentFolder), item =>
            item.Details?.GetValueOrDefault("recordId") == concurrentRecord.GetProperty("id").GetString());

        ReviewSubjectStore.Write(cardFolder, ReviewSubjectStore.Read(cardFolder)! with
        {
            RunAttemptId = "attempt-2",
        });
        scanner.InvalidateCache();
        var newerCard = Assert.Single(scanner.ScanAllJobsRaw(), item => item.Key == "AGT-3166");
        Assert.NotEqual(IntegrationStatuses.Integrated,
            statuses.BuildLookup([newerCard])[newerCard.TaskKey].Status);
        await factory.Services.GetRequiredService<DeliveryChainReconciler>().RunOnceAsync();
        Assert.Equal(TaskStates.AutoReview,
            Assert.Single(scanner.ScanAllJobsRaw(), item => item.Key == "AGT-3166").State);

        using var unpublished = await client.PostAsJsonAsync(path, summary with
        {
            CandidateSha = missing, Cards = ["AGT-3185"],
        });
        Assert.Equal(HttpStatusCode.Conflict, unpublished.StatusCode);

        // The local origin/develop tracking ref still names candidate, while
        // the published branch has moved away from it.
        Git(remote, "update-ref", "refs/heads/develop", ancestor);
        Assert.Equal(candidate, Git(repository, "rev-parse", "refs/remotes/origin/develop"));
        using var stale = await client.PostAsJsonAsync(path, summary with { Cards = ["AGT-3171"] });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Git(repository, "remote", "remove", "origin");
        using var fetchFailed = await client.PostAsJsonAsync(path, summary with { Cards = ["AGT-3171"] });
        Assert.Equal(HttpStatusCode.Conflict, fetchFailed.StatusCode);
    }

    private static void WriteCard(string watchPath, string key, string sha)
    {
        var folder = Path.Combine(watchPath, "5-human-review", key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, title = key, state = "5-human-review", order = 1,
            agent = "codex", cliType = "codex", taskType = "feature",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Fixture delivery.");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = key, RunAttemptId = "attempt-1", AttemptChainId = "chain-1",
            ResultSha = sha, Project = "candidate-test", Repository = "repository",
        });
    }

    private static string Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "OperatorCandidateVerificationEndpointTests cleanup"); }
    }
}
