using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PersonaLeaseRunnerTests
{
    [Fact]
    public void Dense_persona_grounds_are_refused_before_a_model_call()
    {
        Assert.False(PersonaLeaseRunner.PromptOverrun(
            new string('a', 8_000), 12_000));
        Assert.True(PersonaLeaseRunner.PromptOverrun(
            new string('!', 12_000), 12_000));
    }

    [Fact]
    public void Persona_child_environment_drops_deploy_credentials_but_keeps_provider_auth()
    {
        var environment = new Dictionary<string, string>
        {
            ["AWS_SECRET_ACCESS_KEY"] = Guid.NewGuid().ToString("N"),
            ["GITHUB_TOKEN"] = Guid.NewGuid().ToString("N"),
            ["ANTHROPIC_API_KEY"] = Guid.NewGuid().ToString("N"),
        };

        WorkerEdgeCredentialBoundary.RemovePersonaDeploymentCredentials(environment);

        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", environment.Keys);
        Assert.DoesNotContain("GITHUB_TOKEN", environment.Keys);
        Assert.Contains("ANTHROPIC_API_KEY", environment.Keys);
    }
}
