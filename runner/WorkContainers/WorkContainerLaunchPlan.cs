using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRunner.WorkContainers;

/// <summary>
/// One claimed attempt as the executor sees it. Lease, fence and authority
/// epoch come from the Task Server claim and are copied onto every container
/// and volume so a restarted host can reconcile them with authority.
/// </summary>
public sealed record WorkContainerAttempt(
    string HostId,
    string Role,
    string AttemptId,
    string LeaseId,
    long Fence,
    long AuthorityEpoch,
    string RepositoryKey,
    string RepositorySource,
    string ExactSha,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record WorkContainerMount(string Type, string Source, string Target, bool ReadOnly);

/// <summary>The complete, validated container launch for one attempt.</summary>
public sealed record WorkContainerLaunchPlan(
    string ContainerName,
    string AttemptVolume,
    string Image,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<WorkContainerMount> Mounts,
    IReadOnlyList<string> CreateArguments,
    WorkContainerLimits Limits,
    int Uid,
    IReadOnlyList<string> PrepareArguments)
{
    /// <summary>The one-shot ownership step's container, removed with the attempt.</summary>
    public string PrepareContainerName => ContainerName + "-prepare";
}

/// <summary>
/// Pure translation from attempt, image and admitted limits to the runtime
/// command line. Both Docker and Podman accept the same <c>create</c> flags.
/// </summary>
public static class WorkContainerLaunchPlanner
{
    public const string ManagedLabel = "agent-studio.work-container";
    public const string HostLabel = "agent-studio.host";
    public const string RoleLabel = "agent-studio.role";
    public const string AttemptLabel = "agent-studio.attempt";
    public const string LeaseLabel = "agent-studio.lease";
    public const string FenceLabel = "agent-studio.fence";
    public const string EpochLabel = "agent-studio.epoch";
    public const string GrantLabel = "agent-studio.grant-half-cores";
    public const string ImageLabel = "agent-studio.image";

    /// <summary>Non-root identity baked into every role image (see deploy/work-container/Dockerfile).
    /// SM-11: attempts run under their own <see cref="WorkContainerIdentity"/> UID instead.</summary>
    public const string ContainerUser = "10001:10001";
    public const string SecretDirectory = "/run/agent-secrets";
    public const string WorkDirectory = "/work";
    public const string SourceMountTarget = "/mnt/source";

    /// <summary>Exit code the entry script returns when HEAD is not the expected SHA.</summary>
    public const int ShaMismatchExitCode = 86;

    private static readonly Regex Sha = new("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Key = new("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A secret travels as a file in the tmpfs mount (DP-S5), never as a value.
    private static readonly string[] SecretNameFragments = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "API_KEY", "PRIVATE_KEY", "CREDENTIAL"];

    // Host paths that would hand the container a runtime, a login or a key.
    private static readonly string[] ForbiddenSourceFragments =
    [
        "docker.sock", "podman.sock", "containerd.sock", "/.docker", "/.codex", "/.claude", "/.config/gh",
        "/.ssh", "/.git-credentials", "/.gnupg", "/.aws", "/.kube",
    ];

    /// <summary>
    /// SM-11: caches are per repository. One shared mirror volume would show
    /// every attempt the mirrors, and so the source, of every other project.
    /// </summary>
    public static string CacheVolume(string cache, string repositoryKey, int version)
        => $"agent-cache-{cache}-{repositoryKey}-v{version}";

    public static WorkContainerLaunchPlan Plan(
        WorkContainerAttempt attempt, string image, WorkContainerLimits limits, WorkContainerSettings settings,
        bool credentialRequired = false, int? uid = null)
    {
        Validate(attempt, image);
        var attemptUid = uid ?? WorkContainerIdentity.Allocate(attempt.AttemptId, attempt.Fence, new HashSet<int>());
        if (!WorkContainerIdentity.IsAttemptUid(attemptUid))
            throw new ArgumentException("A work container runs under an attempt UID.");
        var user = WorkContainerIdentity.User(attemptUid);
        var uidText = attemptUid.ToString(CultureInfo.InvariantCulture);
        var stem = $"agent-work-{Sanitize(attempt.AttemptId)}-f{attempt.Fence.ToString(CultureInfo.InvariantCulture)}";
        var labels = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedLabel] = "1",
            [HostLabel] = attempt.HostId,
            [RoleLabel] = attempt.Role,
            [AttemptLabel] = attempt.AttemptId,
            [LeaseLabel] = attempt.LeaseId,
            [FenceLabel] = attempt.Fence.ToString(CultureInfo.InvariantCulture),
            [EpochLabel] = attempt.AuthorityEpoch.ToString(CultureInfo.InvariantCulture),
            [GrantLabel] = limits.HalfCores.ToString(CultureInfo.InvariantCulture),
            [ImageLabel] = image,
            [WorkContainerIdentity.UidLabel] = uidText,
        };

        var mounts = new List<WorkContainerMount>
        {
            new("volume", stem, WorkDirectory, false),
            new("volume", CacheVolume("npm", attempt.RepositoryKey, settings.CacheVersion), "/cache/npm", false),
            new("volume", CacheVolume("nuget", attempt.RepositoryKey, settings.CacheVersion), "/cache/nuget", false),
            new("volume", CacheVolume("repo", attempt.RepositoryKey, settings.CacheVersion), "/cache/repo", false),
        };
        var source = attempt.RepositorySource;
        if (Path.IsPathRooted(source))
        {
            mounts.Add(new("bind", Path.GetFullPath(source), SourceMountTarget, true));
            source = SourceMountTarget;
        }
        foreach (var mount in mounts) ValidateMount(mount);

        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = $"{WorkDirectory}/home",
            ["npm_config_cache"] = "/cache/npm",
            ["NUGET_PACKAGES"] = "/cache/nuget",
            ["DOTNET_CLI_HOME"] = $"{WorkDirectory}/home",
            ["DOTNET_PROCESSOR_COUNT"] = limits.ProcessorHint.ToString(CultureInfo.InvariantCulture),
            ["AGENT_SECRET_DIR"] = SecretDirectory,
            ["AGENT_EXPECTED_SHA"] = attempt.ExactSha,
            ["AGENT_REPOSITORY_KEY"] = attempt.RepositoryKey,
            ["AGENT_REPOSITORY_SOURCE"] = source,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        };
        foreach (var (name, value) in WorkerBuildServerHygiene.Variables) environment[name] = value;
        foreach (var (name, value) in attempt.Environment ?? new Dictionary<string, string>())
        {
            ValidateEnvironment(name);
            environment[name] = value;
        }
        // SM-11: the repository mirror belongs to an earlier attempt UID and the
        // bound source to the host, so Git's ownership check needs them named.
        // Command-line configuration is protected; GIT_CONFIG_NOSYSTEM drops the
        // image's own entry.
        var gitConfig = new List<(string Key, string Value)>
        {
            ("safe.directory", SourceMountTarget),
            ("safe.directory", $"/cache/repo/{attempt.RepositoryKey}.git"),
        };
        if (credentialRequired)
        {
            if (!attempt.RepositorySource.StartsWith("https://github.com/", StringComparison.Ordinal)
                || !attempt.RepositorySource.EndsWith(".git", StringComparison.Ordinal))
                throw new ArgumentException("Brokered Git credentials require a canonical GitHub HTTPS source.");
            environment["AGENT_SECRET_REQUIRED"] = "1";
            gitConfig.Add(("credential.helper", "/usr/local/bin/agent-git-credential-helper"));
            gitConfig.Add(("credential.useHttpPath", "true"));
        }
        environment["GIT_CONFIG_COUNT"] = gitConfig.Count.ToString(CultureInfo.InvariantCulture);
        for (var index = 0; index < gitConfig.Count; index++)
        {
            environment[$"GIT_CONFIG_KEY_{index}"] = gitConfig[index].Key;
            environment[$"GIT_CONFIG_VALUE_{index}"] = gitConfig[index].Value;
        }

        var arguments = new List<string>
        {
            "create", "--name", stem,
            "--user", user,
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            // A daemon restart must never revive a fenced attempt.
            "--restart", "no",
            "--cpus", limits.Cpus,
            "--memory", limits.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--memory-swap", limits.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--pids-limit", limits.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--network", settings.Network,
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=1g",
            "--tmpfs", $"{SecretDirectory}:rw,noexec,nosuid,nodev,size=1m,mode=0700,uid={uidText},gid={WorkContainerIdentity.GroupId}",
            "--workdir", WorkDirectory,
        };
        foreach (var (name, value) in labels) arguments.AddRange(["--label", $"{name}={value}"]);
        foreach (var mount in mounts)
            arguments.AddRange(["--mount",
                $"type={mount.Type},source={mount.Source},target={mount.Target}{(mount.ReadOnly ? ",readonly" : "")}"]);
        foreach (var (name, value) in environment) arguments.AddRange(["--env", $"{name}={value}"]);
        arguments.Add(image);
        arguments.AddRange(["/bin/sh", "-c", EntryScript, "agent-work"]);
        arguments.AddRange(attempt.Command);

        // Root without network and with CHOWN and FOWNER only: hand the fresh
        // attempt volume to the attempt UID and keep the repository caches
        // group-writable for the next attempt of the same repository.
        var prepare = new List<string>
        {
            "run", "--rm", "--user", "0:0", "--network", "none", "--read-only", "--cap-drop", "ALL",
            "--cap-add", "CHOWN", "--cap-add", "FOWNER", "--security-opt", "no-new-privileges",
            "--name", stem + "-prepare",
        };
        // No grant label: the ownership step reserves no share of the host grant.
        foreach (var (name, value) in labels.Where(label => label.Key != GrantLabel))
            prepare.AddRange(["--label", $"{name}={value}"]);
        foreach (var mount in mounts.Where(mount => mount.Type == "volume"))
            prepare.AddRange(["--mount", $"type=volume,source={mount.Source},target={mount.Target}"]);
        prepare.AddRange([image, "/bin/sh", "-c", PrepareScript, "agent-prepare", uidText]);

        return new WorkContainerLaunchPlan(stem, stem, image, labels, mounts, arguments, limits, attemptUid, prepare);
    }

    /// <summary>Runs as root in the prepare container; its only input is the attempt UID.</summary>
    public const string PrepareScript = """
        set -eu
        chown "$1:10001" /work
        chmod 0700 /work
        for cache in /cache/npm /cache/nuget /cache/repo; do
          chgrp 10001 "$cache"
          chmod 2770 "$cache"
        done
        """;

    /// <summary>Writes stdin into the private tmpfs as the container's own user (SM-11 attempt UID).</summary>
    public const string InjectTokenScript =
        "umask 077; cat > /run/agent-secrets/.github-token-next && mv /run/agent-secrets/.github-token-next /run/agent-secrets/github-token";

    /// <summary>
    /// Runs inside the container before the work command: materialize the
    /// repository from the versioned mirror cache, verify the exact SHA, and
    /// report cache state. A mirror that cannot supply the SHA is quarantined
    /// and rebuilt from source. Markers are parsed from the flushed log.
    /// </summary>
    public const string EntryScript = """
        set -eu
        # Group-writable files keep the repository caches usable by the next attempt UID.
        umask 0007
        if [ "${AGENT_SECRET_REQUIRED:-0}" = 1 ]; then
          attempts=0
          until [ -r "$AGENT_SECRET_DIR/github-token" ]; do
            attempts=$((attempts + 1))
            if [ "$attempts" -ge 300 ]; then echo "AGENT_SECRET_UNAVAILABLE"; exit 87; fi
            sleep 0.1
          done
        fi
        echo "AGENT_PHASE materialize"
        mirror="/cache/repo/$AGENT_REPOSITORY_KEY.git"
        # The entry shell is PID 1 in every container, so its process ID is not unique across attempts.
        unique="$(cat /proc/sys/kernel/random/uuid)"
        has_sha() { [ -d "$mirror" ] && git -C "$mirror" cat-file -e "$AGENT_EXPECTED_SHA^{commit}" 2>/dev/null; }
        if has_sha; then echo "AGENT_CACHE repo hit"
        else
          [ ! -d "$mirror" ] || git -C "$mirror" fetch --quiet "$AGENT_REPOSITORY_SOURCE" '+refs/heads/*:refs/heads/*' '+refs/tags/*:refs/tags/*' 2>/dev/null || true
          if has_sha; then echo "AGENT_CACHE repo refresh"
          else
            if [ -d "$mirror" ]; then mv "$mirror" "$mirror.quarantine.$unique"; echo "AGENT_CACHE repo quarantined"; fi
            tmp="$mirror.tmp.$unique"
            git clone --quiet --mirror "$AGENT_REPOSITORY_SOURCE" "$tmp"
            if [ -d "$mirror" ]; then rm -rf "$tmp"; else mv "$tmp" "$mirror"; fi
            echo "AGENT_CACHE repo miss"
          fi
        fi
        # --shared borrows mirror objects; the attempt volume holds only the checkout.
        git clone --quiet --shared --no-checkout "$mirror" /work/repo
        if ! git -C /work/repo -c advice.detachedHead=false checkout --quiet --detach "$AGENT_EXPECTED_SHA" 2>/dev/null; then
          echo "AGENT_SHA_MISMATCH expected=$AGENT_EXPECTED_SHA actual=unavailable"
          exit 86
        fi
        actual="$(git -C /work/repo rev-parse HEAD)"
        if [ "$actual" != "$AGENT_EXPECTED_SHA" ]; then
          echo "AGENT_SHA_MISMATCH expected=$AGENT_EXPECTED_SHA actual=$actual"
          exit 86
        fi
        echo "AGENT_SHA_VERIFIED $actual"
        mkdir -p "$HOME"
        for cache in npm nuget; do
          if [ -f "/cache/$cache/.agent-cache" ]; then echo "AGENT_CACHE $cache hit"; else echo "AGENT_CACHE $cache miss"; fi
        done
        echo "AGENT_PHASE first-command"
        cd /work/repo
        set +e
        "$@"
        status=$?
        set -e
        if [ "$status" -eq 0 ]; then
          for cache in npm nuget; do
            [ -z "$(ls -A "/cache/$cache" 2>/dev/null)" ] || touch "/cache/$cache/.agent-cache"
          done
        fi
        echo "AGENT_PHASE exit $status"
        exit "$status"
        """;

    public static void Validate(WorkContainerAttempt attempt, string image)
    {
        if (!RoleImageManifest.IsPinned(image))
            throw new ArgumentException("Work container image must be digest pinned.");
        if (!RoleImageManifest.KnownRoles.Contains(attempt.Role, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown work container role '{attempt.Role}'.");
        if (string.IsNullOrWhiteSpace(attempt.AttemptId) || string.IsNullOrWhiteSpace(attempt.LeaseId)
            || string.IsNullOrWhiteSpace(attempt.HostId))
            throw new ArgumentException("Attempt, lease and host identity are required.");
        if (attempt.Fence <= 0) throw new ArgumentException("A positive fence is required.");
        if (!Sha.IsMatch(attempt.ExactSha))
            throw new ArgumentException("An exact lowercase commit SHA is required.");
        if (!Key.IsMatch(attempt.RepositoryKey))
            throw new ArgumentException("Repository key must be a lowercase cache key.");
        if (attempt.Command.Count == 0) throw new ArgumentException("A work command is required.");
        if (Path.IsPathRooted(attempt.RepositorySource)
            && !attempt.RepositorySource.TrimEnd('/').EndsWith(".git", StringComparison.Ordinal))
            throw new ArgumentException("A local repository source must be a bare repository path ending in .git.");
    }

    public static void ValidateMount(WorkContainerMount mount)
    {
        if (mount.Type == "bind")
        {
            var source = mount.Source.Replace('\\', '/');
            if (!mount.ReadOnly)
                throw new ArgumentException("Host bind mounts into a work container must be read-only.");
            if (source is "/" or "/home" or "/root" or "/etc" or "/var/run" or "/run" or "/proc" or "/sys" or "/dev"
                || source.StartsWith("/run/", StringComparison.Ordinal)
                || source.StartsWith("/var/run/", StringComparison.Ordinal)
                || source.StartsWith("/proc/", StringComparison.Ordinal)
                || source.EndsWith(".sock", StringComparison.Ordinal)
                || ForbiddenSourceFragments.Any(fragment => source.Contains(fragment, StringComparison.Ordinal)))
                throw new ArgumentException($"Host path '{mount.Source}' may not be mounted into a work container.");
        }
        else if (mount.Type != "volume")
            throw new ArgumentException($"Unsupported mount type '{mount.Type}'.");
        if (mount.Target.StartsWith(SecretDirectory, StringComparison.Ordinal))
            throw new ArgumentException("The secret directory is a tmpfs mount only.");
    }

    public static void ValidateEnvironment(string name)
    {
        if (!EnvironmentName.IsMatch(name))
            throw new ArgumentException($"Invalid environment name '{name}'.");
        var upper = name.ToUpperInvariant();
        if (WorkerEdgeCredentialBoundary.IsProtectedName(name)
            || upper == ProviderAuthEnvironment.ClaudeCodeOAuthToken
            || SecretNameFragments.Any(fragment => upper.Contains(fragment, StringComparison.Ordinal)))
            throw new ArgumentException($"Environment '{name}' looks like a secret; secrets reach a work container only as files.");
    }

    internal static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
            builder.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' ? ch : '-');
        var text = builder.ToString().Trim('-', '.', '_');
        if (text.Length == 0) text = "attempt";
        if (text.Length > 64)
        {
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
            text = $"{text[..55]}-{digest}";
        }
        return text;
    }
}
