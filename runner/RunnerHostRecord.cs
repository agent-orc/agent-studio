using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// The one owned desired record of a runner host (AGT-W63 I03, D3 option A).
/// Role environment files and the resource profile are generated from it, so
/// host identity, role principals and the coding/review envelope are stated
/// once. Observed capabilities stay with the live probes; this is desire only.
/// </summary>
public sealed record RunnerHostRecord(
    int SchemaVersion,
    string HostId,
    string HostClass,
    string ServerUrl,
    string GitRemote,
    HostEnvelopeDto Envelope,
    IReadOnlyList<RunnerHostRoleRecord> Roles,
    string? GitPushRemote = null,
    RunnerHostWorkstation? Workstation = null,
    IReadOnlyDictionary<string, string>? Resources = null)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultPath = "/etc/agent-host/host.json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public EnrolHostRequest ToEnrolment(long expectedGeneration)
        => new(HostClass, (Roles ?? []).Select(role => new HostRolePrincipalDto(role.Role, role.PrincipalId)).ToArray(),
            Envelope, expectedGeneration);
}

/// <summary>One separately credentialed role service. The token file stays role-owned.</summary>
public sealed record RunnerHostRoleRecord(
    string Role,
    string PrincipalId,
    string TokenFile,
    string? ClientId = null,
    string? Name = null);

/// <summary>An ordinary workstation host: no systemd units, explicit roots and tools.</summary>
public sealed record RunnerHostWorkstation(IReadOnlyList<string> Roots, IReadOnlyList<string>? Tools = null);

public sealed record RunnerHostRoleService(string Role, string UnitName, string EnvFile, string ServiceRoot);

public sealed record RunnerHostRendering(IReadOnlyDictionary<string, string> Files, IReadOnlyList<RunnerHostRoleService> Services);

public sealed record RunnerHostMigration(RunnerHostRecord? Record, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes);

public static class RunnerHostRecordPolicy
{
    /// <summary>Static per-role unit naming; unchanged from the existing onboarding layout.</summary>
    public static RunnerHostRoleService Service(string role) => role switch
    {
        HostRoles.Coding => new(role, "agent-runner.service", "/etc/agent-runner/runner.env", "/var/lib/agent-runner"),
        HostRoles.Review => new(role, "agent-runner-review.service", "/etc/agent-runner/review.env", "/var/lib/agent-runner-review"),
        _ => throw new ArgumentException($"Unknown role '{role}'."),
    };

    public const string ProfilePath = "/etc/agent-host/profile.conf";

    private static readonly string[] ResourceKeys =
    [
        "CODING_CPU_QUOTA", "CODING_CPU_WEIGHT", "CODING_IO_WEIGHT", "CODING_MEMORY_MAX",
        "REVIEW_CPU_QUOTA", "REVIEW_CPU_WEIGHT", "REVIEW_IO_WEIGHT", "REVIEW_MEMORY_MAX",
    ];

