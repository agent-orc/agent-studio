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
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

/// <summary>Evaluates T4 against the committed plan, the post-merge tip and Task Server evidence.</summary>
public sealed class ReleaseCandidateGate(
    GitService git, ProjectRegistry projects, ProjectEnvironmentService environments,
    PostMergeRunStore postMerge, IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<ReleaseCandidateGateResult> InspectAsync(string project, string commit, CancellationToken ct)
    {
        var record = projects.FindByIdOrDisplayName(project) ?? projects.FindByShortCode(project);
        if (record is null || string.IsNullOrWhiteSpace(commit) || commit.Length != 40 || !commit.All(Uri.IsHexDigit))
            return new("invalid", commit, "", null, false, "Unknown project or invalid candidate SHA.");
        var repo = git.ResolveRepoRootForProject(record.DisplayName);
        if (repo is null) return new("invalid", commit, "", null, false, "The project repository is unavailable.");
        var yaml = git.GetFileAtCommit(repo, commit, ProjectPreparationPaths.Definition);
        if (yaml is null) return new("invalid", commit, "", null, false, "The candidate has no project.yml.");
        var read = ProjectDefinitionReader.Parse(yaml);
        if (!read.IsValid) return new("invalid", commit, "", null, false, "The candidate project.yml is invalid.");
        IReadOnlyList<EnvironmentDeclaration>? declarations;
        try { declarations = environments.DeclarationsAtCommit(record.DisplayName, commit); }
        catch (JsonException) { return new("invalid", commit, "", null, false, "Candidate environments are invalid."); }
        if (declarations is null) return new("invalid", commit, "", null, false, "Candidate environments are unavailable.");
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
        if (!green) return new(mode, commit, digest, null, false,
            "The candidate must be the green post-merge tip before release.");
        if (!TaskServerPlaneProxy.IsConfigured(configuration))
            return new(mode, commit, digest, null, false, "Task Server verdict storage is unavailable.");
        using var response = await clients.CreateClient(TaskServerPlaneProxy.ClientName).GetAsync(
            VerdictPath(record.DisplayName, commit, digest), ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
        var verdict = response.StatusCode == HttpStatusCode.NotFound ? null
            : await response.Content.ReadFromJsonAsync<ReleaseSuiteVerdict>(cancellationToken: ct);
        var allowed = ReleaseCandidateGatePolicy.Allows(mode, green, verdict);
        return new(mode, commit, digest, verdict, green,
            allowed ? null : verdict?.Status == "red" ? "The candidate full suite is red."
                : "The candidate has no green full-suite verdict.");
    }

    public async Task<ReleaseSuiteVerdict> PublishAsync(ReleaseSuiteVerdict verdict, CancellationToken ct)
    {
        var plan = await InspectAsync(verdict.Project, verdict.CommitSha, ct);
        if (!plan.PostMergeGreen || plan.Mode == "invalid" || plan.CommandDigest != verdict.CommandDigest)
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

public sealed record ReleaseCandidateGateResult(string Mode, string CommitSha, string CommandDigest,
    ReleaseSuiteVerdict? Verdict, bool PostMergeGreen, string? Error)
{
    public bool Allowed => Error is null;
}

public static class ReleaseCandidateGatePolicy
{
    public static string Mode(string? configured, bool productionDeclared)
        => configured ?? (productionDeclared ? "required" : "optional");

    public static bool Allows(string mode, bool postMergeGreen, ReleaseSuiteVerdict? verdict)
        => mode == "off" || postMergeGreen && (verdict?.Status == "green"
            || mode == "optional" && verdict is null);
}
