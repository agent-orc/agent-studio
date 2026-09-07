using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ExecutionOutcomeAdapterTests
{
    public static TheoryData<string, ExecutionRawFacts, ExecutionOutcomeKind, ExecutionRecoveryAction> ReplayCases => new()
    {
        {
            "AGT-2070 CLI crash",
            Coding(ExitCode: -1, StdErr: "provider process terminated unexpectedly"),
            ExecutionOutcomeKind.CliCrash,
            ExecutionRecoveryAction.StartFreshAttemptFromSalvage
        },
        {
            "AGT-2148 provider completion without sentinel",
            Coding(
                ProviderTerminalEvent: """{"type":"turn.completed"}""",
                FinalAssistantOutput: "Implemented the requested change and verified the tests.",
                ExitCode: 0,
                DurableOutputState: DurableOutputState.Acknowledged),
            ExecutionOutcomeKind.SuccessfulCompletion,
            ExecutionRecoveryAction.TerminateHonestly
        },
        {
            "auth 401",
            Coding(ExitCode: 1, StdErr: "HTTP 401 Missing bearer or basic authentication"),
            ExecutionOutcomeKind.AuthenticationFailure,
            ExecutionRecoveryAction.WaitForCapabilityRecovery
        },
        {
            "quota",
            Coding(ExitCode: 1, StdErr: "429 quota exceeded"),
            ExecutionOutcomeKind.QuotaExceeded,
            ExecutionRecoveryAction.WaitForCapabilityRecovery
        },
        {
            "invalid model configuration",
            Coding(ExitCode: 1, StdErr: "invalid model configuration: model does not exist"),
            ExecutionOutcomeKind.InvalidModelOrConfiguration,
            ExecutionRecoveryAction.WaitForCapabilityRecovery
        },
        {
            "launch failure",
            Coding(ExitCode: -1, StdErr: "No such executable", LaunchFailed: true),
            ExecutionOutcomeKind.LaunchFailure,
            ExecutionRecoveryAction.WaitForCapabilityRecovery
        },
        {
            "timeout",
            Coding(TimedOut: true),
            ExecutionOutcomeKind.Timeout,
            ExecutionRecoveryAction.StartFreshAttemptFromSalvage
        },
        {
            "OOM",
            Coding(ExitCode: 137, OomKilled: true),
            ExecutionOutcomeKind.OutOfMemory,
            ExecutionRecoveryAction.StartFreshAttemptFromSalvage
        },
        {
            "lease loss",
            Coding(LeaseLost: true),
            ExecutionOutcomeKind.LeaseLoss,
            ExecutionRecoveryAction.TerminateHonestly
        },
        {
            "operator drain",
            Coding(HostShutdown: true),
            ExecutionOutcomeKind.HostShutdown,
            ExecutionRecoveryAction.StartFreshAttemptFromSalvage
        },
        {
            "explicit operator cancellation",
            Coding(OperatorCancelled: true),
            ExecutionOutcomeKind.OperatorCancellation,
            ExecutionRecoveryAction.TerminateHonestly
        },
        {
            "process signal",
            Coding(ExitCode: 139, Signal: 11),
            ExecutionOutcomeKind.CliCrash,
            ExecutionRecoveryAction.StartFreshAttemptFromSalvage
        },
        {
            "lost transport after publish",
            Coding(
                TransportState: ExecutionTransportState.Lost,
                DurableOutputState: DurableOutputState.Published),
            ExecutionOutcomeKind.TransportLoss,
            ExecutionRecoveryAction.RetryHandoff
        },
        {
            "explicit blocker",
            Coding(FinalAssistantOutput: "[[TASK_BLOCKED:missing signing credential]]", ExitCode: 0),
            ExecutionOutcomeKind.ExplicitAgentBlocker,
            ExecutionRecoveryAction.AskForHumanInput
        },
        {
            "exit zero without terminal marker",
            Coding(FinalAssistantOutput: "I changed several files.", ExitCode: 0),
            ExecutionOutcomeKind.ProtocolInconclusive,
            ExecutionRecoveryAction.AskForHumanInput
        },
    };

    [Theory]
    [MemberData(nameof(ReplayCases))]
    public void Incident_replays_produce_typed_outcome_and_recovery(
        string fixture,
        ExecutionRawFacts facts,
        ExecutionOutcomeKind expectedOutcome,
        ExecutionRecoveryAction expectedRecovery)
    {
        Assert.False(string.IsNullOrWhiteSpace(fixture));
        var result = ExecutionOutcomeAdapter.Classify(facts);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedRecovery, result.RecoveryAction);
        Assert.Equal(ExecutionOutcomeAdapter.Version, result.ClassifierVersion);
        Assert.False(result.ConsumesProductDefectBudget);
        Assert.False(result.ConsumesCompletionBudget);
        Assert.False(result.ConsumesCodingReworkBudget);
    }

    [Fact]
    public void Clean_done_completion_is_not_reclassified_by_diagnostic_text_mentions()
    {
        // Live incident 25.07.: agent CLIs narrate over stderr; a successful
        // TASK_DONE run that merely *discussed* OOM handling was classified
        // OutOfMemory. Regex-based infrastructure matches must not override an
        // exit-0 run with an explicit DONE sentinel.
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 0,
            FinalAssistantOutput: "Hardened the lease path for out of memory kills.\n\n[[TASK_DONE]]",
            StdErr: "considering: cannot allocate memory scenarios; quota exceeded paths; invalid session recovery"));

        Assert.Equal(ExecutionOutcomeKind.SuccessfulCompletion, result.Outcome);
    }

    [Fact]
    public void Real_oom_kill_still_wins_even_with_done_sentinel()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 0,
            FinalAssistantOutput: "[[TASK_DONE]]",
            OomKilled: true));

        Assert.Equal(ExecutionOutcomeKind.OutOfMemory, result.Outcome);
    }

    [Fact]
    public void Blocked_completion_is_not_reclassified_as_infra()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 0,
            FinalAssistantOutput: "[[TASK_BLOCKED:deck-panel-v1-decision-missing]]",
            StdErr: "Narrative covered out of memory, 401 unauthorized, 429 quota exceeded, "
                    + "invalid session recovery, and invalid model configuration."));

        Assert.Equal(ExecutionOutcomeKind.ExplicitAgentBlocker, result.Outcome);
        Assert.Equal(ExecutionRecoveryAction.AskForHumanInput, result.RecoveryAction);
        Assert.False(result.IsInfrastructureOutcome);
    }

    [Fact]
    public void Real_oom_kill_still_wins_even_with_blocked_sentinel()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 0,
            FinalAssistantOutput: "[[TASK_BLOCKED:deck-panel-v1-decision-missing]]",
            OomKilled: true));

        Assert.Equal(ExecutionOutcomeKind.OutOfMemory, result.Outcome);
        Assert.True(result.IsInfrastructureOutcome);
    }

    [Fact]
    public void Blocked_completion_carries_reason_slug_as_detail()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 0,
            FinalAssistantOutput: "[[TASK_BLOCKED:deck-panel-v1-decision-missing]]"));

        Assert.Equal(ExecutionOutcomeKind.ExplicitAgentBlocker, result.Outcome);
        Assert.Equal("deck-panel-v1-decision-missing", result.Detail);
    }

    [Fact]
    public void Same_session_resume_is_bounded_once_then_falls_back_once_then_stops()
    {
        var first = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 1,
            SessionState: ExecutionSessionState.Resumable,
            SessionId: "session-1",
            SameSessionResumeAttempts: 0));
        var fallback = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 1,
            SessionState: ExecutionSessionState.Invalid,
            SessionId: "session-1",
            SameSessionResumeAttempts: 1,
            FreshSalvageAttempts: 0));
        var exhausted = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 1,
            SessionState: ExecutionSessionState.Invalid,
            SessionId: "session-1",
            SameSessionResumeAttempts: 1,
            FreshSalvageAttempts: 1));

        Assert.Equal(ExecutionRecoveryAction.ResumeSameSession, first.RecoveryAction);
        Assert.Equal(ExecutionRecoveryAction.StartFreshAttemptFromSalvage, fallback.RecoveryAction);
        Assert.Equal(ExecutionRecoveryAction.TerminateHonestly, exhausted.RecoveryAction);
    }

    [Fact]
    public void Provider_rejected_session_never_retries_the_same_invalid_session()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ProviderTerminalEvent: """{"type":"turn.failed","error":"thread not found"}""",
            StdErr: "thread cannot be resumed",
            ExitCode: 1,
            SessionState: ExecutionSessionState.Resumable,
            SessionId: "session-gone",
            DurableOutputState: DurableOutputState.Published,
            DurableOutputReference: "refs/heads/runner/test/AGT-2185"));

        Assert.Equal(ExecutionOutcomeKind.InvalidSession, result.Outcome);
        Assert.Equal(ExecutionRecoveryAction.StartFreshAttemptFromSalvage, result.RecoveryAction);
    }

    [Fact]
    public void Host_local_workspace_is_not_misreported_as_durable_salvage()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 1,
            DurableOutputState: DurableOutputState.LocalOnly,
            DurableOutputReference: "/runner/worktrees/AGT-2185"));

        Assert.Equal(ExecutionOutcomeKind.CliCrash, result.Outcome);
        Assert.Equal(ExecutionRecoveryAction.TerminateHonestly, result.RecoveryAction);
    }

    [Fact]
    public void Review_infrastructure_retry_stays_on_the_same_immutable_subject_and_never_invokes_coding()
    {
        var facts = new ExecutionRawFacts(
            "review-42",
            ExecutionAttemptKind.Review,
            ExitCode: 1,
            StdErr: "CLI process crashed",
            ReviewSubject: new ImmutableReviewSubject("repo-7", ResultSha: new string('a', 40)));

        var result = ExecutionOutcomeAdapter.Classify(facts);

        Assert.Equal(ExecutionOutcomeKind.CliCrash, result.Outcome);
        Assert.Equal(ExecutionRecoveryAction.RetryReviewAttemptOnSameSubject, result.RecoveryAction);
        Assert.False(result.InvokesCodingModel);
        Assert.Equal(new string('a', 40), result.RawFacts.ReviewSubject!.ResultSha);
    }

    [Fact]
    public void Review_retry_without_an_immutable_subject_fails_closed()
    {
        var result = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "review-43",
            ExecutionAttemptKind.Review,
            ExitCode: 1,
            StdErr: "CLI process crashed"));

        Assert.Equal(ExecutionRecoveryAction.AskForHumanInput, result.RecoveryAction);
        Assert.False(result.InvokesCodingModel);
    }

    [Fact]
    public void Review_retry_rejects_a_moving_or_non_content_addressed_subject()
    {
        var result = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "review-44",
            ExecutionAttemptKind.Review,
            ExitCode: 1,
            StdErr: "CLI process crashed",
            ReviewSubject: new ImmutableReviewSubject("repo-7", ResultSha: "main")));

        Assert.Equal(ExecutionRecoveryAction.AskForHumanInput, result.RecoveryAction);
        Assert.False(result.InvokesCodingModel);
    }

    [Fact]
    public void Retry_handoff_never_invokes_the_coding_model()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            TransportState: ExecutionTransportState.Lost,
            DurableOutputState: DurableOutputState.Published));

        Assert.Equal(ExecutionRecoveryAction.RetryHandoff, result.RecoveryAction);
        Assert.False(result.InvokesCodingModel);
    }

    [Fact]
    public void Divergent_salvage_recovery_uses_the_exact_local_result_sha()
    {
        var decision = ExecutionOutcomeAdapter.Classify(Coding(ExitCode: 1));
        var localResult = new string('b', 40);
        var canonicalRemote = new string('a', 40);
        var durable = RemoteTaskRunner.WithDurableOutput(
            decision,
            new WorktreeTeardownResult(
                true,
                "runner/agent-runner-01/AGT-2185",
                canonicalRemote,
                "https://example.invalid/branch",
                ResultSha: localResult,
                Reconciliation: new SalvageReconciliationResult(
                    "divergent",
                    "runner/agent-runner-01/AGT-2185",
                    canonicalRemote,
                    localResult,
                    "salvage/AGT-2185",
                    localResult,
                    "runner/agent-runner-01/AGT-2185",
                    canonicalRemote)));

        Assert.Equal(localResult, durable.RawFacts.DurableOutputReference);
        Assert.Equal(DurableOutputState.Acknowledged, durable.RawFacts.DurableOutputState);
        Assert.Equal(ExecutionRecoveryAction.StartFreshAttemptFromSalvage, durable.RecoveryAction);
    }

    [Fact]
    public void Provider_jsonl_extraction_preserves_final_prose_terminal_event_and_session()
    {
        var evidence = ProviderOutputEvidenceExtractor.Extract("""
            {"type":"thread.started","thread_id":"thread-9"}
            {"type":"item.completed","item":{"type":"agent_message","text":"Raw semantic review prose"}}
            {"type":"turn.completed","usage":{"input_tokens":12}}
            """);

        Assert.Equal("thread-9", evidence.SessionId);
        Assert.Equal("Raw semantic review prose", evidence.FinalAssistantOutput);
        Assert.Contains("turn.completed", evidence.TerminalEvent);
        Assert.True(evidence.ProviderReportedCompletion);
    }

    [Fact]
    public void Missing_sentinel_uses_out_of_band_only_with_exact_registered_repo_proof()
    {
        var baseSha = new string('0', 40);
        var commit = new string('a', 40);
        var verified = new WorktreeTeardownResult(
            true,
            "runner/agent-runner-01/AGT-2220",
            commit,
            "https://example.invalid/branch",
            ResultSha: commit,
            DeliveryProof: new RemoteDeliveryProof(
                "https://example.invalid/project.git",
                "refs/heads/runner/agent-runner-01/AGT-2220",
                commit));
        var missingSentinel = new RunOutcome(
            RunOutcomeKind.Unknown,
            "The provider completed without a terminal sentinel.");
        var inconclusive = ExecutionOutcomeAdapter.Classify(Coding(
            ExitCode: 0,
            FinalAssistantOutput: "Work was committed."));

        var request = RemoteTaskRunner.BuildVerifiedOutOfBandRequest(
            missingSentinel,
            inconclusive,
            verified,
            baseSha,
            "agent-runner-01");

        Assert.NotNull(request);
        Assert.Equal("5-human-review", request!.TargetState);
        Assert.Contains(commit, request.Summary);
        Assert.Contains(
            request.Deliverables!,
            item => item.Path == $"refs/heads/runner/agent-runner-01/AGT-2220@{commit}");

        Assert.Null(RemoteTaskRunner.BuildVerifiedOutOfBandRequest(
            missingSentinel,
            inconclusive,
            verified with { DeliveryProof = null },
            baseSha,
            "agent-runner-01"));
        Assert.Null(RemoteTaskRunner.BuildVerifiedOutOfBandRequest(
            missingSentinel,
            inconclusive,
            verified with
            {
                ResultSha = baseSha,
                DeliveryProof = verified.DeliveryProof! with { CommitSha = baseSha },
            },
            baseSha,
            "agent-runner-01"));
        Assert.Null(RemoteTaskRunner.BuildVerifiedOutOfBandRequest(
            missingSentinel,
            inconclusive,
            verified with
            {
                DeliveryProof = verified.DeliveryProof! with { CommitSha = new string('b', 40) },
            },
            baseSha,
            "agent-runner-01"));
        Assert.Null(RemoteTaskRunner.BuildVerifiedOutOfBandRequest(
            new RunOutcome(RunOutcomeKind.Done, "Sentinel present."),
            ExecutionOutcomeAdapter.Classify(Coding(
                ExitCode: 0,
                FinalAssistantOutput: "[[TASK_DONE]]")),
            verified,
            baseSha,
            "agent-runner-01"));
    }

    [Fact]
    public void Turn_failed_with_unchanged_secured_work_reports_failure_instead_of_external_completion()
    {
        var baseSha = new string('a', 40);
        const string message = "Selected model is at capacity. Please try a different model.";
        var transcript =
            $"{{\"type\":\"error\",\"message\":\"{message}\"}}\n" +
            $"{{\"type\":\"turn.failed\",\"error\":{{\"message\":\"{message}\"}}}}";
        var provider = ProviderOutputEvidenceExtractor.Extract(transcript);
        var decision = ExecutionOutcomeAdapter.Classify(Coding(
            ProviderTerminalEvent: provider.TerminalEvent,
            FinalAssistantOutput: provider.FinalAssistantOutput,
            StdOut: transcript,
            ExitCode: 1));
        var teardown = new WorktreeTeardownResult(
            true,
            "runner/agent-runner-01/AGT-2692",
            baseSha,
            "https://example.invalid/branch",
            ResultSha: baseSha,
            DeliveryProof: new RemoteDeliveryProof(
                "https://example.invalid/project.git",
                "refs/heads/runner/agent-runner-01/AGT-2692",
                baseSha));

        Assert.Equal(ExecutionOutcomeKind.QuotaExceeded, decision.Outcome);
        Assert.Equal(message, provider.FailureMessage);
        var failedAttempt = RemoteTaskRunner.BuildRunOutcome(
            decision,
            provider,
            SentinelScanner.Scan(transcript),
            message);
        Assert.Equal(RunOutcomeKind.Unknown, failedAttempt.Kind);
        Assert.Equal(message, failedAttempt.Reason);
        Assert.Null(RemoteTaskRunner.BuildVerifiedOutOfBandRequest(
            failedAttempt,
            decision,
            teardown,
            baseSha,
            "agent-runner-01"));
        var changedSha = new string('b', 40);
        Assert.Null(RemoteTaskRunner.BuildVerifiedOutOfBandRequest(
            failedAttempt,
            decision,
            teardown with
            {
                ResultSha = changedSha,
                DeliveryProof = teardown.DeliveryProof! with { CommitSha = changedSha },
            },
            baseSha,
            "agent-runner-01"));
    }

    [Fact]
    public void Claude_error_result_is_provider_failure_not_successful_completion()
    {
        const string terminal = """
            {"type":"result","subtype":"error_max_turns","is_error":true,"result":"Maximum turns reached","session_id":"claude-9"}
            """;
        var evidence = ProviderOutputEvidenceExtractor.Extract(terminal);
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ProviderTerminalEvent: evidence.TerminalEvent,
            FinalAssistantOutput: evidence.FinalAssistantOutput,
            ExitCode: 0,
            SessionState: ExecutionSessionState.Resumable,
            SessionId: evidence.SessionId));

        Assert.False(evidence.ProviderReportedCompletion);
        Assert.True(evidence.ProviderReportedFailure);
        Assert.Equal("Maximum turns reached", evidence.FinalAssistantOutput);
        Assert.Equal(ExecutionOutcomeKind.CliCrash, result.Outcome);
        Assert.Equal(ExecutionRecoveryAction.ResumeSameSession, result.RecoveryAction);
    }

    [Fact]
    public void Successful_provider_terminal_is_not_overridden_by_failure_words_in_final_prose()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            ProviderTerminalEvent: """{"type":"turn.completed"}""",
            FinalAssistantOutput: "The earlier 401 and quota failure are fixed.",
            ExitCode: 0,
            DurableOutputState: DurableOutputState.Acknowledged));

        Assert.Equal(ExecutionOutcomeKind.SuccessfulCompletion, result.Outcome);
        Assert.Equal("The earlier 401 and quota failure are fixed.", result.RawFacts.FinalAssistantOutput);
    }

    [Fact]
    public void Apply_patch_verification_failure_is_a_cli_tool_failure_not_authentication()
    {
        const string error = "ERROR codex_core::tools::router: error=apply_patch verification failed: "
            + "Failed to find context 'public sealed class V1ReviewExecutorRegistry' in "
            + "/home/agent/runner-work/PROJ-002/worktrees/AGT-2694/backend/Features/Runner/V1ReviewPlaneEndpoints.cs";

        var result = ExecutionOutcomeAdapter.Classify(Coding(ExitCode: 1, StdErr: error));

        Assert.Equal(ExecutionOutcomeKind.CliCrash, result.Outcome);
        Assert.NotEqual(ExecutionRecoveryAction.WaitForCapabilityRecovery, result.RecoveryAction);
    }

    [Fact]
    public void Prompt_echo_sentinel_does_not_override_a_distinct_final_assistant_output()
    {
        var result = ExecutionOutcomeAdapter.Classify(Coding(
            FinalAssistantOutput: "I could not reach a supported terminal conclusion.",
            StdOut: "Prompt contract example: [[TASK_DONE]]",
            ExitCode: 0));

        Assert.Equal(ExecutionOutcomeKind.ProtocolInconclusive, result.Outcome);
        Assert.Equal(ExecutionRecoveryAction.AskForHumanInput, result.RecoveryAction);
    }

    private static ExecutionRawFacts Coding(
        string? ProviderTerminalEvent = null,
        string? FinalAssistantOutput = null,
        string? StdOut = null,
        string? StdErr = null,
        int? ExitCode = null,
        int? Signal = null,
        bool LaunchFailed = false,
        bool TimedOut = false,
        bool OomKilled = false,
        bool OperatorCancelled = false,
        bool HostShutdown = false,
        bool LeaseLost = false,
        ExecutionTransportState TransportState = ExecutionTransportState.Connected,
        ExecutionSessionState SessionState = ExecutionSessionState.Unsupported,
        string? SessionId = null,
        DurableOutputState DurableOutputState = DurableOutputState.Published,
        string? DurableOutputReference = "refs/heads/runner/test/AGT-2185",
        int SameSessionResumeAttempts = 0,
        int FreshSalvageAttempts = 0)
        => new(
            "run-1",
            ExecutionAttemptKind.Coding,
            ProviderTerminalEvent,
            FinalAssistantOutput,
            StdOut,
            StdErr,
            ExitCode,
            Signal,
            LaunchFailed,
            TimedOut,
            OomKilled,
            OperatorCancelled,
            HostShutdown,
            LeaseLost,
            TransportState,
            SessionState,
            SessionId,
            DurableOutputState,
            DurableOutputReference,
            SameSessionResumeAttempts,
            FreshSalvageAttempts);
}
