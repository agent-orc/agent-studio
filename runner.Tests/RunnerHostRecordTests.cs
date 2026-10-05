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
    public void Missing_roles_report_validation_instead_of_throwing_or_emitting_enrolment()
    {
        var record = Record() with { Roles = null! };
        Assert.Contains(RunnerHostRecordPolicy.Validate(record), item => item.Contains("At least one role service"));

        var root = Path.Combine(Path.GetTempPath(), "host-record-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "host.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(record, RunnerHostRecord.Json));
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(2, RunnerHostRecordCommand.Run(["enrolment", "--record", path], output, error));
            Assert.Empty(output.ToString());
            Assert.Contains("At least one role service", error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        Assert.False(env.ContainsKey("RUNNER_CLAUDE_CLI_BIN"));
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
    public void Migration_preserves_a_shared_ceiling_below_the_sum_of_role_caps()
    {
        var desired = Record(total: 3, coding: 2, review: 2);
        var files = RunnerHostRecordPolicy.Render(desired).Files;

        var migrated = RunnerHostRecordPolicy.Migrate(
            files["runner.env"], files["review.env"], files["profile.conf"], "linux");

        Assert.Empty(migrated.Errors);
        Assert.Equal(new HostEnvelopeDto(3, 2, 2), migrated.Record!.Envelope);
        Assert.Equal("3", RunnerHostRecordPolicy.ParseEnv(
            RunnerHostRecordPolicy.Render(migrated.Record).Files["profile.conf"])["HOST_TOTAL_SLOTS"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("invalid")]
    [InlineData("1")]
    public void Migration_refuses_an_invalid_or_underprovisioned_shared_ceiling(string total)
    {
        var files = RunnerHostRecordPolicy.Render(Record()).Files;
        var migrated = RunnerHostRecordPolicy.Migrate(
            files["runner.env"], files["review.env"], $"HOST_TOTAL_SLOTS={total}\n", "linux");

        Assert.Null(migrated.Record);
        Assert.NotEmpty(migrated.Errors);
    }

    [Fact]
    public void Migration_refuses_profile_role_caps_that_disagree_with_role_files()
    {
        var files = RunnerHostRecordPolicy.Render(Record()).Files;
        var profile = files["profile.conf"].Replace(
            "CODING_SLOTS=2", "CODING_SLOTS=1", StringComparison.Ordinal);
        Assert.Equal("1", RunnerHostRecordPolicy.ParseEnv(profile)["CODING_SLOTS"]);
        var migrated = RunnerHostRecordPolicy.Migrate(
            files["runner.env"], files["review.env"], profile, "linux");

        Assert.Null(migrated.Record);
        Assert.Contains(migrated.Errors, error => error.Contains("CODING_SLOTS must match"));
    }

    [Theory]
    [InlineData("coding", "invalid")]
    [InlineData("review", "")]
    public void Migration_refuses_an_explicit_invalid_role_slot_count(string role, string value)
    {
        var files = RunnerHostRecordPolicy.Render(Record()).Files;
        var file = role == "coding" ? "runner.env" : "review.env";
        var original = role == "coding" ? "2" : "1";
        var changed = files[file].Replace($"RUNNER_MAX_PARALLELISM={original}",
            $"RUNNER_MAX_PARALLELISM={value}", StringComparison.Ordinal);
        var migrated = RunnerHostRecordPolicy.Migrate(
            file == "runner.env" ? changed : files["runner.env"],
            file == "review.env" ? changed : files["review.env"],
            files["profile.conf"], "linux");

        Assert.Null(migrated.Record);
        Assert.Contains(migrated.Errors, error => error.Contains($"{role} RUNNER_MAX_PARALLELISM"));
    }

    [Fact]
    public void Migration_uses_legacy_default_only_when_role_slot_count_is_absent()
    {
        var files = RunnerHostRecordPolicy.Render(Record()).Files;
        var coding = files["runner.env"].Replace("RUNNER_MAX_PARALLELISM=2\n", "", StringComparison.Ordinal);
        var migrated = RunnerHostRecordPolicy.Migrate(
            coding, files["review.env"], files["profile.conf"], "linux");

        Assert.Empty(migrated.Errors);
        Assert.Equal(2, migrated.Record!.Envelope.CodingSlots);
    }

    [Theory]
    [InlineData("runner.env", "RUNNER_HOST_REVIEW_SLOTS=1", "RUNNER_HOST_REVIEW_SLOTS=3")]
    [InlineData("review.env", "RUNNER_HOST_CODING_SLOTS=2", "RUNNER_HOST_CODING_SLOTS=5")]
    [InlineData("runner.env", "RUNNER_HOST_REVIEW_SLOTS=1", "RUNNER_HOST_REVIEW_SLOTS=two")]
    [InlineData("runner.env", "RUNNER_HOST_CODING_SLOTS=2", "RUNNER_HOST_CODING_SLOTS=3")]
    public void Migration_refuses_a_role_file_slot_count_that_disagrees_with_the_migrated_envelope(
        string file, string original, string changed)
    {
        // The coding file is read first, so its peer count used to be compared
        // with nothing; the review file's mismatch only produced a note.
        var files = RunnerHostRecordPolicy.Render(Record()).Files;
        var edited = files[file].Replace(original, changed, StringComparison.Ordinal);
        Assert.NotEqual(files[file], edited);
        var migrated = RunnerHostRecordPolicy.Migrate(
            file == "runner.env" ? edited : files["runner.env"],
            file == "review.env" ? edited : files["review.env"],
            files["profile.conf"], "linux");

        Assert.Null(migrated.Record);
        Assert.Contains(migrated.Errors, error => error.Contains($"{file} declares {changed}"));
    }

    [Fact]
    public void Migration_refuses_a_peer_slot_count_for_a_role_file_that_was_not_supplied()
    {
        var files = RunnerHostRecordPolicy.Render(Record()).Files;
        var migrated = RunnerHostRecordPolicy.Migrate(files["runner.env"], null, null, "linux");

        Assert.Null(migrated.Record);
        Assert.Contains(migrated.Errors, error => error.Contains(
            "runner.env declares RUNNER_HOST_REVIEW_SLOTS=1, but the record would generate RUNNER_HOST_REVIEW_SLOTS=0"));
    }

    [Theory]
    [InlineData("review.env", "RUNNER_GIT_PUSH_REMOTE=git@example.invalid:team/push.git\n", "RUNNER_GIT_PUSH_REMOTE is set in review.env but not in runner.env")]
    [InlineData("runner.env", null, "RUNNER_HOSTNAME is set in review.env but not in runner.env")]
    public void Migration_refuses_a_shared_fact_declared_by_only_one_role_file(string file, string? added, string expected)
    {
        var files = RunnerHostRecordPolicy.Render(Record()).Files;
        var edited = added is null
            ? files[file].Replace("RUNNER_HOSTNAME=build-02\n", "", StringComparison.Ordinal)
            : files[file] + added;
        Assert.NotEqual(files[file], edited);
        var migrated = RunnerHostRecordPolicy.Migrate(
            file == "runner.env" ? edited : files["runner.env"],
            file == "review.env" ? edited : files["review.env"],
            files["profile.conf"], "linux");

        Assert.Null(migrated.Record);
        Assert.Contains(migrated.Errors, error => error.Contains(expected));
    }

    [Theory]
    [InlineData("RUNNER_TLS_CERTIFICATE_SHA256=ab12cd", "which the host record does not carry")]
    [InlineData("RUNNER_WORKSTATION=1", "which the host record does not carry")]
    [InlineData("RUNNER_STATE_DIR=/srv/runner-state", "the record would generate RUNNER_STATE_DIR=/var/lib/agent-runner/state")]
    [InlineData("RUNNER_CLAUDE_CLI_BIN=/opt/claude/bin/claude", "the record would generate RUNNER_CLAUDE_CLI_BIN=/usr/local/bin/claude")]
    public void Migration_refuses_a_role_setting_the_generated_file_would_drop_or_rewrite(string setting, string expected)
    {
        var files = RunnerHostRecordPolicy.Render(Record()).Files;
        var migrated = RunnerHostRecordPolicy.Migrate(
            files["runner.env"] + setting + "\n", files["review.env"], files["profile.conf"], "linux");

        Assert.Null(migrated.Record);
        Assert.Contains(migrated.Errors, error => error.Contains("runner.env declares " + setting) && error.Contains(expected));
    }

    [Fact]
    public void Migration_reproduces_every_setting_of_legacy_onboarded_role_files()
    {
        // The exact key set remote-runner-onboard.sh writes without --host-record.
        static string Legacy(string role, string id, string root, string token) => $"""
            RUNNER_SERVER_URL=http://127.0.0.1:15031
            RUNNER_ID={id}
            RUNNER_NAME=build-02-{role}
            RUNNER_ROLE={role}
            RUNNER_HOSTNAME=build-02
            RUNNER_CLI_TYPE=claude
            RUNNER_CLAUDE_CLI_BIN=/usr/local/bin/claude
            RUNNER_CODEX_CLI_BIN=/usr/local/bin/codex
            RUNNER_AUTH_TOKEN_FILE={token}
            RUNNER_GIT_REMOTE=git@example.invalid:team/project.git
            RUNNER_WORKDIR={root}/work
            {(role == "review" ? $"RUNNER_REVIEW_WORKDIR={root}/review-work" : "")}
            RUNNER_STATE_DIR={root}/state
            RUNNER_MAX_PARALLELISM={(role == "coding" ? 2 : 1)}
            RUNNER_HOST_CODING_SLOTS=2
            RUNNER_HOST_REVIEW_SLOTS=1
            """;
        var migrated = RunnerHostRecordPolicy.Migrate(
            Legacy("coding", "rnr-coding", "/var/lib/agent-runner", "/etc/agent-runner/coding.token"),
            Legacy("review", "rnr-review", "/var/lib/agent-runner-review", "/etc/agent-runner/review.token"),
            null, "linux");

        Assert.Empty(migrated.Errors);
        Assert.Equal(new HostEnvelopeDto(3, 2, 1), migrated.Record!.Envelope);
        var generated = RunnerHostRecordPolicy.ParseEnv(RunnerHostRecordPolicy.Render(migrated.Record).Files["review.env"]);
        Assert.Equal("/usr/local/bin/claude", generated["RUNNER_CLAUDE_CLI_BIN"]);
        Assert.Equal("/var/lib/agent-runner-review/review-work", generated["RUNNER_REVIEW_WORKDIR"]);
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

    [Theory]
    [InlineData("--runner-env", "runner.env")]
    [InlineData("--review-env", "review.env")]
    [InlineData("--profile", "profile.conf")]
    public void Migration_refuses_an_explicit_missing_input_before_writing_the_record(string option, string file)
    {
        var root = Path.Combine(Path.GetTempPath(), "host-record-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rendering = RunnerHostRecordPolicy.Render(Record());
            foreach (var (name, content) in rendering.Files)
                if (name != file) File.WriteAllText(Path.Combine(root, name), content);
            var outputPath = Path.Combine(root, "host.json");
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.Equal(2, RunnerHostRecordCommand.Run(
                ["migrate", "--runner-env", Path.Combine(root, "runner.env"),
                    "--review-env", Path.Combine(root, "review.env"),
                    "--profile", Path.Combine(root, "profile.conf"), "--out", outputPath], output, error));
            Assert.Empty(output.ToString());
            Assert.False(File.Exists(outputPath));
            Assert.Contains(option, error.ToString());
            Assert.Contains(Path.Combine(root, file), error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Migration_allows_an_omitted_optional_role_and_profile_but_refuses_a_flag_without_a_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "host-record-single-role-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var record = Record(total: 2, coding: 2, review: 0) with
            {
                Roles = [Record().Roles[0]],
            };
            var runnerEnvPath = Path.Combine(root, "runner.env");
            File.WriteAllText(runnerEnvPath, RunnerHostRecordPolicy.Render(record).Files["runner.env"]);
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.Equal(0, RunnerHostRecordCommand.Run(
                ["migrate", "--runner-env", runnerEnvPath], output, error));
            Assert.Contains("\"role\": \"coding\"", output.ToString());
            Assert.DoesNotContain("\"role\": \"review\"", output.ToString());

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            Assert.Equal(2, RunnerHostRecordCommand.Run(
                ["migrate", "--runner-env", runnerEnvPath, "--profile"], output, error));
            Assert.Empty(output.ToString());
            Assert.Contains("--profile needs a value", error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    [Theory]
    [InlineData("migrate", "--reveiw-env", "x", "migrate does not accept --reveiw-env")]
    [InlineData("check", "--record", "a", "--record is given more than once")]
    [InlineData("enrolment", "--expected-generation", "99999999999999999999", "--expected-generation must be a non-negative integer")]
    public void Command_refuses_unknown_repeated_or_unparseable_options(string verb, string option, string value, string expected)
    {
        var args = verb == "check" ? new[] { verb, option, value, option, value } : [verb, option, value];
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(2, RunnerHostRecordCommand.Run(args, output, error));
        Assert.Empty(output.ToString());
        Assert.Contains(expected, error.ToString());
    }

    [Theory]
    [InlineData("\"reviewSlot\": 2", "reviewSlot")]
    [InlineData("\"roles\": [null]", "A role entry is empty")]
    [InlineData("\"workstation\": {}", "at least one root")]
    public void Command_refuses_a_record_with_unknown_or_empty_fields_instead_of_defaulting(string field, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "host-record-fields-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(Record(), RunnerHostRecord.Json))!.AsObject();
            var patch = System.Text.Json.Nodes.JsonNode.Parse("{" + field + "}")!.AsObject();
            foreach (var (key, value) in patch.ToArray())
            {
                patch.Remove(key);
                json[key] = value;
            }
            var path = Path.Combine(root, "host.json");
            File.WriteAllText(path, json.ToJsonString());
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.Equal(2, RunnerHostRecordCommand.Run(["check", "--record", path], output, error));
            Assert.Empty(output.ToString());
            Assert.Contains(expected, error.ToString());
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
