using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Tasks;
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
        var watchPath = Path.Combine(_root, "workspace");
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
        WriteCard(watchPath, "AGT-3190", ancestor);
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
            ["AGT-3166", "AGT-3171", "AGT-3185"], "gate-host",
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

        File.WriteAllText(Path.Combine(repository, "later.txt"), "later card\n");
        Git(repository, "add", ".");
        Git(repository, "commit", "-q", "-m", "later card");
        Git(repository, "push", "-q", "origin", "develop");

        Git(repository, "checkout", "-q", "-b", "late-attribution", ancestor);
        Git(repository, "cherry-pick", delivery);
        Git(repository, "commit", "-q", "--amend", "-m", "late attribution of the same patch");
        var lateAttribution = Git(repository, "rev-parse", "HEAD");
        Git(repository, "checkout", "-q", "develop");
        var cardFolder = Path.Combine(watchPath, "5-human-review", "AGT-3166");
        TaskJsonFile.UpdateFieldOrThrow(cardFolder, "commits", new[]
        {
            new { sha = lateAttribution, shortSha = lateAttribution[..7], message = "late attribution",
                runAttemptId = "attempt-1", resultSha = delivery, deliveryGeneration = 1,
                filesChanged = 1, files = new[] { "delivered.txt" } },
        });

        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var mappedCard = scanner.FindJob("AGT-3166", watchPath)!;
        var status = factory.Services.GetRequiredService<TaskIntegrationStatusService>()
            .BuildLookup([mappedCard])[mappedCard.TaskKey];
        // Late attribution was absent when the immutable candidate receipt was
        // written. The status projection cannot run Git to prove equivalence.
        Assert.NotEqual(IntegrationStatuses.Integrated, status.Status);

        TaskJsonFile.UpdateFieldOrThrow(cardFolder, "commits", new[]
        {
            new { sha = missing, shortSha = missing[..7], message = "unrelated late attribution",
                runAttemptId = "attempt-1", resultSha = delivery, deliveryGeneration = 1,
                filesChanged = 1, files = new[] { "missing.txt" } },
        });
        scanner.InvalidateCache();
        var unrelated = scanner.FindJob("AGT-3166", watchPath)!;
        Assert.NotEqual(IntegrationStatuses.Integrated,
            factory.Services.GetRequiredService<TaskIntegrationStatusService>()
                .BuildLookup([unrelated])[unrelated.TaskKey].Status);
        TaskJsonFile.UpdateFieldOrThrow(cardFolder, "commits", new[]
        {
            new { sha = lateAttribution, shortSha = lateAttribution[..7], message = "late attribution",
                runAttemptId = "attempt-1", resultSha = delivery, deliveryGeneration = 1,
                filesChanged = 1, files = new[] { "delivered.txt" } },
        });
        scanner.InvalidateCache();

        using var repeated = await client.PostAsJsonAsync(path, summary with { Cards = ["AGT-3166"] });
        repeated.EnsureSuccessStatusCode();
        using var replay = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.Equal("Already recorded.", replay.RootElement.GetProperty("results")[0].GetProperty("reason").GetString());
        string originalActor;
        using (var beforeReplay = JsonDocument.Parse(File.ReadAllText(Path.Combine(cardFolder, "task.json"))))
            originalActor = beforeReplay.RootElement.GetProperty("integrationRecords")[0]
                .GetProperty("actor").GetString()!;
        var secondOperator = store.CreateUser(new CreateUserRequest(
            "candidate.reviewer", "Candidate reviewer", StudioRoles.Operator, ["*"],
            "temporary candidate phrase!"));
        var secondToken = store.CreateApiToken(new CreateApiTokenRequest(
            "second candidate operator", StudioRoles.Operator, ["*"],
            _clock.GetUtcNow().UtcDateTime.AddDays(1)), secondOperator.User.Id);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondToken.Secret);
        File.Delete(TaskPaths.TimelineLog(cardFolder));
        using (var secondReplay = await client.PostAsJsonAsync(path, summary with { Cards = ["AGT-3166"] }))
        {
            secondReplay.EnsureSuccessStatusCode();
            using var secondBody = JsonDocument.Parse(await secondReplay.Content.ReadAsStringAsync());
            Assert.Equal("Already recorded.", secondBody.RootElement.GetProperty("results")[0]
                .GetProperty("reason").GetString());
        }
        using var changed = await client.PostAsJsonAsync(path, summary with
        {
            Cards = ["AGT-3166"], VerificationHost = "other-host",
        });
        changed.EnsureSuccessStatusCode();
        using var conflict = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        Assert.False(conflict.RootElement.GetProperty("results")[0].GetProperty("recorded").GetBoolean());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);

        using var task = JsonDocument.Parse(File.ReadAllText(Path.Combine(cardFolder, "task.json")));
        var record = Assert.Single(task.RootElement.GetProperty("integrationRecords").EnumerateArray());
        Assert.Equal(candidate, record.GetProperty("integrationSha").GetString());
        Assert.Equal(delivery, record.GetProperty("sourceSha").GetString());
        Assert.Equal("gate-host", record.GetProperty("verificationHost").GetString());
        Assert.Equal(originalActor, record.GetProperty("actor").GetString());
        Assert.False(factory.Services.GetRequiredService<TaskMutationService>()
            .RemoveIntegrationRecordOnFolder(cardFolder, record.GetProperty("id").GetString()!));
        var timeline = factory.Services.GetRequiredService<TimelineLog>().ReadAll(cardFolder);
        Assert.Single(timeline, item => item.Details?.GetValueOrDefault("recordId") == record.GetProperty("id").GetString());
        Assert.Equal(originalActor, Assert.Single(timeline,
            item => item.Kind == TimelineEventKinds.IntegrationVerificationRecorded).Actor);
        File.Delete(Path.Combine(cardFolder, "logs", "timeline.jsonl"));
        var auditRepair = factory.Services.GetRequiredService<OperatorCandidateAuditRepair>();
        await auditRepair.StartAsync(CancellationToken.None);
        auditRepair.RepairAll();
        Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(cardFolder),
            item => item.Kind == TimelineEventKinds.IntegrationVerificationRecorded);

        var titleUpdates = Task.Run(() =>
        {
            var mutations = factory.Services.GetRequiredService<TaskMutationService>();
            for (var index = 0; index < 20; index++)
                Assert.True(mutations.SetJobTitle("AGT-3190", $"title-{index}", watchPath));
        });
        var concurrent = Enumerable.Range(0, 12).Select(index => client.PostAsJsonAsync(path,
            summary with { Cards = ["AGT-3190"], VerificationHost = index % 2 == 0 ? "gate-host" : "other-host" }));
        var concurrentResponses = await Task.WhenAll(concurrent);
        await titleUpdates;
        foreach (var response in concurrentResponses) response.EnsureSuccessStatusCode();
        var concurrentResults = await Task.WhenAll(concurrentResponses.Select(async response =>
        {
            using (response)
            using (var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                return (Recorded: payload.RootElement.GetProperty("results")[0].GetProperty("recorded").GetBoolean(),
                    Reason: payload.RootElement.GetProperty("results")[0].GetProperty("reason").GetString());
        }));
        Assert.Contains(concurrentResults, item => item.Reason == "Recorded.");
        Assert.Contains(concurrentResults, item => item.Reason == "Already recorded.");
        Assert.Contains(concurrentResults, item => !item.Recorded && item.Reason!.Contains("different evidence"));
        var concurrentFolder = Path.Combine(watchPath, "5-human-review", "AGT-3190");
        using (var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(concurrentFolder, "task.json"))))
        {
            Assert.Single(persisted.RootElement.GetProperty("integrationRecords").EnumerateArray());
            Assert.Equal("title-19", persisted.RootElement.GetProperty("title").GetString());
        }
        Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(concurrentFolder),
            item => item.Kind == TimelineEventKinds.IntegrationVerificationRecorded);

        File.Delete(Path.Combine(concurrentFolder, "logs", "timeline.jsonl"));
        scanner.InvalidateCache();
        Assert.Equal(concurrentFolder, scanner.FindJob("AGT-3190", watchPath)?.FolderPath);
        Directory.CreateDirectory(Path.Combine(watchPath, TaskStates.Escalated));
        var laneMove = Task.Run(() => factory.Services.GetRequiredService<TaskStateMachine>()
            .MoveJob("AGT-3190", TaskStates.Escalated, watchPath));
        var concurrentRepair = Task.Run(() => auditRepair.RepairAll());
        Assert.Equal(MoveJobStatus.Success, (await laneMove).Status);
        await concurrentRepair;
        auditRepair.RepairAll();
        var movedFolder = scanner.FindJob("AGT-3190", watchPath)!.FolderPath;
        Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(movedFolder),
            item => item.Kind == TimelineEventKinds.IntegrationVerificationRecorded);
        Assert.False(Directory.Exists(concurrentFolder));

        var archiveFolder = Path.Combine(watchPath, TaskStates.Archive, "AGT-3166");
        Directory.CreateDirectory(archiveFolder);
        File.Copy(Path.Combine(cardFolder, "task.json"), Path.Combine(archiveFolder, "task.json"));
        TaskJsonFile.UpdateFieldOrThrow(archiveFolder, "state", TaskStates.Archive);
        scanner.InvalidateCache();
        auditRepair.RepairAll();
        Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(archiveFolder),
            item => item.Kind == TimelineEventKinds.IntegrationVerificationRecorded);
        Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(cardFolder),
            item => item.Kind == TimelineEventKinds.IntegrationVerificationRecorded);

        var archiveTimeline = TaskPaths.TimelineLog(archiveFolder);
        using (var writer = new StreamWriter(archiveTimeline, append: true))
            for (var index = 0; index < 2300; index++) writer.WriteLine(new string('x', 8192));
        var originalLength = new FileInfo(archiveTimeline).Length;
        Assert.True(originalLength > BoundedFileRead.LedgerBytes);
        Assert.DoesNotContain(factory.Services.GetRequiredService<TimelineLog>().ReadAll(archiveFolder),
            item => item.Kind == TimelineEventKinds.IntegrationVerificationRecorded);
        auditRepair.RepairAll();
        auditRepair.RepairAll();
        Assert.Equal(originalLength, new FileInfo(archiveTimeline).Length);

        ReviewSubjectStore.Write(cardFolder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-3166", RunAttemptId = "attempt-2", AttemptChainId = "chain-2",
            ResultSha = missing, Project = "candidate-test", Repository = "repository",
        });
        var nextDelivery = scanner.FindJob("AGT-3166", watchPath)!;
        Assert.NotEqual(IntegrationStatuses.Integrated,
            factory.Services.GetRequiredService<TaskIntegrationStatusService>()
                .BuildLookup([nextDelivery])[nextDelivery.TaskKey].Status);

        using var unpublished = await client.PostAsJsonAsync(path, summary with
        {
            CandidateSha = missing, Cards = ["AGT-3185"],
        });
        Assert.Equal(HttpStatusCode.Conflict, unpublished.StatusCode);

        Git(repository, "checkout", "-q", "-b", "rewritten", ancestor);
        File.WriteAllText(Path.Combine(repository, "replacement.txt"), "replacement\n");
        Git(repository, "add", ".");
        Git(repository, "commit", "-q", "-m", "rewrite publication");
        Git(repository, "push", "-q", "--force", "origin", "rewritten:develop");
        Git(repository, "update-ref", "refs/remotes/origin/develop", candidate);
        // origin/develop still names the old candidate until the route fetches.
        Assert.Equal(candidate, Git(repository, "rev-parse", "refs/remotes/origin/develop"));
        using var stale = await client.PostAsJsonAsync(path, summary with { Cards = ["AGT-3166"] });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.NotEqual(candidate, Git(repository, "rev-parse", "refs/remotes/origin/develop"));

        Git(repository, "remote", "set-url", "origin", Path.Combine(_root, "unavailable.git"));
        using var failedFetch = await client.PostAsJsonAsync(path, summary with { Cards = ["AGT-3171"] });
        Assert.Equal(HttpStatusCode.Conflict, failedFetch.StatusCode);
        Assert.Contains("could not be refreshed", await failedFetch.Content.ReadAsStringAsync());
    }

    private static void WriteCard(string watchPath, string key, string sha)
    {
        var folder = Path.Combine(watchPath, "5-human-review", key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, title = key, state = "5-human-review", order = 1,
            agent = "codex", cliType = "codex", taskType = "feature",
            commits = new[] { new { sha, shortSha = sha[..7], message = "delivery", runAttemptId = "attempt-1",
                resultSha = sha, deliveryGeneration = 1, filesChanged = 1, files = new[] { "delivered.txt" } } },
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
