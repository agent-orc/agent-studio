using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

// clock-independent: CreatedAt values only order attempts in the claim answer.

/// <summary>
/// AGT-3077: private repository access is a per-repository review capability.
/// These tests pin its key form, the claim-side refusal for both Task Server
/// implementations, and the executor profile file that grants it.
/// </summary>
public sealed class ReviewRepositoryAccessPolicyTests : IDisposable
{
    private const string Website = "https://github.com/RobertMischke/agent-studio-for-software-website.git";
    private const string WebsiteKey =
        "review:repository-read:github.com/robertmischke/agent-studio-for-software-website";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "review-repository-access-" + Guid.NewGuid().ToString("N"));
    // Built at runtime: no credential-shaped literal in source.
    private readonly string _token = "fixture-" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData(Website)]
    [InlineData("https://github.com/robertmischke/agent-studio-for-software-website")]
    [InlineData("https://github.com/RobertMischke/agent-studio-for-software-website/")]
    [InlineData("git@github.com:RobertMischke/agent-studio-for-software-website.git")]
    [InlineData("ssh://git@github.com/RobertMischke/agent-studio-for-software-website.git")]
    public void Every_remote_spelling_of_one_repository_requires_the_same_key(string url)
        => Assert.Equal(WebsiteKey, ReviewRepositoryAccessPolicy.ReadCapability(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/srv/git/origin.git")]
    [InlineData("C:\\repos\\origin.git")]
    public void A_local_path_has_no_repository_read_key(string? url)
        => Assert.Null(ReviewRepositoryAccessPolicy.ReadCapability(url));

    public static TheoryData<bool, bool, bool> ClaimMatrix => new()
    {
        // privateRepository, executorHoldsKey, claimable
        { false, false, true },
        { false, true, true },
        { true, false, false },
        { true, true, true },
    };

    [Theory]
    [MemberData(nameof(ClaimMatrix))]
    public void Claim_filter_matrix(bool privateRepository, bool executorHoldsKey, bool claimable)
    {
        var plan = Plan(privateRepository);
        var capabilities = new HashSet<string>(StringComparer.Ordinal) { ReviewCapabilities.GitMaterialization };
        if (executorHoldsKey) capabilities.Add(WebsiteKey);

        Assert.Equal(claimable, ReviewRepositoryAccessPolicy.Supports(plan, Website, capabilities));
        Assert.Equal(claimable ? [] : [WebsiteKey],
            ReviewRepositoryAccessPolicy.MissingCapabilities(plan, Website, capabilities));
    }

    [Fact]
    public void A_private_plan_whose_repository_is_not_yet_resolved_is_judged_after_resolution()
        => Assert.Null(ReviewRepositoryAccessPolicy.RequiredCapability(Plan(privateRepository: true), null));

    [Fact]
    public void Unclaimable_answer_names_the_repository_and_the_credential_setting()
    {
        var response = ReviewClaimEmptyResponses.ForQueue(
            [new ReviewUnclaimableAttemptDto("review-1", "WEB-28", DateTime.UnixEpoch, [WebsiteKey])],
            ReviewClaimEmptyReasons.QueueEmpty,
            "unused");

        Assert.Equal(ReviewClaimEmptyReasons.UnclaimablePlanRequirements, response.Reason);
        Assert.Equal([WebsiteKey], response.MissingCapabilities);
        Assert.Contains("private repository 'github.com/robertmischke/agent-studio-for-software-website'",
            response.Message, StringComparison.Ordinal);
        Assert.Contains(ReviewRepositoryCredentials.FileVariable, response.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public", "public")]
    [InlineData(" Private ", "private")]
    [InlineData("internal", null)]
    [InlineData(null, null)]
    public void Visibility_normalizes_to_public_or_private(string? value, string? expected)
        => Assert.Equal(expected, ReviewRepositoryAccessPolicy.NormalizeVisibility(value));

    [Fact]
    public void Credentials_file_grants_one_key_per_repository_and_never_prints_the_secret()
    {
        var credentials = ReviewRepositoryCredentials.Parse(
        [
            "# website review credential",
            $"https://x-access-token:{_token}@github.com/RobertMischke/agent-studio-for-software-website.git",
            "",
            $"https://reader:{_token}@github.com/agent-orc/website",
        ]);

        Assert.Equal(
            [WebsiteKey, "review:repository-read:github.com/agent-orc/website"],
            credentials.CapabilityKeys);
        var website = credentials.Find("git@github.com:robertmischke/agent-studio-for-software-website.git");
        Assert.NotNull(website);
        Assert.Equal("x-access-token", website.Username);
        Assert.Equal(
            "https://github.com/RobertMischke/agent-studio-for-software-website.git", website.CloneUrl);
        Assert.Equal("robertmischke/agent-studio-for-software-website", website.RepositoryPath);
        Assert.DoesNotContain(_token, website.ToString(), StringComparison.Ordinal);
        Assert.Null(credentials.Find("https://github.com/agent-orc/agent-studio.git"));
    }

    [Theory]
    [InlineData("https://{0}@github.com/owner/repo")]          // no user name
    [InlineData("https://reader:{0}@github.com/")]              // host-wide, not repository-scoped
    [InlineData("https://reader:{0}@github.com/owner")]         // owner-wide, not repository-scoped
    [InlineData("http://reader:{0}@github.com/owner/repo")]     // clear text to a remote host
    [InlineData("ssh://reader:{0}@github.com/owner/repo")]      // not an https credential
    [InlineData("https://github.com/owner/repo")]               // no credential at all
    public void Credentials_file_rejects_entries_that_are_not_repository_scoped_https(string pattern)
    {
        var failure = Assert.Throws<ArgumentException>(
            () => ReviewRepositoryCredentials.Parse([string.Format(pattern, _token)]));

        Assert.Contains(ReviewRepositoryCredentials.FileVariable, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_token, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_file_readable_by_group_or_others_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = WriteCredentials(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var failure = Assert.Throws<ArgumentException>(() => ReviewRepositoryCredentials.Load(path));

        Assert.Contains("chmod 600", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_names_the_configured_key_and_the_advertisement_its_read_proof()
    {
        var path = WriteCredentials(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var unproven = new ReviewRepositoryReadProbe(clock: () => DateTimeOffset.UnixEpoch, prove: Answer(0));
        var proven = new ReviewRepositoryReadProbe(clock: () => DateTimeOffset.UnixEpoch, prove: Answer(0));
        var revoked = new ReviewRepositoryReadProbe(clock: () => DateTimeOffset.UnixEpoch, prove: Answer(128));
        await proven.RefreshDueAsync(path, _ => { }, default);
        await revoked.RefreshDueAsync(path, _ => { }, default);

        var registered = RunnerCapabilityProbe.ReviewRegistrationCapabilities(
            Options("review", path), onPath: _ => false, composeRenderVersion: () => null);
        var withoutFile = RunnerCapabilityProbe.ReviewRegistrationCapabilities(
            Options("review", null), onPath: _ => false, composeRenderVersion: () => null);

        Assert.Contains(WebsiteKey, registered);
        Assert.DoesNotContain(withoutFile, key =>
            key.StartsWith(ReviewRepositoryAccessPolicy.ReadCapabilityPrefix, StringComparison.Ordinal));
        Assert.Empty(RunnerCapabilityProbe.ReviewRepositoryReadCapabilities(Options("coding", path)));
        // A line in the file is not a working token: no proof, no advertisement.
        Assert.DoesNotContain(Advertised(path, unproven), capability => capability.Key == WebsiteKey);
        Assert.Contains(Advertised(path, proven), capability =>
            capability.Key == WebsiteKey && capability.Status == ReviewRepositoryReadProbe.Ready);
        Assert.Contains(Advertised(path, revoked), capability =>
            capability.Key == WebsiteKey && capability.Status == ReviewRepositoryReadProbe.Unavailable
            && capability.Detail!.Contains("Authentication failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_proof_is_repeated_only_when_due_or_after_a_failed_private_clone()
    {
        var path = WriteCredentials(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var now = DateTimeOffset.UnixEpoch;
        var proofs = 0;
        var exitCode = 0;
        var probe = new ReviewRepositoryReadProbe(clock: () => now, prove: (_, _) =>
        {
            proofs++;
            return Task.FromResult(new ProcessResult(exitCode, string.Empty, "fatal: Authentication failed"));
        });

        await probe.RefreshDueAsync(path, _ => { }, default);
        now += ReviewRepositoryReadProbe.ReadyTtl - TimeSpan.FromSeconds(1);
        await probe.RefreshDueAsync(path, _ => { }, default);
        Assert.Equal(1, proofs);

        exitCode = 128;
        probe.Invalidate(WebsiteKey);
        await probe.RefreshDueAsync(path, _ => { }, default);
        Assert.Equal(2, proofs);
        Assert.Equal(ReviewRepositoryReadProbe.Unavailable, probe.Proofs[WebsiteKey].Status);

        // A failed proof is retried soon, so an approved token needs no restart.
        exitCode = 0;
        now += ReviewRepositoryReadProbe.UnavailableTtl;
        await probe.RefreshDueAsync(path, _ => { }, default);
        Assert.Equal(3, proofs);
        Assert.Equal(ReviewRepositoryReadProbe.Ready, probe.Proofs[WebsiteKey].Status);
    }

    [Fact]
    public async Task Read_proof_that_times_out_or_loses_its_file_is_unavailable()
    {
        var path = WriteCredentials(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var probe = new ReviewRepositoryReadProbe(clock: () => DateTimeOffset.UnixEpoch, prove: Answer(0));
        var hanging = new ReviewRepositoryReadProbe(
            clock: () => DateTimeOffset.UnixEpoch,
            prove: async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ProcessResult(0, string.Empty, string.Empty);
            },
            timeout: TimeSpan.FromMilliseconds(50));
        var log = new List<string>();

        await probe.RefreshDueAsync(path, _ => { }, default);
        await hanging.RefreshDueAsync(path, _ => { }, default);
        File.WriteAllText(path, "not a credential line\n");
        probe.Invalidate(WebsiteKey);
        await probe.RefreshDueAsync(path, log.Add, default);

        Assert.Equal(ReviewRepositoryReadProbe.Unavailable, hanging.Proofs[WebsiteKey].Status);
        Assert.Contains("timed out", hanging.Proofs[WebsiteKey].Detail, StringComparison.Ordinal);
        Assert.Equal(ReviewRepositoryReadProbe.Unavailable, probe.Proofs[WebsiteKey].Status);
        Assert.Contains("unreadable", probe.Proofs[WebsiteKey].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(log, line => line.Contains(_token, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SnapshotUnavailable", true, null, WebsiteKey)]
    [InlineData("SnapshotUnavailable", false, CapabilityProtocol.RepositoryAccess, null)]
    [InlineData("ShaMismatch", true, CapabilityProtocol.RepositoryAccess, null)]
    [InlineData("DiskFull", true, CapabilityProtocol.Disk, null)]
    public void Failed_private_snapshot_reproves_its_read_key_instead_of_draining_repository_access(
        string classification, bool privateRepository, string? drained, string? reproved)
    {
        var subject = new ReviewSubjectDto(
            "subject", "task", "run", "repository", Website, new string('a', 40), null, null, null, null,
            "policy", Plan(privateRepository), DateTime.UnixEpoch);

        var failed = RemoteReviewExecutor.FailedCapability(classification, subject, out var readKey);

        Assert.Equal(drained, failed);
        Assert.Equal(reproved, readKey);
    }

    private static Func<ReviewRepositoryCredential, CancellationToken, Task<ProcessResult>> Answer(int exitCode)
        => (_, _) => Task.FromResult(new ProcessResult(
            exitCode, string.Empty, exitCode == 0 ? string.Empty : "fatal: Authentication failed"));

    private IReadOnlyList<AdvertisedCapabilityDto> Advertised(string path, ReviewRepositoryReadProbe probe)
        => RunnerCapabilityProbe.Advertise(Options("review", path), gitPushReady: false, repositoryRead: probe);

    [Fact]
    public void Detached_review_worker_receives_the_credentials_file_path()
    {
        var path = WriteCredentials(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var spec = new DetachedReviewSpec(
            null!, new ReviewLeaseDto("lease", "attempt", "subject", "executor", "instance", "host", 1,
                DateTime.UnixEpoch, DateTime.UnixEpoch, "active", "ns", 24000),
            _root, [], ReviewGitCredentialsFile: path);

        Assert.Equal(path, DurableReviewProcess.WorkerOptions(spec).ReviewGitCredentialsFile);
    }

    private string WriteCredentials(UnixFileMode mode)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "review-git-credentials");
        File.WriteAllText(path, $"https://x-access-token:{_token}@github.com/RobertMischke/agent-studio-for-software-website.git\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
        return path;
    }

    private static ReviewPlanDto Plan(bool privateRepository)
        => new([new ReviewCommandDto("verify", "build-tests", "git", ["status"])], ["build-tests"],
            PrivateRepository: privateRepository);

    private RunnerOptions Options(string role, string? credentialsFile) => new()
    {
        ServerUrl = "http://127.0.0.1:5031",
        RunnerId = "review-runner",
        RunnerName = "review-runner",
        Hostname = "review-host",
        BackendName = "test",
        Role = role,
        WorkDir = Path.Combine(_root, "coding"),
        ReviewWorkDir = Path.Combine(_root, "review"),
        ReviewGitCredentialsFile = credentialsFile,
        BaseBranch = "main",
        ClaudeCliBin = "sh",
    };

    public void Dispose() => ResilientDirectory.TryDelete(_root);
}
