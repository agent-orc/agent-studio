using System.Diagnostics;
using System.Net;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BuildTestGateDiagnosisWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "gate-diagnosis-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(_root, "repo");
    private string CacheRoot => Path.Combine(_root, "cache");
    private string VerifyLog => Path.Combine(_root, "verify-runs.txt");

    public BuildTestGateDiagnosisWorkflowTests()
    {
        Directory.CreateDirectory(Repository);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Failed_gate_runs_baseline_and_clean_repeat_reads_history_and_evicts_cache()
    {
        if (OperatingSystem.IsWindows()) return;
        Write("packages.lock.json", "{\"version\":1,\"dependencies\":{}}");
        Write(".agent-studio/project.yml", """
            schemaVersion: 1
            stack: [dotnet]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - sh .agent-studio/verify-build
              test:
              lint:
            testSuites:
            cachePaths: [bin]
            capabilities: [linux]
            environment:
              CI: "true"
            """);
        Write(".agent-studio/prepare", """
            #!/bin/sh
            set -eu
            mkdir -p "$NUGET_PACKAGES/example.package/1.0.0"
            printf nupkg > "$NUGET_PACKAGES/example.package/1.0.0/example.package.1.0.0.nupkg"
            printf metadata > "$NUGET_PACKAGES/example.package/1.0.0/.nupkg.metadata"
            """);
        Write(".agent-studio/verify-build", "#!/bin/sh\n" +
            $"printf '%s\\n' \"$NUGET_PACKAGES\" >> '{VerifyLog}'\n" +
            "echo 'persistent failure' >&2\nexit 1\n");
        Git("init");
        Git("config", "user.email", "gate-diagnosis@example.invalid");
        Git("config", "user.name", "Gate Diagnosis Test");
        Git("add", ".");
        Git("commit", "-m", "baseline");
        Git("branch", "integration-baseline");
        Write("changed.cs", "public class Changed {}\n");
        Git("add", ".");
        Git("commit", "-m", "candidate");
        var candidate = Git("rev-parse", "HEAD").Trim();
        var reporter = new FingerprintReporter();
        var runner = new BuildTestGateRunner(NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest, CacheRoot, reporter);

        var result = await runner.RunAsync(new BuildTestGateRequest(
            Repository, candidate, "test-executor")
        {
            JobId = "AGT-gate-diagnosis",
            IntegrationRef = "integration-baseline",
        }, null, null, PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal("environment", result.Diagnosis?.Classification);
        Assert.False(result.Diagnosis!.ChargesCard);
        Assert.Contains("baseline=red", result.Diagnosis.Evidence[1]);
        Assert.Contains("clean-repeat=red", result.Diagnosis.Evidence[2]);
        Assert.Contains(result.Diagnosis.Evidence,
            evidence => evidence.Contains("restored-dependency-cache=false", StringComparison.Ordinal));
        var cacheBindings = File.ReadAllLines(VerifyLog);
        Assert.Equal(3, cacheBindings.Length);
        Assert.Equal(3, cacheBindings.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, cacheBindings.Count(path => path.Contains(
            Path.Combine(CacheRoot, "diagnostics"), StringComparison.Ordinal)));
        Assert.Equal(1, reporter.Reads);
        Assert.Equal(1, reporter.Writes);
        Assert.NotEmpty(result.PreparationManifest!.Caches);
        Assert.All(result.PreparationManifest!.Caches,
            entry => Assert.False(Directory.Exists(entry.EntryPath)));
    }

    [Fact]
    public async Task Aow15_PreMainGate_InvalidDefinitionInDelivery_IsACodeFailureListingTheIssues()
    {
        if (OperatingSystem.IsWindows()) return;
        Write("seed.txt", "seed\n");
        Git("init");
        Git("config", "user.email", "gate-diagnosis@example.invalid");
        Git("config", "user.name", "Gate Diagnosis Test");
        Git("add", ".");
        Git("commit", "-m", "baseline");
        Git("branch", "integration-baseline");
        // The AOW-15 delivery: a project.yml without commands.prepare.
        Write(".agent-studio/project.yml", """
            schemaVersion: 1
            stack: [shell]
            commands:
              build: []
              test: []
              lint: []
            """);
        Git("add", ".");
        Git("commit", "-m", "candidate");
        var candidate = Git("rev-parse", "HEAD").Trim();
        var reporter = new FingerprintReporter();
        var gate = new PreMainTestGate(new BuildTestGateRunner(NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest, CacheRoot, reporter));

        var result = await gate.RunAsync(new BuildTestGateRequest(Repository, candidate, "merge-into-main")
        {
            JobId = "AOW-15",
            IntegrationRef = "integration-baseline",
        }, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.Null(result.Diagnosis);
        Assert.Equal(0, reporter.Reads);
        Assert.Contains(result.ProjectDefinitionIssues, issue =>
            issue is { Path: "commands.prepare", Code: "prepare-path-invalid" });
        Assert.Contains("commands.prepare", result.Reason);
        Assert.Contains("[prepare-path-invalid] Prepare must be a repository-relative path", result.Reason);
        Assert.DoesNotContain("diagnosis=", result.Reason);

        // The merge runner records the gate, and the router classifies that log.
        var job = Path.Combine(_root, "job");
        IntegrationGateReceipts.Record(job, "pre-main-test-gate", result);
        var triage = GateFailureTriagePolicy.Classify(
            File.ReadAllText(Directory.GetFiles(Path.Combine(job, "post-steps"), "pre-main-test-gate-*.log").Single()));
        Assert.Equal(GateFailureClasses.Product, triage.Class);
        Assert.Equal(GateProductKinds.Definition, triage.Kind);
        Assert.Contains("commands.prepare [prepare-path-invalid]", triage.FailingItems);
        var route = GateFailureRoutingPolicy.Decide(triage, new GateFailureRoutingFacts(false, null, 0, true));
        Assert.Equal(GateFailureRouteAction.FixRound, route.Action);
        Assert.DoesNotContain("repair the gate host",
            GateFailureRoutingPolicy.ParkReason(triage, route with { Action = GateFailureRouteAction.ParkExhausted }));
    }

    private void Write(string relative, string value)
    {
        var path = Path.Combine(Repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private sealed class FingerprintReporter : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public int Reads;
        public int Writes;

        public FingerprintReporter()
        {
            _client = new HttpClient(new Handler(this))
            {
                BaseAddress = new Uri("http://localhost"),
            };
        }

        public HttpClient CreateClient(string name) => _client;

        private sealed class Handler(FingerprintReporter owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get) owner.Reads++;
                if (request.Method == HttpMethod.Post) owner.Writes++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.Method == HttpMethod.Get ? "[]" : "{}"),
                });
            }
        }
    }
}
