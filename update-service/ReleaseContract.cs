using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentTaskboard.UpdateService;

public sealed record ReleaseArtifact(
    string Name,
    string Version,
    string Tag,
    string Commit,
    string Integrity);

public sealed record ReleaseManifest(
    int SchemaVersion,
    string Application,
    string Tag,
    string Version,
    string Commit,
    bool Dirty,
    DateTimeOffset? BuiltAt,
    string Integrity,
    ReleaseArtifact CodingAgentRunner,
    ReleaseArtifact CodingAgentChat,
    bool Legacy = false);

public enum ReleaseDirection
{
    SameVersion,
    Upgrade,
    Downgrade,
    Divergence,
    Unknown
}

public sealed record ReleaseComparison(
    bool Allowed,
    ReleaseDirection Direction,
    string Summary,
    IReadOnlyList<string> Errors,
    ReleaseManifest? Running,
    ReleaseManifest? Installed,
    ReleaseManifest? Candidate,
    string? LatestApprovedTag,
    bool Offline,
    // Populated by ReleasePreflightService with operator-facing context for
    // either a running/installed divergence refusal or the normal
    // UpgradeInVerification window. It may include matching run history; the
    // pure Compare below performs no operator-facing enrichment.
    string? DivergenceExplanation = null,
    // True while a backend already reports the candidate identity and the
    // checkout root still carries the previous manifest. That is the normal
    // window between the Update Service's restart and its manifest commit,
    // not a divergence. See StableReleaseContract.Compare.
    bool UpgradeInVerification = false,
    // AGT-2865: what the post-restart verification matrix needs from the
    // instance that is running right now. Filled by ReleasePreflightService
    // (the pure Compare below has no way to reach the backend); a blocking
    // failure here is already folded into Allowed and Errors.
    IReadOnlyList<VerificationPreconditionResult>? VerificationPreconditions = null);

