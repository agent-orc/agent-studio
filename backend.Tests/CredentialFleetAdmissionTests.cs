using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CredentialFleetAdmissionTests
{
    [Fact]
    public void Legacy_coding_and_review_claim_sets_respect_provider_incident_signal()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = new V1ReviewExecutorRegistry(clock);
        var provider = CapabilityProtocol.ProviderAuthentication("codex");
        registry.Register("coding", new RegisterRunnerRequest("coding", "host", "coding-instance",
            "1", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor, provider]));
        registry.Register("review", new RegisterRunnerRequest("review", "host", "review-instance",
            "1", TaskServerProtocol.Current, [ReviewCapabilities.ReviewExecutor, provider]));
        foreach (var (runner, instance, executor) in new[]
                 { ("coding", "coding-instance", CapabilityProtocol.CodingExecutor),
                   ("review", "review-instance", CapabilityProtocol.ReviewExecutor) })
        {
            registry.AdvertiseCapabilities(runner, new CapabilityAdvertisementRequest(
                runner, instance, 2, clock.GetUtcNow().UtcDateTime, 180, 1,
                [new AdvertisedCapabilityDto(executor, "executor"),
                 new AdvertisedCapabilityDto(provider, "provider-auth", "ready",
                     Signal: "provider_incident")], CredentialHealthVersion: 1));
        }
        Assert.False(registry.EvaluateCodingAdmission("coding", "coding-instance",
            [CapabilityProtocol.CodingExecutor, provider]).Eligible);
        Assert.DoesNotContain(provider, registry.AdmittedReviewCapabilities("review", "review-instance"));
    }
}
