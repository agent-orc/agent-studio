using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RunnerHostRecordTests
{
    private static RunnerHostRecord Record(string tokenRoot = "/etc/agent-runner", int total = 3, int coding = 2, int review = 1)
        => new(
            RunnerHostRecord.CurrentSchemaVersion,
            "build-02",
            "linux",
            "http://127.0.0.1:15031",
            "git@example.invalid:team/project.git",
            new HostEnvelopeDto(total, coding, review),
            [
                new RunnerHostRoleRecord(HostRoles.Coding, "rnr-build-02-coding", $"{tokenRoot}/coding.token"),
                new RunnerHostRoleRecord(HostRoles.Review, "rnr-build-02-review", $"{tokenRoot}/review.token"),
            ],
            Resources: new Dictionary<string, string> { ["REVIEW_MEMORY_MAX"] = "6G" });

    [Fact]
    public void Render_uses_static_unit_names_and_one_env_file_per_role()
    {
        var rendering = RunnerHostRecordPolicy.Render(Record());

        Assert.Equal(
            [("coding", "agent-runner.service", "/etc/agent-runner/runner.env"),
             ("review", "agent-runner-review.service", "/etc/agent-runner/review.env")],
            rendering.Services.Select(service => (service.Role, service.UnitName, service.EnvFile)));
        Assert.Equal(["profile.conf", "review.env", "runner.env"], rendering.Files.Keys);
        var coding = RunnerHostRecordPolicy.ParseEnv(rendering.Files["runner.env"]);
        var review = RunnerHostRecordPolicy.ParseEnv(rendering.Files["review.env"]);
        Assert.Equal("build-02", coding["RUNNER_HOSTNAME"]);
        Assert.Equal("build-02", review["RUNNER_HOSTNAME"]);
        Assert.Equal("2", coding["RUNNER_MAX_PARALLELISM"]);
        Assert.Equal("1", coding["RUNNER_HOST_REVIEW_SLOTS"]);
        Assert.Equal("1", review["RUNNER_MAX_PARALLELISM"]);
        Assert.Equal("2", review["RUNNER_HOST_CODING_SLOTS"]);
        Assert.NotEqual(coding["RUNNER_ID"], review["RUNNER_ID"]);
        Assert.NotEqual(coding["RUNNER_AUTH_TOKEN_FILE"], review["RUNNER_AUTH_TOKEN_FILE"]);
        Assert.Contains("REVIEW_MEMORY_MAX=6G", rendering.Files["profile.conf"]);
        Assert.Contains("HOST_TOTAL_SLOTS=3", rendering.Files["profile.conf"]);
        Assert.Equal(rendering.Files, RunnerHostRecordPolicy.Render(Record()).Files);
    }

    [Fact]
    public void Single_role_host_declares_zero_slots_for_the_absent_role()
    {
        var record = Record(total: 2, coding: 2, review: 0) with
        {
            Roles = [new RunnerHostRoleRecord(HostRoles.Coding, "rnr-coding", "/etc/agent-runner/coding.token")],
        };
        var rendering = RunnerHostRecordPolicy.Render(record);
        Assert.Single(rendering.Services);
        Assert.Equal("0", RunnerHostRecordPolicy.ParseEnv(rendering.Files["runner.env"])["RUNNER_HOST_REVIEW_SLOTS"]);
    }

    [Theory]
    [InlineData("shared-principal")]
    [InlineData("shared-token")]
    [InlineData("oversized-role")]
    [InlineData("workstation-without-roots")]
    public void Validate_rejects_merged_credentials_and_unbounded_envelopes(string defect)
    {
        var record = Record();
        record = defect switch
        {
            "shared-principal" => record with { Roles = [record.Roles[0], record.Roles[1] with { PrincipalId = record.Roles[0].PrincipalId }] },
            "shared-token" => record with { Roles = [record.Roles[0], record.Roles[1] with { TokenFile = record.Roles[0].TokenFile }] },
            "oversized-role" => record with { Envelope = new HostEnvelopeDto(2, 3, 1) },
            _ => record with { Workstation = new RunnerHostWorkstation([]) },
        };
        Assert.NotEmpty(RunnerHostRecordPolicy.Validate(record));
    }

    [Fact]
    public void Workstation_host_renders_explicit_roots_without_new_unit_names()
    {
        var record = Record() with
        {
            HostClass = "windows",
            Workstation = new RunnerHostWorkstation(["/home/dev/src"], ["dotnet", "node"]),
        };
        var env = RunnerHostRecordPolicy.ParseEnv(RunnerHostRecordPolicy.Render(record).Files["runner.env"]);
        Assert.Equal("1", env["RUNNER_WORKSTATION"]);
        Assert.Equal("/home/dev/src", env["RUNNER_WORKSTATION_ROOTS"]);
        Assert.Equal("dotnet,node", env["RUNNER_WORKSTATION_TOOLS"]);
    }

    [Fact]
    public void Migration_imports_onboarded_role_files_and_round_trips()
    {
        const string runnerEnv = """
            RUNNER_SERVER_URL=http://127.0.0.1:15031
            RUNNER_ID=rnr-a-coding
            RUNNER_NAME=build-01
            RUNNER_ROLE=coding
            RUNNER_AUTH_TOKEN_FILE=/etc/agent-runner/coding.token
            RUNNER_GIT_REMOTE=git@example.invalid:team/project.git
            RUNNER_MAX_PARALLELISM=2
            RUNNER_HOST_REVIEW_SLOTS=2
            """;
        const string reviewEnv = """
            RUNNER_SERVER_URL=http://127.0.0.1:15031
            RUNNER_ID=rnr-a-review
            RUNNER_ROLE=review
            RUNNER_AUTH_TOKEN_FILE=/etc/agent-runner/review.token
            RUNNER_GIT_REMOTE=git@example.invalid:team/project.git
            RUNNER_MAX_PARALLELISM=2
            RUNNER_HOST_CODING_SLOTS=2
            """;
        var migration = RunnerHostRecordPolicy.Migrate(
            runnerEnv, reviewEnv, "CODING_MEMORY_MAX=12G\nUNRELATED=1\n", "linux");

        var record = Assert.IsType<RunnerHostRecord>(migration.Record);
        Assert.Equal(new HostEnvelopeDto(4, 2, 2), record.Envelope);
        Assert.Equal(Environment.MachineName, record.HostId);
        Assert.Equal("12G", record.Resources!["CODING_MEMORY_MAX"]);
        Assert.Contains(migration.Notes, note => note.Contains("UNRELATED"));
        Assert.Contains(migration.Notes, note => note.Contains("pinned the current machine name"));

        var rendering = RunnerHostRecordPolicy.Render(record);
        var again = RunnerHostRecordPolicy.Migrate(
            rendering.Files["runner.env"], rendering.Files["review.env"], rendering.Files["profile.conf"], "linux");
        Assert.Equal(
            RunnerHostRecordPolicy.Digest(record),
            RunnerHostRecordPolicy.Digest(again.Record!));
    }

    [Fact]
    public void Migration_refuses_disagreeing_or_shared_host_facts()
    {
        const string coding = "RUNNER_SERVER_URL=http://a\nRUNNER_ID=same\nRUNNER_AUTH_TOKEN_FILE=/t1\nRUNNER_GIT_REMOTE=r\n";
        const string review = "RUNNER_SERVER_URL=http://b\nRUNNER_ID=same\nRUNNER_ROLE=review\nRUNNER_AUTH_TOKEN_FILE=/t2\nRUNNER_GIT_REMOTE=r\n";
        var migration = RunnerHostRecordPolicy.Migrate(coding, review, null, "linux");
        Assert.Null(migration.Record);
        Assert.Contains(migration.Errors, item => item.Contains("RUNNER_SERVER_URL differs"));

        var sameServer = review.Replace("http://b", "http://a");
        var shared = RunnerHostRecordPolicy.Migrate(coding, sameServer, null, "linux");
        Assert.Contains(shared.Errors, item => item.Contains("distinct principals"));
    }

    [Fact]
    public void Generated_role_files_load_into_runner_options_with_the_declared_slot_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), "host-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "coding.token"), "rnr.test-token\n");
            File.WriteAllText(Path.Combine(root, "review.token"), "rnr.test-token-review\n");
            if (!OperatingSystem.IsWindows())
                foreach (var token in new[] { "coding.token", "review.token" })
                    File.SetUnixFileMode(Path.Combine(root, token), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var rendering = RunnerHostRecordPolicy.Render(Record(root));
            foreach (var (file, role, own, coding, review) in new[]
                     {
                         ("runner.env", "coding", 2, 2, 1),
                         ("review.env", "review", 1, 2, 1),
                     })
            {
                var env = RunnerHostRecordPolicy.ParseEnv(rendering.Files[file]);
                var previous = env.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
                try
                {
                    foreach (var (key, value) in env) Environment.SetEnvironmentVariable(key, value);
                    var (options, _, _, _) = RunnerOptions.Parse([]);
                    Assert.Equal(role, options.Role);
                    Assert.Equal("build-02", options.Hostname);
                    Assert.Equal(own, options.HostMaxParallelism);
                    Assert.Equal(coding, options.HostCodingSlots);
                    Assert.Equal(review, options.HostReviewSlots);
                }
                finally
                {
                    foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Command_renders_and_checks_a_record_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "host-record-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var recordPath = Path.Combine(root, "host.json");
            File.WriteAllText(recordPath, System.Text.Json.JsonSerializer.Serialize(Record(), RunnerHostRecord.Json));
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(0, RunnerHostRecordCommand.Run(["check", "--record", recordPath], output, error));
            Assert.Contains("roles=coding,review slots=3/2/1", output.ToString());
            output = new StringWriter();
            Assert.Equal(0, RunnerHostRecordCommand.Run(
                ["render", "--record", recordPath, "--out-dir", Path.Combine(root, "out")], output, error));
            Assert.Contains("agent-runner-review.service", output.ToString());
            Assert.True(File.Exists(Path.Combine(root, "out", "runner.env")));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(root, "out", "review.env")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
