using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Host;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

/// <summary>
/// Evaluates T4 against the committed plan, the candidate's own green gate and
/// Task Server evidence. The candidate is green when the post-merge store holds
/// a green run on it; a project outside post-merge mode never has one, so there
/// its exact-SHA pre-main or batch gate verdict counts instead (F2).
/// </summary>
public sealed class ReleaseCandidateGate(
    GitService git, ProjectRegistry projects, ProjectEnvironmentService environments,
    PostMergeRunStore postMerge, IHttpClientFactory clients, IConfiguration configuration,
    ReleaseCandidateEvidence evidence)
{
    public async Task<ReleaseCandidateGateResult> InspectAsync(string project, string commit, CancellationToken ct)
    {
        var record = projects.FindByIdOrDisplayName(project) ?? projects.FindByShortCode(project);
        if (record is null || string.IsNullOrWhiteSpace(commit) || commit.Length != 40 || !commit.All(Uri.IsHexDigit))
            return new("invalid", commit, "", null, false, "Unknown project or invalid candidate SHA.");
        return (await InspectAsync(record, commit, ct)) with { Project = record.DisplayName };
    }

    private async Task<ReleaseCandidateGateResult> InspectAsync(ProjectRecord record, string commit, CancellationToken ct)
    {
        const string fixDefinition = "Commit a valid .agent-studio/project.yml and environment declarations, "
            + "then release that commit.";
        var repo = git.ResolveRepoRootForProject(record.DisplayName);
        if (repo is null) return new("invalid", commit, "", null, false, "The project repository is unavailable.");
        var yaml = git.GetFileAtCommit(repo, commit, ProjectPreparationPaths.Definition);
        if (yaml is null) return new("invalid", commit, "", null, false, "The candidate has no project.yml.")
            { NextStep = fixDefinition };
        var read = ProjectDefinitionReader.Parse(yaml);
        if (!read.IsValid) return new("invalid", commit, "", null, false, "The candidate project.yml is invalid.")
            { NextStep = fixDefinition };
        IReadOnlyList<EnvironmentDeclaration>? declarations;
        try { declarations = environments.DeclarationsAtCommit(record.DisplayName, commit); }
        catch (JsonException)
        {
            return new("invalid", commit, "", null, false, "Candidate environments are invalid.")
                { NextStep = fixDefinition };
        }
        if (declarations is null) return new("invalid", commit, "", null, false, "Candidate environments are unavailable.")
            { NextStep = fixDefinition };
        var mode = ReleaseCandidateGatePolicy.Mode(read.Definition!.DeliveryFullSuite,
            declarations.Any(item => item.Kind == "production"));
        var script = git.GetFileAtCommit(repo, commit, "scripts/release/promotion-full-gate.sh") ?? "";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml + "\n" + script)))
            .ToLowerInvariant();
        if (mode == "off") return new(mode, commit, digest, null, true, null);
        var state = postMerge.Read(record.Id);
        var green = state.DevelopState == "green"
            && (string.Equals(state.LastGreenSha, commit, StringComparison.OrdinalIgnoreCase)
                || state.Runs.Any(run => run.Status == "green"
                    && string.Equals(run.TipSha, commit, StringComparison.OrdinalIgnoreCase)));
        var postMergeMode = evidence.InPostMergeMode(record);
        var candidate = ReleaseCandidateGatePolicy.Candidate(mode, green, postMergeMode,
            postMergeHistory: state.DevelopState is not null || state.Runs.Count > 0,
            () => evidence.IntegrationGateGreen(record, commit));
        if (candidate is null)
        {
            var refusal = ReleaseCandidateGatePolicy.NotGreenCandidate(postMergeMode);
            return new(mode, commit, digest, null, green, refusal.Error) { NextStep = refusal.NextStep };
        }
        if (!TaskServerPlaneProxy.IsConfigured(configuration))
            return new(mode, commit, digest, null, green, "Task Server verdict storage is unavailable.")
            {
                CandidateEvidence = candidate,
                NextStep = "Connect this installation to its Task Server, or set delivery.fullSuite: off "
                    + "in .agent-studio/project.yml.",
            };
        using var response = await clients.CreateClient(TaskServerPlaneProxy.ClientName).GetAsync(
            VerdictPath(record.DisplayName, commit, digest), ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
        var verdict = response.StatusCode == HttpStatusCode.NotFound ? null
            : await response.Content.ReadFromJsonAsync<ReleaseSuiteVerdict>(cancellationToken: ct);
        var allowed = ReleaseCandidateGatePolicy.Allows(mode, candidateGreen: true, verdict);
        var suite = allowed ? null : ReleaseCandidateGatePolicy.SuiteRefusal(verdict);
        return new(mode, commit, digest, verdict, green, suite?.Error)
            { CandidateEvidence = candidate, NextStep = suite?.NextStep };
    }

    public async Task<ReleaseSuiteVerdict> PublishAsync(ReleaseSuiteVerdict verdict, CancellationToken ct)
    {
        var plan = await InspectAsync(verdict.Project, verdict.CommitSha, ct);
        if (!plan.CandidateGreen || plan.Mode == "invalid" || plan.CommandDigest != verdict.CommandDigest)
            throw new ArgumentException("The verdict does not match a green release candidate and its command set.");
        if (verdict.Status is not ("green" or "red"))
            throw new ArgumentException("The verdict status must be green or red.");
        if (!TaskServerPlaneProxy.IsConfigured(configuration))
            throw new InvalidOperationException("Task Server verdict storage is unavailable.");
        var record = projects.FindByIdOrDisplayName(verdict.Project) ?? projects.FindByShortCode(verdict.Project);
        verdict = verdict with { Project = record!.DisplayName };
        using var response = await clients.CreateClient(TaskServerPlaneProxy.ClientName)
            .PutAsJsonAsync("/api/v1/release-suite-verdicts/", verdict, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReleaseSuiteVerdict>(cancellationToken: ct)
            ?? throw new IOException("Task Server returned an empty release suite verdict.");
    }

    private static string VerdictPath(string project, string sha, string digest)
        => $"/api/v1/release-suite-verdicts/{Uri.EscapeDataString(project)}/{sha}/{digest}";
}

