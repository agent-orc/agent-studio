using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed partial class RemoteReviewAuthorityTests
{
    [Theory]
    [InlineData("claude", false)]
    [InlineData("codex", false)]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    public async Task Model_routing_uses_the_installation_authority_for_all_review_commands(string preferred, bool bothExhausted)
    {
        using var temp = _fixture.CreateDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var seed = Store(temp.Path, clock);
        await seed.InitializeAsync();
        var aspects = new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence", "code-review-grade" };
        var plan = ReviewLibraryStepPolicy.Seal(new ReviewPlanDto(aspects.Select(aspect => new ReviewCommandDto(
            "aspect-" + aspect, aspect, preferred, [], ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: "Review the immutable delivery.", CliType: preferred, Model: "fixture-" + preferred, ThinkingLevel: "high")).ToArray(),
            aspects, IntegrationRef: "refs/heads/develop", LibraryVersion: 1), ResultSha);
        await SeedReviewSubjectAsync(seed, plan: plan);
        await RegisterReviewerAsync(seed, "review-a", "instance-a", "host-a");
        await seed.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest("review-a", "instance-a",
            CapabilityProtocol.CurrentSchemaVersion, clock.GetUtcNow().UtcDateTime, 900, 1,
            [new(CapabilityProtocol.CliExecution("claude"), "cli-execution", "ready"),
             new(CapabilityProtocol.ProviderAuthentication("claude"), "provider-auth", "ready")]), "review-a", default);
        var routing = new RoutingAuthority(bothExhausted);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock,
            new ApplicationResultFinalizationSummaryGenerator(), null, null, modelRouteAdmission: routing);
        await store.InitializeAsync();
        var claim = await store.ClaimReviewAsync(new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        Assert.NotEmpty(routing.Requests);
        if (bothExhausted)
        {
            Assert.Equal("empty", claim.Status);
            Assert.Contains("all providers", claim.Message);
        }
        else
        {
            Assert.Equal("claimed", claim.Status);
            Assert.Equal(5, routing.Requests.Count);
            var target = preferred == "claude" ? "codex" : "claude";
            Assert.All(claim.Subject!.Plan.Commands, command => Assert.Equal(target, command.CliType));
            Assert.True(ReviewLibraryStepPolicy.ValidPlan(claim.Subject.Plan, ResultSha));
        }
    }

    private sealed class RoutingAuthority(bool bothExhausted) : IModelRouteAdmission
    {
        public List<ModelRouteAdmissionRequest> Requests { get; } = [];
        public Task<SelectionResult> SelectAsync(ModelRouteAdmissionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var primary = new SelectionRoute(request.Request.Pins.CliType!, request.Request.Pins.Model, request.Request.Pins.ThinkingLevel);
            var target = primary.CliType == "claude" ? "codex" : "claude";
            return Task.FromResult(new SelectionResult
            {
                Outcome = bothExhausted ? SelectionOutcome.Wait : SelectionOutcome.Fallback,
                Primary = primary, Route = bothExhausted ? primary : new(target, "fixture-" + target, "high"),
                Rule = SelectionRules.QuotaCap, Reason = bothExhausted ? "all providers exhausted" : "fixture quota fallback",
            });
        }
    }
}
