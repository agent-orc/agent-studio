using AgentStudio.Readiness;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure policy input; every age is computed from the fixed Now below.
/// <summary>
/// Direct matrix of every server readiness line (AGT-3412): for each state, the
/// meaning and the exact remedy an operator reads on the operations page and in
/// the move tool's report.
/// </summary>
public sealed class ServerReadinessPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static ProjectReadinessFact Project(
        RepositoryRefsFact? refs = null, string? visibility = "private", string? branch = null,
        IReadOnlyList<GitLockFact>? locks = null)
        => new("Demo", "/data/projects/demo", visibility, branch,
            refs ?? Refs("refs/remotes/origin/main", "refs/agent-studio/integration/main"),
            new DataFolderFact("Demo", "/data/projects/demo", "projects/demo", true, "/data/projects/demo", "ext4"),
            locks ?? []);

    private static RepositoryRefsFact Refs(params string[] refs) => RefsWithHead("main", refs);

    private static RepositoryRefsFact RefsWithHead(string? originHead, params string[] refs)
        => new(true, true, new HashSet<string>(refs, StringComparer.Ordinal), originHead);

    private static ServerReadinessFacts Facts(params ProjectReadinessFact[] projects) => new(
        Now,
        new GitIdentityFact("Agent Studio", "file:/etc/gitconfig", "studio@agent-studio.invalid", "file:/etc/gitconfig"),
        new CommitProbeFact(true, true, "6185b47919fe87ca3fa640c2a822ad08a3989073"),
        true,
        [new DataFolderFact("workspace", "/data/workspace", "workspace", true, "/data/workspace", "ext4")],
        [new StateFolderFact("workspace", "/data/workspace", true, null)],
        [],
        new GateWorkerFact([new GateWorkerHostFact("kori-gate", 3, Now.AddSeconds(-30), true)], null, "unverified", null, null),
        new ExecutionHostsFact([new ExecutionHostFact("runner_1", "kori-review-1", Now.UtcDateTime.AddDays(-3), Now.UtcDateTime.AddMinutes(-1))], 0),
        new GitHubAppFact(true, "https://github.com/apps/studio/installations/new", null,
            new Dictionary<string, GitHubAppCoverageFact>(StringComparer.OrdinalIgnoreCase)
            {
                ["Demo"] = new(GitHubAppCoverageStates.Covered, "https://github.com/acme/demo", 42, null),
            }),
        projects);

    [Fact]
    public void A_ready_server_reports_every_required_check_as_passing_without_remedies()
    {
        var report = ServerReadinessPolicy.Evaluate(Facts(Project()));

        Assert.Equal(ReadinessStates.Pass, report.State);
        Assert.Equal(0, report.Failing);
        Assert.Equal(0, report.Attention);
        Assert.Equal(Now, report.ComputedAt);
        Assert.All(report.Checks, check => Assert.Null(check.Remedy));
        Assert.Equal(
            ["git-identity", "committer-identity", "data-volume", "state-writable", "git-locks", "gate-worker",
             "execution-host", "data-volume", "repository-visibility", "app-coverage", "integration-branch",
             "integration-lane", "git-locks"],
            report.Checks.Select(check => check.Id));
        Assert.All(report.Checks.Where(check => check.Scope == ReadinessScopes.Project),
            check => Assert.Equal("Demo", check.Subject));
    }

    [Fact]
    public void The_worst_line_decides_the_report_state_and_counts()
    {
        var report = ServerReadinessPolicy.Evaluate(Facts(Project(visibility: null, refs: Refs("refs/remotes/origin/main"))) with
        {
            CommitProbe = new CommitProbeFact(true, false, "Committer identity unknown"),
        });

        Assert.Equal(ReadinessStates.Fail, report.State);
        Assert.Equal(1, report.Failing);
        Assert.Equal(2, report.Attention);
    }

    [Fact]
    public void Git_identity_from_the_image_passes_and_names_the_image_default()
    {
        var check = ServerReadinessPolicy.GitIdentity(
            new GitIdentityFact("Agent Studio", "file:/etc/gitconfig", "studio@agent-studio.invalid", "file:/etc/gitconfig"));

        Assert.Equal(ReadinessStates.Pass, check.State);
        Assert.Equal("Commits are made as Agent Studio <studio@agent-studio.invalid>, from /etc/gitconfig (the image default).", check.Meaning);
        Assert.Null(check.Remedy);
    }

    [Fact]
    public void Git_identity_from_two_files_names_both()
    {
        var check = ServerReadinessPolicy.GitIdentity(
            new GitIdentityFact("Ops", "file:/data/home/.gitconfig", "ops@example.org", "file:/etc/gitconfig"));

        Assert.Equal(ReadinessStates.Pass, check.State);
        Assert.Contains("from /data/home/.gitconfig and /etc/gitconfig (the image default)", check.Meaning);
    }

    [Fact]
    public void Missing_git_identity_fails_and_gives_the_config_commands()
    {
        var check = ServerReadinessPolicy.GitIdentity(new GitIdentityFact(null, null, null, null));

        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.Contains("Git has no user.name and no user.email for the service user", check.Meaning);
        Assert.Contains("\"Committer identity unknown\"", check.Meaning);
        Assert.Equal(
            "In the stack directory run: docker compose exec api git config --global user.name \"Agent Studio\" "
            + "&& docker compose exec api git config --global user.email \"studio@agent-studio.invalid\"",
            check.Remedy);
    }

    [Fact]
    public void A_missing_email_alone_fails()
    {
        var check = ServerReadinessPolicy.GitIdentity(new GitIdentityFact("Agent Studio", "file:/etc/gitconfig", " ", null));

        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.StartsWith("Git has no user.email for the service user", check.Meaning);
    }

    [Fact]
    public void Git_that_does_not_answer_leaves_the_identity_unknown()
    {
        var check = ServerReadinessPolicy.GitIdentity(new GitIdentityFact(null, null, null, null, "git: not found"));

        Assert.Equal(ReadinessStates.Unknown, check.State);
        Assert.Contains("git: not found", check.Meaning);
        Assert.Equal("Check that Git runs in the API container: docker compose exec api git --version", check.Remedy);
    }

    [Fact]
    public void A_successful_commit_tree_probe_passes()
    {
        var check = ServerReadinessPolicy.CommitterIdentity(new CommitProbeFact(true, true, "abc"));

        Assert.Equal(ReadinessStates.Pass, check.State);
        Assert.Equal("A probe commit in a scratch repository succeeded, so merges and artifact commits can record a committer.", check.Meaning);
    }

    [Fact]
    public void A_commit_tree_probe_refused_for_identity_fails_with_the_identity_remedy()
    {
        var output = "Committer identity unknown\n\n*** Please tell me who you are.\n\nfatal: unable to auto-detect email address (got 'agentstudio@abc.(none)')";
        var check = ServerReadinessPolicy.CommitterIdentity(new CommitProbeFact(true, false, output));

        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.Equal("git commit-tree failed in a scratch repository (Committer identity unknown). "
            + "Direct merges and artifact commits fail the same way.", check.Meaning);
        Assert.Contains("git config --global user.name \"Agent Studio\"", check.Remedy);
    }

    [Fact]
    public void A_commit_tree_probe_failing_for_another_reason_gives_the_manual_probe()
    {
        var check = ServerReadinessPolicy.CommitterIdentity(new CommitProbeFact(true, false, "fatal: disk quota exceeded"));

        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.Equal("Run the probe by hand and fix what it prints: docker compose exec api sh -c "
            + "'cd \"$(mktemp -d)\" && git init -q && git commit-tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904 -m probe'",
            check.Remedy);
    }

    [Fact]
    public void A_probe_that_could_not_run_is_unknown()
    {
        var check = ServerReadinessPolicy.CommitterIdentity(new CommitProbeFact(false, false, "No space left on device"));

        Assert.Equal(ReadinessStates.Unknown, check.State);
        Assert.Equal("The probe could not run: No space left on device.", check.Meaning);
        Assert.StartsWith("Run the probe by hand", check.Remedy);
    }

    [Fact]
    public void A_data_folder_on_its_own_mount_passes()
    {
        var check = ServerReadinessPolicy.DataFolder(
            new DataFolderFact("home", "/data/home", "home", true, "/data/home", "ext4"), runsInContainer: true);

        Assert.Equal(ReadinessStates.Pass, check.State);
        Assert.Equal("home", check.Subject);
        Assert.Equal("/data/home is on the mount /data/home (ext4), outside the container layer.", check.Meaning);
    }

    [Fact]
    public void A_data_folder_in_the_container_layer_fails_with_the_volume_line()
    {
        var check = ServerReadinessPolicy.DataFolder(
            new DataFolderFact("home", "/data/home", "home", true, "/", "overlay"), runsInContainer: true);

        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.Equal("/data/home is inside the container layer: whatever Studio writes there is lost when the container is recreated.", check.Meaning);
        Assert.Equal("Mount it from the data directory: add \"- ${STUDIO_DATA_DIR}/home:/data/home\" under services.api.volumes "
            + "in compose.override.yaml and run docker compose up -d.", check.Remedy);
    }

    [Fact]
    public void A_project_checkout_in_the_container_layer_fails_under_its_project()
    {
        var check = ServerReadinessPolicy.DataFolder(
            new DataFolderFact("Demo", "/data/projects/demo", "projects/demo", true, "/", "overlay"),
            runsInContainer: true, ReadinessScopes.Project, "Demo");

        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.Equal(ReadinessScopes.Project, check.Scope);
        Assert.Contains("\"- ${STUDIO_DATA_DIR}/projects/demo:/data/projects/demo\"", check.Remedy);
    }

    [Fact]
    public void A_missing_data_folder_fails()
    {
        var check = ServerReadinessPolicy.DataFolder(
            new DataFolderFact("workspace", "/data/workspace", "workspace", false, null, null), runsInContainer: true);

        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.Equal("/data/workspace does not exist, so Studio cannot keep its workspace data.", check.Meaning);
        Assert.EndsWith("create that folder owned by UID 10001, and run docker compose up -d.", check.Remedy);
    }

    [Fact]
    public void Outside_a_container_every_data_folder_is_on_the_host()
    {
        var check = ServerReadinessPolicy.DataFolder(
            new DataFolderFact("workspace", "/srv/workspace", "workspace", true, "/", "ext4"), runsInContainer: false);

        Assert.Equal(ReadinessStates.Pass, check.State);
        Assert.Equal("/srv/workspace is on the host file system; Studio does not run in a container.", check.Meaning);
    }

    [Fact]
    public void An_unreadable_mount_table_leaves_the_data_folder_unknown()
    {
        var check = ServerReadinessPolicy.DataFolder(
            new DataFolderFact("workspace", "/data/workspace", "workspace", true, null, null, "permission denied"), runsInContainer: true);

        Assert.Equal(ReadinessStates.Unknown, check.State);
        Assert.Equal("Check the mounts by hand: docker compose exec api cat /proc/self/mountinfo", check.Remedy);
    }

    [Fact]
    public void A_writable_state_folder_passes_and_a_read_only_one_fails_with_chown()
    {
        var pass = ServerReadinessPolicy.StateFolder(new StateFolderFact("workspace", "/data/workspace", true, null));
        var fail = ServerReadinessPolicy.StateFolder(
            new StateFolderFact("workspace", "/data/workspace", false, "Access to the path is denied."));

        Assert.Equal(ReadinessStates.Pass, pass.State);
        Assert.Equal("Studio created and removed a probe file in /data/workspace.", pass.Meaning);
        Assert.Equal(ReadinessStates.Fail, fail.State);
        Assert.Contains("(Access to the path is denied.)", fail.Meaning);
        Assert.Equal("Give the host folder behind /data/workspace to the service user: "
            + "sudo chown -R 10001:10001 \"$STUDIO_DATA_DIR/workspace\", then docker compose restart api.", fail.Remedy);
    }

    [Fact]
    public void Young_locks_pass_and_stale_locks_warn_with_the_files_to_remove()
    {
        var young = ServerReadinessPolicy.GitLocks(ReadinessScopes.Server, "workspace",
            [new GitLockFact("/data/workspace/.git/index.lock", TimeSpan.FromMinutes(2))]);
        var stale = ServerReadinessPolicy.GitLocks(ReadinessScopes.Server, "workspace",
            [new GitLockFact("/data/workspace/.git/index.lock", TimeSpan.FromMinutes(70)),
             new GitLockFact("/data/workspace/.git/refs/heads/main.lock", TimeSpan.FromMinutes(12))]);

        Assert.Equal(ReadinessStates.Pass, young.State);
        Assert.Equal(ReadinessStates.Warn, stale.State);
        Assert.StartsWith("2 Git lock file(s) older than 10 minutes, the oldest 1 h:", stale.Meaning);
        Assert.Equal("Make sure no git process runs in the repository (ps -ef | grep git), then remove them: "
            + "rm -f /data/workspace/.git/index.lock /data/workspace/.git/refs/heads/main.lock", stale.Remedy);
    }

    [Fact]
    public void A_healthy_pull_gate_worker_passes()
    {
        var check = ServerReadinessPolicy.GateWorker(
            new GateWorkerFact([new GateWorkerHostFact("kori-gate", 3, Now.AddSeconds(-30), true)], null, "unverified", null, null), Now);

        Assert.Equal(ReadinessStates.Pass, check.State);
        Assert.Equal("kori-gate", check.Subject);
        Assert.Equal("Pull gate worker kori-gate sent a heartbeat less than a minute ago (3 slot(s)).", check.Meaning);
    }

    [Fact]
    public void A_silent_pull_gate_worker_fails()
    {
        var check = ServerReadinessPolicy.GateWorker(
            new GateWorkerFact([new GateWorkerHostFact("kori-gate", 3, Now.AddMinutes(-40), false)], null, "unverified", null, null), Now);

        Assert.Equal(ReadinessStates.Fail, check.State);
        Assert.Equal("No pull gate worker is alive: kori-gate sent its last heartbeat 40 min ago. Build gates wait until a worker is back.", check.Meaning);
        Assert.Equal("On the gate host, restart the gate worker of kori-gate and check in its log that it reaches this server's URL.", check.Remedy);
    }

    [Theory]
    [InlineData("reachable", ReadinessStates.Pass)]
    [InlineData("unreachable", ReadinessStates.Fail)]
    [InlineData("unverified", ReadinessStates.Unknown)]
    public void An_ssh_gate_host_follows_its_last_contact(string reachability, string expected)
    {
        var check = ServerReadinessPolicy.GateWorker(
            new GateWorkerFact([], "kori-gate", reachability, Now.AddMinutes(-2), Now.AddMinutes(-1)), Now);

        Assert.Equal(expected, check.State);
        Assert.Equal("kori-gate", check.Subject);
        if (expected != ReadinessStates.Pass)
            Assert.Contains("docker compose exec api ssh -o BatchMode=yes kori-gate true", check.Remedy);
    }

    [Fact]
    public void No_gate_worker_at_all_warns()
    {
        var check = ServerReadinessPolicy.GateWorker(new GateWorkerFact([], null, "unverified", null, null), Now);

        Assert.Equal(ReadinessStates.Warn, check.State);
        Assert.Equal("Start a pull gate worker against this server, or set RemoteGate__SshHost and RemoteGate__WorkerPath "
            + "for the api service and run docker compose up -d.", check.Remedy);
    }

    [Fact]
    public void Each_execution_host_is_seen_never_seen_or_silent()
    {
        var enrolled = Now.UtcDateTime.AddDays(-3);
        var checks = ServerReadinessPolicy.ExecutionHosts(new ExecutionHostsFact(
        [
            new ExecutionHostFact("runner_a", "alpha", enrolled, Now.UtcDateTime.AddMinutes(-3)),
            new ExecutionHostFact("runner_b", "bravo", enrolled, null),
            new ExecutionHostFact("runner_c", "charlie", enrolled, Now.UtcDateTime.AddHours(-5)),
        ], 0), Now).ToArray();

        Assert.Equal(["alpha", "bravo", "charlie"], checks.Select(check => check.Subject));
        Assert.Equal([ReadinessStates.Pass, ReadinessStates.Fail, ReadinessStates.Fail], checks.Select(check => check.State));
        Assert.Equal("Enrolled and last seen 3 min ago.", checks[0].Meaning);
        Assert.Equal("Enrolled 2026-10-29 but has never contacted this server.", checks[1].Meaning);
        Assert.Equal("Enrolled, but not seen for 5 h: it claims no cards.", checks[2].Meaning);
        Assert.Equal("On charlie, check that the runner service runs and its RUNNER_SERVER_URL points at this server, "
            + "then restart it; re-enrol it if its credential was revoked.", checks[2].Remedy);
    }

    [Fact]
    public void No_enrolled_host_warns_and_mentions_unredeemed_codes()
    {
        var check = Assert.Single(ServerReadinessPolicy.ExecutionHosts(new ExecutionHostsFact([], 2), Now));

        Assert.Equal(ReadinessStates.Warn, check.State);
        Assert.Null(check.Subject);
        Assert.Contains("2 enrolment code(s) are issued but not redeemed", check.Meaning);
        Assert.Equal("Create an enrolment under Execution Hosts and run the enrolment command it shows on the host.", check.Remedy);
    }

    [Theory]
    [InlineData("private", ReadinessStates.Pass)]
    [InlineData("public", ReadinessStates.Pass)]
    [InlineData(null, ReadinessStates.Warn)]
    public void Repository_visibility_must_be_recorded(string? visibility, string expected)
    {
        var check = ServerReadinessPolicy.RepositoryVisibility(Project(visibility: visibility));

        Assert.Equal(expected, check.State);
        if (visibility is null)
        {
            Assert.Contains("which fails for a private repository", check.Meaning);
            Assert.Equal("Record it: PUT /api/projects/Demo/repository-visibility with {\"visibility\":\"private\"} "
                + "or {\"visibility\":\"public\"}.", check.Remedy);
        }
    }

    [Theory]
    [InlineData(GitHubAppCoverageStates.Covered, ReadinessStates.Pass, null)]
    [InlineData(GitHubAppCoverageStates.NotGitHub, ReadinessStates.Pass, null)]
    [InlineData(GitHubAppCoverageStates.NotInstalled, ReadinessStates.Fail,
        "Install the App on this repository: https://github.com/apps/studio/installations/new, then Recheck.")]
    [InlineData(GitHubAppCoverageStates.NotAdopted, ReadinessStates.Fail, "Adopt the installation under Settings > GitHub, then Recheck.")]
    [InlineData(GitHubAppCoverageStates.NoRepositoryUrl, ReadinessStates.Warn,
        "Add the repository URL as the project's \"Repository\" link, then Recheck.")]
    [InlineData(GitHubAppCoverageStates.Unknown, ReadinessStates.Unknown,
        "Recheck in a few minutes; if it stays unknown, check the App connection under Settings > GitHub.")]
    public void App_coverage_follows_the_coverage_state(string coverage, string expected, string? remedy)
    {
        var app = new GitHubAppFact(true, "https://github.com/apps/studio/installations/new", null,
            new Dictionary<string, GitHubAppCoverageFact>(StringComparer.OrdinalIgnoreCase)
            {
                ["demo"] = new(coverage, "https://github.com/acme/demo", 42, "rate limited"),
            });

        var check = ServerReadinessPolicy.AppCoverage("Demo", app);

        Assert.Equal(expected, check.State);
        Assert.Equal(remedy, check.Remedy);
    }

    [Fact]
    public void Without_a_connected_app_coverage_warns()
    {
        var check = ServerReadinessPolicy.AppCoverage("Demo",
            new GitHubAppFact(false, null, null, new Dictionary<string, GitHubAppCoverageFact>()));

        Assert.Equal(ReadinessStates.Warn, check.State);
        Assert.Equal("Connect the App under Settings > GitHub, or keep a credential for this repository on every host.", check.Remedy);
    }

    [Fact]
    public void A_project_missing_from_the_app_status_is_unknown()
    {
        var check = ServerReadinessPolicy.AppCoverage("Demo",
            new GitHubAppFact(true, null, "GitHub answered 502", new Dictionary<string, GitHubAppCoverageFact>()));

        Assert.Equal(ReadinessStates.Unknown, check.State);
        Assert.Equal("The App status lists no coverage for this project (GitHub answered 502).", check.Meaning);
    }

    [Fact]
    public void The_integration_branch_comes_from_origin_head_or_the_setting()
    {
        var fromHead = ServerReadinessPolicy.IntegrationBranch(Project());
        var configured = ServerReadinessPolicy.IntegrationBranch(
            Project(branch: " develop ", refs: RefsWithHead("main", "refs/remotes/origin/develop")));

        Assert.Equal(("main", ReadinessStates.Pass), (fromHead.Branch, fromHead.Check.State));
        Assert.Equal("Integrates into main (from origin/HEAD); origin/main is present.", fromHead.Check.Meaning);
        Assert.Equal(("develop", ReadinessStates.Pass), (configured.Branch, configured.Check.State));
        Assert.Equal("Integrates into develop (set for the project); origin/develop is present.", configured.Check.Meaning);
    }

    [Fact]
    public void An_unset_branch_without_origin_head_fails_with_set_head()
    {
        var result = ServerReadinessPolicy.IntegrationBranch(Project(refs: RefsWithHead(null, "refs/remotes/origin/main")));

        Assert.Null(result.Branch);
        Assert.Equal(ReadinessStates.Fail, result.Check.State);
        Assert.Equal("Run git -C /data/projects/demo remote set-head origin --auto, or set it: "
            + "PUT /api/projects/Demo/integration-branch with {\"branch\":\"<branch>\"}.", result.Check.Remedy);
    }

    [Fact]
    public void A_branch_missing_on_origin_fails_with_fetch()
    {
        var result = ServerReadinessPolicy.IntegrationBranch(Project(branch: "develop"));

        Assert.Null(result.Branch);
        Assert.Equal(ReadinessStates.Fail, result.Check.State);
        Assert.Equal("The integration branch develop (set for the project) has no origin/develop in this checkout, so every integration fails.",
            result.Check.Meaning);
        Assert.StartsWith("Run git -C /data/projects/demo fetch origin develop;", result.Check.Remedy);
    }

    [Fact]
    public void A_checkout_without_origin_integrates_into_its_local_branch()
    {
        var present = ServerReadinessPolicy.IntegrationBranch(Project(branch: "main", refs: RefsWithHead(null, "refs/heads/main")));
        var absent = ServerReadinessPolicy.IntegrationBranch(Project(branch: "main", refs: RefsWithHead(null, "refs/heads/trunk")));
        var unset = ServerReadinessPolicy.IntegrationBranch(Project(refs: RefsWithHead(null, "refs/heads/main")));

        Assert.Equal(ReadinessStates.Pass, present.Check.State);
        Assert.Equal(ReadinessStates.Fail, absent.Check.State);
        Assert.Equal("Create it (git -C /data/projects/demo branch main) or set the right one: "
            + "PUT /api/projects/Demo/integration-branch with {\"branch\":\"<branch>\"}.", absent.Check.Remedy);
        Assert.Equal("Set it: PUT /api/projects/Demo/integration-branch with {\"branch\":\"<branch>\"}.", unset.Check.Remedy);
    }

    [Fact]
    public void A_missing_or_foreign_checkout_fails_and_a_git_error_is_unknown()
    {
        var missing = ServerReadinessPolicy.IntegrationBranch(Project(refs: RepositoryRefsFact.Missing)).Check;
        var notGit = ServerReadinessPolicy.IntegrationBranch(Project(refs: RepositoryRefsFact.NotGit)).Check;
        var broken = ServerReadinessPolicy.IntegrationBranch(Project(refs:
            new RepositoryRefsFact(true, true, new HashSet<string>(), null, "fatal: bad object"))).Check;

        Assert.Equal(ReadinessStates.Fail, missing.State);
        Assert.Equal("Clone the repository there: git clone <repository URL> /data/projects/demo, then Recheck.", missing.Remedy);
        Assert.Equal(ReadinessStates.Fail, notGit.State);
        Assert.Equal("/data/projects/demo is not a Git checkout.", notGit.Meaning);
        Assert.Equal(ReadinessStates.Unknown, broken.State);
        Assert.Contains("fatal: bad object", broken.Meaning);
    }

    [Fact]
    public void A_missing_integration_lane_warns_with_the_seed_command()
    {
        var withOrigin = ServerReadinessPolicy.IntegrationLane(Project(refs: Refs("refs/remotes/origin/main")), "main");
        var local = ServerReadinessPolicy.IntegrationLane(Project(refs: RefsWithHead(null, "refs/heads/main")), "main");
        var present = ServerReadinessPolicy.IntegrationLane(Project(), "main");

        Assert.Equal(ReadinessStates.Warn, withOrigin.State);
        Assert.StartsWith("The integration lane refs/agent-studio/integration/main is missing, as in a fresh clone.", withOrigin.Meaning);
        Assert.Equal("Seed it from the published branch: git -C /data/projects/demo update-ref "
            + "refs/agent-studio/integration/main origin/main", withOrigin.Remedy);
        Assert.EndsWith("update-ref refs/agent-studio/integration/main main", local.Remedy);
        Assert.Equal(ReadinessStates.Pass, present.State);
    }

    [Fact]
    public void An_unresolved_branch_reports_no_lane_line_and_a_missing_checkout_no_volume_line()
    {
        var report = ServerReadinessPolicy.Evaluate(Facts(Project(refs: RepositoryRefsFact.Missing)));
        var project = report.Checks.Where(check => check.Scope == ReadinessScopes.Project).Select(check => check.Id);

        Assert.Equal(["repository-visibility", "app-coverage", "integration-branch", "git-locks"], project);
    }

    [Fact]
    public void A_filtered_report_recounts_its_lines()
    {
        var report = ServerReadinessPolicy.Evaluate(Facts(Project(visibility: null)));

        var serverOnly = report.Where(check => check.Scope != ReadinessScopes.Project);

        Assert.Equal(ReadinessStates.Warn, report.State);
        Assert.Equal(ReadinessStates.Pass, serverOnly.State);
        Assert.Equal(0, serverOnly.Attention);
        Assert.Same(ServerReadinessReport.Pending, ServerReadinessReport.Pending.Where(_ => false));
    }
}
