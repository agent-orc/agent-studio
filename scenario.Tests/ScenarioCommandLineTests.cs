using Xunit;

namespace AgentStudio.Scenario.Tests;

public sealed class ScenarioCommandLineTests
{
    private static string? NoEnvironment(string name) => null;

    [Theory]
    [InlineData("inproc", ScenarioTargetKind.InProc)]
    [InlineData("compose", ScenarioTargetKind.Compose)]
    [InlineData("remote", ScenarioTargetKind.Remote)]
    [InlineData("INPROC", ScenarioTargetKind.InProc)]
    public void Every_supported_target_parses(string value, ScenarioTargetKind expected)
    {
        string[] args = value.Equals("remote", StringComparison.OrdinalIgnoreCase)
            ? ["--target", value, "--server-url", "https://tasks.invalid", "--token", "credential"]
            : ["--target", value];

        var result = ScenarioCommandLine.Parse(args, NoEnvironment);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Options!.Target);
    }

    [Fact]
    public void Level_defaults_to_smoke_so_a_gate_never_runs_the_long_suite_by_accident()
    {
        var result = ScenarioCommandLine.Parse(["--target", "inproc"], NoEnvironment);

        Assert.Equal(ScenarioLevel.Smoke, result.Options!.Level);
    }

    [Fact]
    public void Full_level_is_selected_explicitly()
    {
        var result = ScenarioCommandLine.Parse(
            ["--target", "inproc", "--level", "full"], NoEnvironment);

        Assert.Equal(ScenarioLevel.Full, result.Options!.Level);
    }

    [Fact]
    public void Missing_target_is_a_usage_error()
    {
        var result = ScenarioCommandLine.Parse(["--level", "full"], NoEnvironment);

        Assert.Null(result.Options);
        Assert.Contains("--target", result.Error);
    }

    [Theory]
    [InlineData("--target", "podman")]
    [InlineData("--level", "deep")]
    public void An_unknown_enumeration_value_is_rejected(string option, string value)
    {
        var result = ScenarioCommandLine.Parse(
            ["--target", "inproc", option, value], NoEnvironment);

        Assert.Null(result.Options);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void An_unknown_option_is_rejected_rather_than_ignored()
    {
        var result = ScenarioCommandLine.Parse(
            ["--target", "inproc", "--parallel", "4"], NoEnvironment);

        Assert.Contains("--parallel", result.Error);
    }

    [Fact]
    public void An_option_without_a_value_is_rejected()
    {
        var result = ScenarioCommandLine.Parse(["--target"], NoEnvironment);

        Assert.Contains("needs a value", result.Error);
    }

    [Theory]
    [InlineData("--server-url")]
    [InlineData("--token")]
    public void The_remote_target_requires_its_address_and_credential(string missing)
    {
        string[] args = missing == "--server-url"
            ? ["--target", "remote", "--token", "credential"]
            : ["--target", "remote", "--server-url", "https://tasks.invalid"];

        var result = ScenarioCommandLine.Parse(args, NoEnvironment);

        Assert.Null(result.Options);
        Assert.Contains(missing, result.Error);
    }

    [Fact]
    public void A_remote_address_must_be_an_absolute_http_url()
    {
        var result = ScenarioCommandLine.Parse(
            ["--target", "remote", "--server-url", "tasks.invalid", "--token", "credential"],
            NoEnvironment);

        Assert.Contains("absolute http", result.Error);
    }

    [Fact]
    public void Remote_options_are_refused_for_a_local_target_instead_of_being_ignored()
    {
        var result = ScenarioCommandLine.Parse(
            ["--target", "inproc", "--server-url", "https://tasks.invalid"], NoEnvironment);

        Assert.Null(result.Options);
        Assert.Contains("remote", result.Error);
    }

    [Fact]
    public void A_trailing_slash_on_a_remote_address_is_normalized()
    {
        var result = ScenarioCommandLine.Parse(
            ["--target", "remote", "--server-url", "https://tasks.invalid/", "--token", "c"],
            NoEnvironment);

        Assert.Equal("https://tasks.invalid", result.Options!.RemoteTaskServerUrl);
    }

    [Fact]
    public void The_job_results_directory_is_the_default_output_so_evidence_is_collected()
    {
        var result = ScenarioCommandLine.Parse(
            ["--target", "inproc"],
            name => name == "JOB_RESULTS_DIR" ? "/results" : null);

        Assert.Equal("/results", result.Options!.OutputDirectory);
    }

    [Fact]
    public void An_explicit_output_directory_wins_over_the_environment()
    {
        var result = ScenarioCommandLine.Parse(
            ["--target", "inproc", "--out", "/tmp/report"],
            name => name == "JOB_RESULTS_DIR" ? "/results" : null);

        Assert.Equal("/tmp/report", result.Options!.OutputDirectory);
    }

    [Fact]
    public void The_default_document_is_the_shipped_scenario()
    {
        var result = ScenarioCommandLine.Parse(["--target", "inproc"], NoEnvironment);

        Assert.Equal(ScenarioCommandLine.DefaultDocumentPath, result.Options!.DocumentPath);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Help_short_circuits_before_any_validation(string flag)
    {
        var result = ScenarioCommandLine.Parse([flag], NoEnvironment);

        Assert.True(result.HelpRequested);
        Assert.Null(result.Error);
    }
}