/// <param name="PostMergeGreen">The post-merge store holds a green run on the candidate.</param>
public sealed record ReleaseCandidateGateResult(string Mode, string CommitSha, string CommandDigest,
    ReleaseSuiteVerdict? Verdict, bool PostMergeGreen, string? Error)
{
    public bool Allowed => Error is null;
    /// <summary>The registered display name; empty for an invalid request.</summary>
    public string Project { get; init; } = "";
    /// <summary>A <see cref="ReleaseCandidateEvidenceSources"/> value; null when the candidate is not green.</summary>
    public string? CandidateEvidence { get; init; }
    /// <summary>The candidate may receive a full-suite verdict; <c>off</c> needs none.</summary>
    public bool CandidateGreen => Mode == "off" || CandidateEvidence is not null;
    /// <summary>What the owner does next when <see cref="Error"/> refuses the release.</summary>
    public string? NextStep { get; init; }
}

public sealed record ReleaseCandidateRefusal(string Error, string NextStep);

public static class ReleaseCandidateGatePolicy
{
    public static string Mode(string? configured, bool productionDeclared)
        => configured ?? (productionDeclared ? "required" : "optional");

    public static bool Allows(string mode, bool candidateGreen, ReleaseSuiteVerdict? verdict)
        => mode == "off" || candidateGreen && (verdict?.Status == "green"
            || mode == "optional" && verdict is null);

    /// <summary>
    /// The evidence that makes the candidate green, or null. A green post-merge
    /// run always counts. In post-merge mode that run is the only proof, because
    /// it is the verdict on the merged tip. Outside post-merge mode the exact-SHA
    /// pre-main or batch gate verdict counts. <c>optional</c> also admits a
    /// candidate of a project that has no post-merge history at all.
    /// </summary>
    public static string? Candidate(string mode, bool postMergeGreen, bool postMergeMode,
        bool postMergeHistory, Func<string?> integrationGateGreen)
    {
        if (mode == "off") return null;
        if (postMergeGreen) return ReleaseCandidateEvidenceSources.PostMerge;
        if (!postMergeMode && integrationGateGreen() is { } source) return source;
        return mode == "optional" && !postMergeHistory ? ReleaseCandidateEvidenceSources.NoPostMergeHistory : null;
    }

    public static ReleaseCandidateRefusal NotGreenCandidate(bool postMergeMode) => postMergeMode
        ? new("The candidate must be the green post-merge tip before release.",
            "Wait for the post-merge run on this commit to finish green, or release the last green post-merge tip.")
        : new("The candidate has no green pre-main or batch gate on this exact commit.",
            "Release a commit that passed the pre-main or batch gate, or set delivery.fullSuite: off "
            + "in .agent-studio/project.yml to release without that check.");

    public static ReleaseCandidateRefusal SuiteRefusal(ReleaseSuiteVerdict? verdict) => verdict?.Status == "red"
        ? new("The candidate full suite is red.", "Fix the failing tests and release a newer commit.")
        : new("The candidate has no green full-suite verdict.",
            "Run scripts/release/promotion-full-gate.sh on this commit to record its verdict, or set "
            + "delivery.fullSuite: optional in .agent-studio/project.yml.");
}