    public static IReadOnlyList<string> Validate(RunnerHostRecord record)
    {
        var errors = new List<string>();
        if (record.SchemaVersion != RunnerHostRecord.CurrentSchemaVersion)
            errors.Add($"schemaVersion must be {RunnerHostRecord.CurrentSchemaVersion}.");
        if (HostEnrolmentPolicy.Validate(record.HostId ?? string.Empty, record.ToEnrolment(0)) is { } enrolment)
            errors.Add(enrolment);
        if (!Uri.TryCreate(record.ServerUrl, UriKind.Absolute, out _)) errors.Add("serverUrl must be an absolute URL.");
        if (string.IsNullOrWhiteSpace(record.GitRemote)) errors.Add("gitRemote is required.");
        var tokenFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in record.Roles ?? [])
        {
            if (string.IsNullOrWhiteSpace(role.TokenFile) || !role.TokenFile.StartsWith('/'))
                errors.Add($"Role '{role.Role}' needs an absolute tokenFile.");
            else if (!tokenFiles.Add(role.TokenFile))
                errors.Add("Each role keeps its own tokenFile; a shared file would merge credential scopes.");
        }
        foreach (var key in record.Resources?.Keys ?? [])
            if (!ResourceKeys.Contains(key, StringComparer.Ordinal))
                errors.Add($"Unknown resource key '{key}'.");
        if (record.Workstation is { } workstation && workstation.Roots.Count == 0)
            errors.Add("A workstation host must declare at least one root.");
        return errors;
    }

    /// <summary>Deterministic generated role environment and resource profile.</summary>
    public static RunnerHostRendering Render(RunnerHostRecord record)
    {
        var errors = Validate(record);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var digest = Digest(record);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var services = new List<RunnerHostRoleService>();
        foreach (var role in record.Roles.OrderBy(role => role.Role, StringComparer.Ordinal))
        {
            var service = Service(role.Role);
            services.Add(service);
            var lines = new List<string>
            {
                $"# Generated from {RunnerHostRecord.DefaultPath} (sha256:{digest}). Edit the record, not this file.",
                $"RUNNER_SERVER_URL={record.ServerUrl}",
            };
            if (!string.IsNullOrWhiteSpace(role.ClientId)) lines.Add($"RUNNER_CLIENT_ID={role.ClientId}");
            if (!string.IsNullOrWhiteSpace(role.Name)) lines.Add($"RUNNER_NAME={role.Name}");
            lines.AddRange(
            [
                $"RUNNER_ID={role.PrincipalId}",
                $"RUNNER_HOSTNAME={record.HostId}",
                $"RUNNER_ROLE={role.Role}",
                $"RUNNER_AUTH_TOKEN_FILE={role.TokenFile}",
                $"RUNNER_GIT_REMOTE={record.GitRemote}",
            ]);
            if (!string.IsNullOrWhiteSpace(record.GitPushRemote)) lines.Add($"RUNNER_GIT_PUSH_REMOTE={record.GitPushRemote}");
            lines.Add($"RUNNER_WORKDIR={service.ServiceRoot}/work");
            if (role.Role == HostRoles.Review) lines.Add($"RUNNER_REVIEW_WORKDIR={service.ServiceRoot}/review-work");
            lines.Add($"RUNNER_STATE_DIR={service.ServiceRoot}/state");
            lines.Add($"RUNNER_MAX_PARALLELISM={record.Envelope.RoleSlots(role.Role)}");
            // AGT-2866 declared only the other role's count so a hand edit could
            // not leave a stale own count. Both files now regenerate from one
            // record, so both counts are explicit and inherited values cannot leak.
            lines.Add($"RUNNER_HOST_CODING_SLOTS={record.Envelope.CodingSlots}");
            lines.Add($"RUNNER_HOST_REVIEW_SLOTS={record.Envelope.ReviewSlots}");
            if (record.Workstation is { } workstation)
            {
                lines.Add("RUNNER_WORKSTATION=1");
                lines.Add($"RUNNER_WORKSTATION_ROOTS={string.Join(Path.PathSeparator, workstation.Roots)}");
                if (workstation.Tools is { Count: > 0 } tools)
                    lines.Add($"RUNNER_WORKSTATION_TOOLS={string.Join(',', tools)}");
            }
            files[Path.GetFileName(service.EnvFile)] = string.Join('\n', lines) + "\n";
        }
        var profile = new List<string>
        {
            "# agent-host Linux resource profile.",
            $"# Generated from {RunnerHostRecord.DefaultPath} (sha256:{digest}).",
            $"HOST_TOTAL_SLOTS={record.Envelope.TotalSlots}",
            $"CODING_SLOTS={record.Envelope.CodingSlots}",
            $"REVIEW_SLOTS={record.Envelope.ReviewSlots}",
        };
        foreach (var key in ResourceKeys)
            if (record.Resources?.TryGetValue(key, out var value) == true) profile.Add($"{key}={value}");
        files[Path.GetFileName(ProfilePath)] = string.Join('\n', profile) + "\n";
        return new RunnerHostRendering(files, services);
    }

    /// <summary>
    /// Builds the desired record from existing runner.env/review.env/profile.conf.
    /// Disagreements are errors; the operator resolves them instead of the
    /// importer choosing one host fact silently.
    /// </summary>
    public static RunnerHostMigration Migrate(string? runnerEnv, string? reviewEnv, string? profileConf, string hostClass)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var sources = new List<(string Role, IReadOnlyDictionary<string, string> Values)>();
        if (runnerEnv is not null) sources.Add((HostRoles.Coding, ParseEnv(runnerEnv)));
        if (reviewEnv is not null) sources.Add((HostRoles.Review, ParseEnv(reviewEnv)));
        if (sources.Count == 0) return new(null, ["No runner.env or review.env was supplied."], notes);

        string? Shared(string key, bool required)
        {
            var values = sources
                .Select(source => source.Values.GetValueOrDefault(key))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (values.Length > 1) errors.Add($"{key} differs between role files: {string.Join(" vs ", values)}.");
            if (values.Length == 0 && required) errors.Add($"{key} is missing.");
            return values.FirstOrDefault();
        }

        var serverUrl = Shared("RUNNER_SERVER_URL", true);
        var gitRemote = Shared("RUNNER_GIT_REMOTE", true);
        var pushRemote = Shared("RUNNER_GIT_PUSH_REMOTE", false);
        var hostId = Shared("RUNNER_HOSTNAME", false);
        if (hostId is null)
        {
            hostId = Environment.MachineName;
            notes.Add($"RUNNER_HOSTNAME was absent; pinned the current machine name '{hostId}' so restarts keep this identity.");
        }

        var roles = new List<RunnerHostRoleRecord>();
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (role, values) in sources)
        {
            var declared = values.GetValueOrDefault("RUNNER_ROLE") ?? HostRoles.Coding;
            if (!string.Equals(declared, role, StringComparison.Ordinal))
                errors.Add($"{Path.GetFileName(Service(role).EnvFile)} declares RUNNER_ROLE={declared}.");
            var principal = values.GetValueOrDefault("RUNNER_ID");
            var token = values.GetValueOrDefault("RUNNER_AUTH_TOKEN_FILE");
            if (string.IsNullOrWhiteSpace(principal)) errors.Add($"{role} RUNNER_ID is missing.");
            if (string.IsNullOrWhiteSpace(token)) errors.Add($"{role} RUNNER_AUTH_TOKEN_FILE is missing; enrol a role principal first.");
            roles.Add(new RunnerHostRoleRecord(role, principal ?? string.Empty, token ?? string.Empty,
                values.GetValueOrDefault("RUNNER_CLIENT_ID"), values.GetValueOrDefault("RUNNER_NAME")));
            slots[role] = int.TryParse(values.GetValueOrDefault("RUNNER_MAX_PARALLELISM"), out var own) ? own : 2;
            var otherKey = role == HostRoles.Coding ? "RUNNER_HOST_REVIEW_SLOTS" : "RUNNER_HOST_CODING_SLOTS";
            var otherRole = role == HostRoles.Coding ? HostRoles.Review : HostRoles.Coding;
            if (int.TryParse(values.GetValueOrDefault(otherKey), out var declaredOther)
                && sources.Any(source => source.Role == otherRole)
                && slots.TryGetValue(otherRole, out var actualOther)
                && actualOther != declaredOther)
                notes.Add($"{otherKey}={declaredOther} in the {role} file disagreed with the {otherRole} file; the {otherRole} file wins.");
        }

        var profile = ParseEnv(profileConf ?? string.Empty);
        var resources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in profile)
        {
            if (ResourceKeys.Contains(key, StringComparer.Ordinal)) resources[key] = value;
            else if (key is not ("HOST_TOTAL_SLOTS" or "CODING_SLOTS" or "REVIEW_SLOTS"))
                notes.Add($"profile.conf key {key} is not owned by the host record and was dropped.");
        }

        var coding = slots.GetValueOrDefault(HostRoles.Coding);
        var review = slots.GetValueOrDefault(HostRoles.Review);
        foreach (var (key, roleSlots) in new[] { ("CODING_SLOTS", coding), ("REVIEW_SLOTS", review) })
        {
            if (!profile.TryGetValue(key, out var declared)) continue;
            if (!int.TryParse(declared, out var parsed) || parsed != roleSlots)
                errors.Add($"profile.conf {key} must match the role environment slot count ({roleSlots}).");
        }
        // Legacy profiles had no shared ceiling. A profile rendered from an
        // owned record does, and importing it must keep that tighter budget.
        var total = Math.Max(1, coding + review);
        if (profile.TryGetValue("HOST_TOTAL_SLOTS", out var declaredTotal))
        {
            if (!int.TryParse(declaredTotal, out total) || total < 1)
                errors.Add("profile.conf HOST_TOTAL_SLOTS must be a positive integer.");
        }
        var envelope = new HostEnvelopeDto(total, coding, review);
        notes.Add($"Envelope set to {envelope.TotalSlots} total slots (coding {coding}, review {review}); project parallelism is unchanged.");
        var record = new RunnerHostRecord(
            RunnerHostRecord.CurrentSchemaVersion,
            hostId,
            hostClass,
            serverUrl ?? string.Empty,
            gitRemote ?? string.Empty,
            envelope,
            roles,
            pushRemote,
            null,
            resources.Count == 0 ? null : resources);
        if (errors.Count == 0) errors.AddRange(Validate(record));
        return new RunnerHostMigration(errors.Count == 0 ? record : null, errors, notes);
    }

    public static string Digest(RunnerHostRecord record)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(record, RunnerHostRecord.Json))))[..16];

    internal static IReadOnlyDictionary<string, string> ParseEnv(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            values[line[..separator].Trim()] = value;
        }
        return values;
    }
}
