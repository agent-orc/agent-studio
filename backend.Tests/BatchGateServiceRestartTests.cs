using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateServiceRestartTests : BatchGatePilotServiceTests
{
    /// <summary>
    /// 2 November 2026: two planned restarts of the API parked cards that had
    /// nothing wrong with them. Each restart interrupted the running per-task
    /// gate and charged its start budget, and the first integration after the
    /// start fetched a private repository before the host's App tokens were
    /// written. Here the service stops twice while the gate runs, starts with a
    /// private repository that refuses the credential, and no card changes lane.
    /// </summary>
    [Fact]
    public async Task Two_service_restarts_with_a_running_gate_and_a_private_repository_move_no_card()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-RESTART", "docs/restart.md");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var developBefore = RemoteTip();

        for (var restart = 1; restart <= 2; restart++)
        {
            // The service's own stop cancels its host token while the gate runs.
            using var host = new CancellationTokenSource();
            _gate.Fallback = _ =>
            {
                host.Cancel();
                throw new OperationCanceledException();
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pilot.TickAsync(host.Token));
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

            Assert.Equal(restart, _gate.FallbackRuns);
            AssertLane(member.Key, TaskStates.AutoReview);
            var marker = Assert.IsType<BatchGateOwnership>(Ownership(member.Key));
            Assert.True(marker.FallbackGateActive);
            Assert.Equal(0, marker.FallbackGateStarts);
            _clock = _clock.AddMinutes(1);
        }

        // The start after the second restart: the gate resumes and passes, and
        // the private repository refuses the integration fetch because the
        // host's App tokens are not written yet.
        var refuse = Path.Combine(_workspace, "refuse-credentials.sh");
        File.WriteAllText(refuse,
            "#!/bin/sh\necho \"fatal: could not read Username for 'https://github.com': terminal prompts disabled\" >&2\nexit 128\n");
        Git(_repo, "config", "protocol.ext.allow", "always");
        Git(_repo, "remote", "set-url", "origin", "ext::sh " + refuse);
        _gate.Fallback = request => Green(request.ExpectedSha);

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(3, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.AutoReview);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var card = scanner.FindJob(member.Key, _watchPath)!;
        var integration = factory.Services.GetRequiredService<TaskIntegrationStatusService>();
        var mergeStep = integration.ReadLatestMergeStep(card)!;
        Assert.Equal(AcceptedIntegrationFailureCodes.GitCredentialUnavailable, mergeStep.FailureCode);
        Assert.Contains("could not read Username", mergeStep.Reason);

        // The delivery chain does not park the card on the first refused fetch.
        await factory.Services.GetRequiredService<DeliveryChainReconciler>().RunOnceAsync(CancellationToken.None);
        await factory.Services.GetRequiredService<DeliveryChainReconciler>().RunOnceAsync(CancellationToken.None);

        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Equal(developBefore, RemoteTip());

        // The tokens are written: the bounded ladder replays the integration of
        // the same reviewed delivery without a new review, instead of parking it.
        Git(_repo, "remote", "set-url", "origin", _origin);
        // The first rung is due five minutes after the recorded failure.
        var failedAt = (mergeStep.CompletedAt ?? mergeStep.StartedAt)!.Value;
        _clock = new DateTimeOffset(DateTime.SpecifyKind(failedAt, DateTimeKind.Utc)).AddMinutes(6);
        var retries = factory.Services.GetRequiredService<GateEnvironmentRetryService>();
        scanner.InvalidateCache();
        card = scanner.FindJob(member.Key, _watchPath)!;
        var evaluation = retries.Evaluate(card, integration.BuildLookup([card])[card.TaskKey],
            GateEnvironmentRetryOptions.Default);
        Assert.Null(AgentStudio.Runner.MonolithTransitionAdapter
            .GateEnvironmentRetryPolicyOperatorReplayRefusal(evaluation.Facts));
        Assert.True(evaluation.Facts.IsGateEnvironmentFailure);
        Assert.Equal(0, evaluation.Ledger.AttemptsSpent);

        var sweep = await retries.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, sweep.Retried);
        Assert.Equal(0, sweep.Parked);
        AssertLane(member.Key, TaskStates.AutoReview);
    }
}