/// <summary>
/// Pure Stable release gate. It never uses filesystem timestamps and can run
/// with a cached approved tag when the network is unavailable.
/// </summary>
public static class StableReleaseContract
{
    private static readonly Regex VersionPattern = new("^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:[-+].*)?$", RegexOptions.Compiled);
    private static readonly Regex TagPattern = new("^v[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    public static ReleaseManifest Read(string json)
    {
        var value = JsonSerializer.Deserialize<ReleaseManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return value ?? throw new InvalidDataException("Release manifest is empty.");
    }

    public static ReleaseComparison Compare(
        ReleaseManifest? running,
        ReleaseManifest? installed,
        ReleaseManifest? candidate,
        string? latestApprovedTag,
        bool offline,
        bool allowDowngrade = false)
    {
        var errors = new List<string>();
        Validate(candidate, "candidate", errors, allowLegacy: false);
        Validate(installed, "installed", errors, allowLegacy: true);
        Validate(running, "running", errors, allowLegacy: true);

        if (string.IsNullOrWhiteSpace(latestApprovedTag))
            errors.Add(offline
                ? "cached latest approved tag is missing"
                : "latest approved tag is missing");
        else if (candidate is not null && !string.Equals(candidate.Tag, latestApprovedTag, StringComparison.Ordinal))
            errors.Add($"candidate tag {candidate.Tag} does not equal latest approved tag {latestApprovedTag}");

        // The Update Service hands the intended manifest to the backend it
        // restarts (ATP_BUILD_MANIFEST -> <run folder>/intended-build-manifest.json)
        // and only commits that manifest into the checkout root once restart,
        // health, runtime identity, and the frontend port have been verified.
        // Between those two points the running backend legitimately reports
        // the candidate while the root still carries the previous release.
        // That state is "upgrade in verification", not "running identity
        // diverges from the installed manifest": a divergence is a running
        // identity that matches neither side of the release being applied.
        var upgradeInVerification =
            running is not null && installed is not null && candidate is not null
            && !IdentityEquals(running, installed)
            && IdentityEquals(running, candidate);

        if (running is not null && installed is not null && !IdentityEquals(running, installed)
            && !upgradeInVerification)
            errors.Add("running identity diverges from the installed manifest");

        var direction = installed?.Legacy == true && candidate is not null
            ? ReleaseDirection.Upgrade
            : Classify(installed?.Version, candidate?.Version, installed?.Commit, candidate?.Commit);
        if (direction == ReleaseDirection.SameVersion && installed is not null && candidate is not null
            && !IdentityEquals(installed, candidate))
            direction = ReleaseDirection.Divergence;
        if (direction == ReleaseDirection.Downgrade && !allowDowngrade)
            errors.Add("downgrade requires explicit approval");
        if (direction == ReleaseDirection.Divergence)
            errors.Add("same version points at different release artifacts");

        return new ReleaseComparison(
            errors.Count == 0,
            direction,
            Summary(direction, offline, upgradeInVerification),
            errors,
            running,
            installed,
            candidate,
            latestApprovedTag,
            offline,
            DivergenceExplanation: null,
            UpgradeInVerification: upgradeInVerification);
    }

    public static bool IdentityEquals(ReleaseManifest left, ReleaseManifest right) =>
        left == right;

    /// <summary>
    /// Proves that the dependency identities declared by a candidate manifest
    /// are the identities selected by that candidate commit's project release
    /// definition. Callers must supply source files read from
    /// <c>manifest.Commit</c>, never from the mutable working tree.
    /// </summary>
    public static IReadOnlyList<string> ValidateCandidateDependencies(
        ReleaseManifest manifest,
        ProjectExecutionDefinition definition,
        IReadOnlyDictionary<string, string> sourceFiles)
    {
        var errors = new List<string>();
        if (definition.Release is null)
        {
            errors.Add("candidate .agent-studio/project.yml does not declare a release contract");
            return errors;
        }

        ValidateIdentity(manifest.CodingAgentRunner, "CodingAgentRunner", definition.Release, sourceFiles, errors);
        ValidateIdentity(manifest.CodingAgentChat, "coding-agent-chat", definition.Release, sourceFiles, errors);
        return errors;
    }

    private static void ValidateIdentity(
        ReleaseArtifact artifact,
        string package,
        ProjectReleaseDefinition release,
        IReadOnlyDictionary<string, string> sourceFiles,
        List<string> errors)
    {
        var rule = release.Identity.FirstOrDefault(candidate =>
            string.Equals(candidate.Package, package, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            errors.Add($"candidate release identity rule for {package} is missing from .agent-studio/project.yml");
            return;
        }

        if (!rule.UsesLockFile)
        {
            CompareRuleValue(artifact.Version, rule.Version, $"candidate {package} version", errors);
            CompareRuleValue(artifact.Integrity, rule.Integrity, $"candidate {package} integrity", errors);
            return;
        }

        if (!sourceFiles.TryGetValue(rule.Source!, out var source))
        {
            errors.Add($"candidate release identity source {rule.Source} could not be read from the tagged commit");
            return;
        }

        if (string.Equals(rule.Ecosystem, "nuget", StringComparison.Ordinal))
            ValidateNugetIdentity(artifact, rule, source, errors);
        else if (string.Equals(rule.Ecosystem, "npm", StringComparison.Ordinal))
            ValidateNpmIdentity(artifact, rule, source, sourceFiles, errors);
        else
            errors.Add($"candidate release identity {package} has unsupported ecosystem {rule.Ecosystem}");
    }

    private static void ValidateNugetIdentity(
        ReleaseArtifact artifact,
        ProjectReleaseIdentityRule rule,
        string source,
        List<string> errors)
    {
        try
        {
            using var nuget = JsonDocument.Parse(source);
            JsonElement? lockedPackage = null;
            if (nuget.RootElement.TryGetProperty("dependencies", out var frameworks))
            {
                foreach (var framework in frameworks.EnumerateObject())
                {
                    if (framework.Value.TryGetProperty(rule.Package, out var candidate))
                    {
                        lockedPackage = candidate;
                        break;
                    }
                }
            }

            if (lockedPackage is null)
            {
                errors.Add($"candidate {rule.Package} is missing from {rule.Source}");
            }
            else
            {
                var value = lockedPackage.Value;
                CompareRuleValue(artifact.Version, ReadString(value, "resolved"),
                    $"candidate {rule.Package} version", errors);
                var contentHash = ReadString(value, "contentHash");
                CompareRuleValue(artifact.Integrity,
                    string.IsNullOrWhiteSpace(contentHash) ? null : $"sha512-{contentHash}",
                    $"candidate {rule.Package} integrity", errors);
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"candidate {rule.Source} is invalid: {ex.Message}");
        }
    }

    private static void ValidateNpmIdentity(
        ReleaseArtifact artifact,
        ProjectReleaseIdentityRule rule,
        string source,
        IReadOnlyDictionary<string, string> sourceFiles,
        List<string> errors)
    {
        var packageJsonPath = SiblingPath(rule.Source!, "package.json");
        if (!sourceFiles.TryGetValue(packageJsonPath, out var packageJson))
            errors.Add($"candidate {packageJsonPath} could not be read from the tagged commit");
        else
        {
            try
            {
                using var package = JsonDocument.Parse(packageJson);
                var spec = package.RootElement.TryGetProperty("dependencies", out var dependencies)
                    ? ReadString(dependencies, rule.Package)
                    : null;
                if (string.IsNullOrWhiteSpace(spec))
                    errors.Add($"candidate {rule.Package} is missing from {packageJsonPath}");
                else if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"candidate {rule.Package} still resolves from a local file: dist artifact");
                else
                    CompareRuleValue(artifact.Version, spec, $"candidate {rule.Package} package.json version", errors);
            }
            catch (JsonException ex)
            {
                errors.Add($"candidate {packageJsonPath} is invalid: {ex.Message}");
            }
        }

        try
        {
            using var packageLock = JsonDocument.Parse(source);
            if (!packageLock.RootElement.TryGetProperty("packages", out var packages)
                || !packages.TryGetProperty($"node_modules/{rule.Package}", out var lockedPackage))
            {
                errors.Add($"candidate {rule.Package} is missing from {rule.Source}");
                return;
            }
            var resolved = ReadString(lockedPackage, "resolved");
            if (string.IsNullOrWhiteSpace(resolved)
                || resolved.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                errors.Add($"candidate {rule.Package} lock entry is not an immutable registry artifact");
            CompareRuleValue(artifact.Version, ReadString(lockedPackage, "version"),
                $"candidate {rule.Package} locked version", errors);
            CompareRuleValue(artifact.Integrity, ReadString(lockedPackage, "integrity"),
                $"candidate {rule.Package} integrity", errors);
        }
        catch (JsonException ex)
        {
            errors.Add($"candidate {rule.Source} is invalid: {ex.Message}");
        }
    }

    private static string SiblingPath(string path, string fileName)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? fileName : $"{path[..slash]}/{fileName}";
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    private static void CompareRuleValue(string declared, string? expected, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(expected))
            errors.Add($"{label} is missing from its release identity source");
        else if (!string.Equals(declared, expected, StringComparison.Ordinal))
            errors.Add($"{label} mismatch (manifest={declared}, rule={expected})");
    }

    private static void Validate(ReleaseManifest? manifest, string name, List<string> errors, bool allowLegacy)
    {
        if (manifest is null)
        {
            errors.Add($"{name} build manifest is missing");
            return;
        }
        if (manifest.SchemaVersion != 1) errors.Add($"{name} schemaVersion is unsupported");
        if (manifest.Legacy && allowLegacy)
        {
            if (string.IsNullOrWhiteSpace(manifest.Commit) || manifest.Commit == "unknown")
                errors.Add($"{name} legacy migration commit is missing");
            return;
        }
        if (manifest.Legacy || string.IsNullOrWhiteSpace(manifest.Tag) || manifest.Tag == "untagged")
            errors.Add($"{name} immutable release tag is missing");
        if (manifest.Dirty) errors.Add($"{name} build is dirty");
        if (!string.Equals(manifest.Application, "Agent Studio", StringComparison.Ordinal))
            errors.Add($"{name} application identity is invalid");
        if (manifest.BuiltAt is null) errors.Add($"{name} build time is missing");
        if (!TagPattern.IsMatch(manifest.Tag ?? "")) errors.Add($"{name} release tag is invalid");
        if (!TagMatchesVersion(manifest.Tag, manifest.Version))
            errors.Add($"{name} tag/version mismatch ({manifest.Tag} vs {manifest.Version})");
        ValidateArtifact(manifest.CodingAgentRunner, $"{name} CodingAgentRunner", "CodingAgentRunner", errors);
        ValidateArtifact(manifest.CodingAgentChat, $"{name} Coding Agent Chat", "coding-agent-chat", errors);
        if (!IsIntegrity(manifest.Integrity)) errors.Add($"{name} application integrity is missing or invalid");
    }

    private static void ValidateArtifact(ReleaseArtifact? artifact, string name, string expectedName, List<string> errors)
    {
        if (artifact is null)
        {
            errors.Add($"{name} package identity is missing");
            return;
        }
        if (!string.Equals(artifact.Name, expectedName, StringComparison.Ordinal))
            errors.Add($"{name} package name mismatch ({artifact.Name} vs {expectedName})");
        if (string.IsNullOrWhiteSpace(artifact.Version) || string.IsNullOrWhiteSpace(artifact.Commit)
            || string.IsNullOrWhiteSpace(artifact.Tag) || artifact.Tag == "untagged")
            errors.Add($"{name} version/tag/commit is incomplete");
        if (!TagMatchesVersion(artifact.Tag, artifact.Version))
            errors.Add($"{name} tag/version mismatch ({artifact.Tag} vs {artifact.Version})");
        if (!IsIntegrity(artifact.Integrity)) errors.Add($"{name} integrity is missing or invalid");
    }

    private static bool TagMatchesVersion(string? tag, string? version)
    {
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(version)) return false;
        var normalizedTag = tag.StartsWith('v') ? tag[1..] : tag;
        return string.Equals(normalizedTag, version, StringComparison.Ordinal);
    }

