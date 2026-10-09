using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteBuildTestGateRunnerTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(120, 198L * 1024 * 1024, 198)]
    [InlineData(600, 198L * 1024 * 1024, 600)]
    [InlineData(120, 2L * 1024 * 1024 * 1024, 2048)]
    [InlineData(120, 8L * 1024 * 1024 * 1024, 3600)]
    [InlineData(7200, 198L * 1024 * 1024, 7200)]
    [InlineData(7200, 8L * 1024 * 1024 * 1024, 7200)]
    public void Bundle_transfer_budget_scales_with_bytes_and_has_a_ceiling(
        int configuredSeconds, long bytes, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            RemoteGateTransport.TransferBudget(TimeSpan.FromSeconds(configuredSeconds), bytes));
    }

    [Fact]
    public void Transfer_failure_evidence_names_step_size_and_elapsed_time()
    {
        var evidence = RemoteGateTransport.TransferFailure(198_000_000,
            TimeSpan.FromSeconds(201.5), TimeSpan.FromSeconds(240), "deadline");
        Assert.Contains("source.bundle transfer", evidence);
        Assert.Contains("198000000 bytes", evidence);
        Assert.Contains("201.5s", evidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("HEAD")]
    [InlineData("not-a-commit")]
    public async Task Missing_exact_subject_fails_without_dispatch(string? sha)
    {
        var transport = new RecordingTransport();
        var result = await Runner(transport).RunAsync(Request() with { ExpectedSha = sha },
            [], null, PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(BuildTestGateFailureKind.MissingSource, result.FailureKind);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Nonexact_gate_cannot_enable_local_execution()
    {
        var transport = new RecordingTransport();
        var result = await Runner(transport).RunAsync(Request() with { RequireExactSubject = false },
            [], null, PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Remote_result_preserves_full_gate_evidence_and_scope()
    {
        var profile = new BuildProfile { BuildCmds = ["dotnet build"], TestCmds = ["dotnet test"] };
        var transport = new RecordingTransport
        {
            Result = Passed() with { Output = "full-suite evidence", ToolchainIdentity = "remote-dotnet" },
        };
        var request = Request() with { RequiredTestLevel = "full", GateId = PipelineCatalogue.BuildTestGateStepId };
        var result = await Runner(transport).RunAsync(request, ["backend/example.cs"], profile,
            PostStepMode.Fail, TimeSpan.FromMinutes(90), CancellationToken.None);
        Assert.Same(profile, transport.Profile);
        Assert.Same(request, transport.Request);
        Assert.Equal(["backend/example.cs"], transport.ChangedFiles);
        Assert.Equal(TimeSpan.FromMinutes(90), transport.Timeout);
        Assert.Equal("full-suite evidence", result.Output);
        Assert.Equal("remote-dotnet", result.ToolchainIdentity);
        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
    }

    [Theory]
    [InlineData("tested")]
    [InlineData("expected")]
    [InlineData("gate")]
    public async Task Unbound_remote_evidence_fails_closed(string mismatch)
    {
        var result = Passed();
        result = mismatch switch
        {
            "tested" => result with { TestedSha = new string('b', 40) },
            "expected" => result with { ExpectedSha = new string('b', 40) },
            _ => result with { GateId = "another-gate" },
        };
        var actual = await Runner(new RecordingTransport { Result = result }).RunAsync(Request(),
            [], null, PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(BuildTestGateVerdict.Fail, actual.Verdict);
        Assert.Equal(BuildTestGateFailureKind.MissingSource, actual.FailureKind);
    }

    [Fact]
    public async Task Missing_remote_configuration_is_infrastructure_failure_without_fallback()
    {
        var configuration = new ConfigurationBuilder().Build();
        var transport = new RemoteGateTransport(configuration, NullLogger<RemoteGateTransport>.Instance);
        var result = await Runner(transport).RunAsync(Request(), [], null, PostStepMode.Fail,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, result.FailureKind);
        Assert.Contains("local execution is disabled", result.Reason);
    }

    [Theory]
    [InlineData("network", BuildTestGateFailureKind.Environment)]
    [InlineData("timeout", BuildTestGateFailureKind.Environment)]
    [InlineData("cancel", BuildTestGateFailureKind.Cancellation)]
    public async Task Transport_failure_is_typed_and_never_replaced_by_a_local_verdict(
        string failure, BuildTestGateFailureKind expected)
    {
        Exception exception = failure switch
        {
            "timeout" => new TimeoutException("remote deadline"),
            "cancel" => new OperationCanceledException(),
            _ => new IOException("SSH unavailable"),
        };
        var transport = new RecordingTransport { Exception = exception };
        var result = await Runner(transport).RunAsync(Request(), [], null, PostStepMode.Fail,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(failure == "cancel" ? 1 : RemoteBuildTestGateRunner.MaxTransportAttempts, transport.Calls);
        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(expected, result.FailureKind);
    }

    [Theory]
    [InlineData("ECONNRESET")]
    [InlineData("ETIMEDOUT")]
    [InlineData("unable to access")]
    public async Task Transport_marker_retries_same_gate_subject_until_success(string marker)
    {
        var request = Request();
        var transport = new RecordingTransport
        {
            Result = Passed(),
            TransientResult = RemoteBuildTestGateRunner.Failure(request,
                BuildTestGateFailureKind.Environment, marker),
        };

        var result = await Runner(transport).RunAsync(request, [], null, PostStepMode.Fail,
            TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(2, transport.Calls);
        Assert.All(transport.Requests, actual => Assert.Same(request, actual));
        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(Sha, result.TestedSha);
    }

    [Fact]
    public async Task Persistent_transport_marker_stops_at_budget_without_accepting_delivery()
    {
        var transport = new RecordingTransport
        {
            Result = RemoteBuildTestGateRunner.Failure(Request(),
                BuildTestGateFailureKind.Environment, "ECONNRESET"),
        };
        var result = await Runner(transport).RunAsync(Request(), [], null, PostStepMode.Fail,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(RemoteBuildTestGateRunner.MaxTransportAttempts, transport.Calls);
        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
    }

    [Fact]
    public async Task Product_failure_with_transport_text_does_not_retry()
    {
        var transport = new RecordingTransport
        {
            Result = RemoteBuildTestGateRunner.Failure(Request(),
                BuildTestGateFailureKind.Code, "ECONNRESET") with
            { Output = "Failed Product.Tests.TransportRule [1 ms]" },
        };
        var result = await Runner(transport).RunAsync(Request(), [], null, PostStepMode.Fail,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
    }

    [Fact]
    public async Task Off_gate_does_not_dispatch()
    {
        var transport = new RecordingTransport();
        var result = await Runner(transport).RunAsync(Request(), [], null, PostStepMode.Off,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(BuildTestGateVerdict.Skipped, result.Verdict);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("localhost", "/srv/worker.dll")]
    [InlineData("-oProxyCommand=anything", "/srv/worker.dll")]
    [InlineData("agent-runner", "/srv/../worker.dll")]
    [InlineData("agent-runner", "relative/worker.dll")]
    public void Unsafe_transport_configuration_is_rejected(string host, string worker)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:SshHost"] = host,
            ["RemoteGate:WorkerPath"] = worker,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => RemoteGateOptions.Read(configuration));
    }

    [Fact]
    public void Invocation_roundtrip_preserves_verification_inputs_without_serializing_callbacks()
    {
        var request = Request() with { OnMachineGateAcquired = () => { }, OnMachineGateWaiting = () => { } };
        var invocation = Invocation(request);
        var json = JsonSerializer.Serialize(invocation, RemoteBuildTestGateRunner.Json);
        var roundtrip = JsonSerializer.Deserialize<RemoteGateInvocation>(json, RemoteBuildTestGateRunner.Json)!;
        RemoteGateWorker.Validate(roundtrip, invocation.RunId);
        Assert.Null(roundtrip.Request.OnMachineGateAcquired);
        Assert.Null(roundtrip.Request.OnMachineGateWaiting);
        Assert.Equal(Sha, roundtrip.Request.ExpectedSha);
        Assert.Equal(invocation.TimeoutSeconds, roundtrip.TimeoutSeconds);
    }

    [Fact]
    public void Worker_rejects_mismatched_directory_identity_and_unbounded_budget()
    {
        var invocation = Invocation(Request());
        Assert.Throws<InvalidDataException>(() => RemoteGateWorker.Validate(invocation, new string('b', 32)));
        Assert.Throws<InvalidDataException>(() => RemoteGateWorker.Validate(
            invocation with { OverallTimeoutSeconds = 86401 }, invocation.RunId));
    }

    private static BuildTestGateRequest Request() => new("/operator/repository", Sha, "integration-gate");
    private static RemoteGateInvocation Invocation(BuildTestGateRequest request) => new(
        1, new string('a', 32), new string('b', 64), request, [], null, PostStepMode.Fail, 60, 180);
    private static BuildTestGateResult Passed() => new(BuildTestGateVerdict.Ok, 0, 1, "", "passed", true, false)
    {
        ExpectedSha = Sha, TestedSha = Sha, GateId = PipelineCatalogue.BuildTestGateStepId,
    };
    private static RemoteBuildTestGateRunner Runner(IRemoteGateTransport transport)
        => new(transport, NullLogger<RemoteBuildTestGateRunner>.Instance);

    private sealed class RecordingTransport : IRemoteGateTransport
    {
        public int Calls { get; private set; }
        public List<BuildTestGateRequest> Requests { get; } = [];
        public BuildTestGateResult? TransientResult { get; init; }
        public BuildTestGateRequest? Request { get; private set; }
        public IReadOnlyList<string>? ChangedFiles { get; private set; }
        public BuildProfile? Profile { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public BuildTestGateResult Result { get; init; } = Passed();
        public Exception? Exception { get; init; }
        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
            TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request);
            if (Calls == 1 && TransientResult is not null) return Task.FromResult(TransientResult);
            Request = request;
            ChangedFiles = changedFiles;
            Profile = profile;
            Timeout = timeout;
            return Exception is null ? Task.FromResult(Result) : Task.FromException<BuildTestGateResult>(Exception);
        }
    }
}

public sealed class RemoteGateBundleTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    public void Incremental_bundle_requires_cache_tip_and_restores_exact_subject()
    {
        var root = Path.Combine(Path.GetTempPath(), "remote-gate-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var cache = Path.Combine(root, "cache.git");
            var restored = Path.Combine(root, "restored.git");
            var empty = Path.Combine(root, "empty.git");
            var initial = Path.Combine(root, "initial.bundle");
            var incremental = Path.Combine(root, "incremental.bundle");
            Git(root, "init", source);
            Git(source, "config", "user.email", "gate@example.invalid");
            Git(source, "config", "user.name", "Gate Test");
            File.WriteAllText(Path.Combine(source, "base.txt"), new string('a', 8192));
            Git(source, "add", ".");
            Git(source, "commit", "-qm", "base");
            var baseSha = Git(source, "rev-parse", "HEAD").Trim();
            Git(source, "branch", "gate-subject");
            Git(source, "bundle", "create", initial, "refs/heads/gate-subject");
            Git(root, "clone", "--bare", initial, cache);

            File.WriteAllText(Path.Combine(source, "change.txt"), "gated merge candidate");
            Git(source, "add", ".");
            Git(source, "commit", "-qm", "candidate");
            var expectedSha = Git(source, "rev-parse", "HEAD").Trim();
            Git(source, "branch", "-f", "gate-subject", expectedSha);
            var args = RemoteGateTransport.BundleArguments(source, incremental, [baseSha]);
            Git(root, args.ToArray());
            Git(root, "init", "--bare", empty);
            Assert.Throws<InvalidOperationException>(() => Git(empty, "bundle", "verify", incremental));
            Git(cache, "bundle", "verify", incremental);
            Git(root, "clone", "--bare", cache, restored);
            Git(restored, "fetch", incremental, "refs/heads/gate-subject:refs/heads/gate-subject");
            Assert.Equal(expectedSha, Git(restored, "rev-parse", "refs/heads/gate-subject").Trim());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Git(string cwd, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }
}