    private static bool IsIntegrity(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase));

    private static ReleaseDirection Classify(string? installed, string? candidate, string? installedCommit, string? candidateCommit)
    {
        if (!TryVersion(installed, out var from) || !TryVersion(candidate, out var to)) return ReleaseDirection.Unknown;
        var cmp = to.CompareTo(from);
        if (cmp > 0) return ReleaseDirection.Upgrade;
        if (cmp < 0) return ReleaseDirection.Downgrade;
        return string.Equals(installedCommit, candidateCommit, StringComparison.OrdinalIgnoreCase)
            ? ReleaseDirection.SameVersion
            : ReleaseDirection.Divergence;
    }

    private static bool TryVersion(string? value, out Version version)
    {
        version = new Version();
        var match = VersionPattern.Match(value ?? "");
        return match.Success && Version.TryParse(
            $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}", out version!);
    }

    private static string Summary(ReleaseDirection direction, bool offline, bool upgradeInVerification) =>
        $"{direction switch
        {
            ReleaseDirection.SameVersion => "same version",
            ReleaseDirection.Upgrade => "upgrade",
            ReleaseDirection.Downgrade => "downgrade",
            ReleaseDirection.Divergence => "divergence",
            _ => "comparison unavailable"
        }}{(upgradeInVerification ? " (in verification, manifest not committed yet)" : "")}{(offline ? " (offline, cached approval)" : "")}";
}
