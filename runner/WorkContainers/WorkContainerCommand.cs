using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentRunner.WorkContainers;

/// <summary>
/// <c>agent-host work-container</c>: host-agent operations for the DP-S3
/// executor. <c>run</c> executes one claimed attempt in canary mode,
/// <c>reconcile</c> removes orphans after a restart, <c>image-promote</c> and
/// <c>image-rollback</c> maintain the pinned role image manifest, and
/// <c>proof</c> records the acceptance and go/no-go evidence.
/// </summary>
public static class WorkContainerCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string Usage = """
        Usage: agent-host work-container <command> [options]
          run --attempt <attempt.json> --grant-half-cores <n> --allocation-version <v> [--acknowledged-version <v>] [--image <ref>]
              [--broker-url <url> --broker-bearer-file <file> --runner-id <id> --instance-id <id> --subject-id <id>]
          reconcile --host <host-id> --inventory <host-agent attempt-inventory.json>
          kubernetes-run --attempt <attempt.json> --grant-half-cores <n> --allocation-version <v> --acknowledged-version <v>
              --broker-url <url> --broker-bearer-file <file> --runner-id <id> --instance-id <id> --subject-id <id>
          kubernetes-reconcile --host <host-id> --inventory <host-agent attempt-inventory.json>
          image-promote --manifest <path> --role <coding|review|gate|all> --image <ref@sha256:...> [--release <version>]
          image-rollback --manifest <path> --role <coding|review|gate|all>
          proof --host <host-id> --image <ref> --source <bare.git> --sha <sha> --report <dir>
                [--attempts 100] [--parallel 4] [--grant-half-cores 3] [--repository-key <key>] [--gate-script <sh>] [--image-layers-local true]
        Common: --state-dir <dir> (default $RUNNER_STATE_DIR/work-containers)
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            error.WriteLine(Usage);
            return 2;
        }
        Dictionary<string, string> options;
        try { options = ParseOptions(args[1..]); }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(Usage);
            return 2;
        }
        try
        {
            switch (args[0])
            {
                case "kubernetes-run":
                case "kubernetes-reconcile":
                    return await KubernetesAsync(args[0], options, output, ct);
                case "image-promote":
                case "image-rollback":
                    return UpdateManifest(args[0], options, output);
                case "reconcile":
                {
                    var executor = Executor(options, output);
                    var inventory = JsonSerializer.Deserialize<HostAttemptInventory>(
                        await File.ReadAllTextAsync(Required(options, "inventory"), ct), Json)
                        ?? throw new InvalidDataException("Attempt inventory is empty.");
                    // The host-agent inventory lists only attempts that still hold a lease.
                    var authority = inventory.Attempts
                        .Select(item => new WorkAttemptAuthority(item.AttemptId, item.LeaseId, item.Fence, true)).ToArray();
                    var actions = await executor.ReconcileAsync(Required(options, "host"), authority, ct);
                    output.WriteLine(JsonSerializer.Serialize(actions, Json));
                    return 0;
                }
                case "run":
                {
                    var settings = Settings(options);
                    if (settings.Mode != WorkContainerMode.Canary)
                    {
                        error.WriteLine($"work-container run requires {WorkContainerSettings.ModeVariable}=canary; the process executor stays authoritative.");
                        return 3;
                    }
                    var attempt = JsonSerializer.Deserialize<WorkContainerAttempt>(
                        await File.ReadAllTextAsync(Required(options, "attempt"), ct), Json)
                        ?? throw new InvalidDataException("Attempt file is empty.");
                    var image = options.GetValueOrDefault("image")
                        ?? RoleImageManifest.TryLoad(settings.ImageManifestPath)?.Resolve(attempt.Role)
                        ?? throw new InvalidOperationException($"No pinned image for role '{attempt.Role}'.");
                    var version = Number(options, "allocation-version");
                    var grant = new WorkContainerGrant(version,
                        options.ContainsKey("acknowledged-version") ? Number(options, "acknowledged-version") : version,
                        (int)Number(options, "grant-half-cores"));
                    using var brokerHttp = options.ContainsKey("broker-url")
                        ? new HttpClient { BaseAddress = new Uri(Required(options, "broker-url").TrimEnd('/') + "/"),
                            Timeout = TimeSpan.FromSeconds(30) }
                        : null;
                    IWorkContainerCredentialSource? credentialSource = brokerHttp is null ? null
                        : new TaskServerWorkCredentialSource(brokerHttp, Required(options, "broker-bearer-file"),
                            Required(options, "runner-id"), Required(options, "instance-id"), Required(options, "subject-id"));
                    async Task Report(WorkContainerAttempt item, string selectedImage, int halfCores,
                        string cleanup, string cache, DateTime startedAt, CancellationToken token)
                    {
                        if (brokerHttp is null) return;
                        using var request = new HttpRequestMessage(HttpMethod.Post,
                            $"api/v1/runs/{Uri.EscapeDataString(item.AttemptId)}/executor-receipt");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                            File.ReadAllText(Required(options, "broker-bearer-file")).Trim());
                        var at = selectedImage.IndexOf("@sha256:", StringComparison.Ordinal);
                        request.Content = JsonContent.Create(new
                        {
                            RunnerId = Required(options, "runner-id"), InstanceId = Required(options, "instance-id"),
                            item.LeaseId, item.Fence, Executor = "container",
                            ImageDigest = at < 0 ? null : selectedImage[(at + 1)..],
                            GrantHalfCores = halfCores, CacheOutcome = cache,
                            CleanupStatus = cleanup, StartedAt = startedAt,
                        });
                        using var response = await brokerHttp.SendAsync(request, token);
                        response.EnsureSuccessStatusCode();
                    }
                    var receipt = await new WorkContainerExecutor(new CliWorkContainerRuntime(settings.Runtime), settings,
                            log: output.WriteLine, credentials: credentialSource,
                            reportStarted: (item, selectedImage, halfCores, token) =>
                                Report(item, selectedImage, halfCores, "running", "pending", DateTime.UtcNow, token))
                        .RunAsync(attempt, image, grant, ct);
                    if (brokerHttp is not null)
                    {
                        try
                        {
                            var cache = string.Join(", ", receipt.Cache.Select(pair => $"{pair.Key}:{pair.Value}"));
                            await Report(attempt, image, grant.HalfCores,
                                receipt.CleanupVerified ? "verified" : "failed",
                                cache.Length == 0 ? "unreported" : cache.Length <= 80 ? cache : cache[..80],
                                receipt.StartedAtUtc,
                                CancellationToken.None);
                        }
                        catch (Exception) { output.WriteLine($"work-container visibility receipt pending attempt={attempt.AttemptId}"); }
                    }
                    output.WriteLine(JsonSerializer.Serialize(receipt, Json));
                    return receipt.Outcome == "succeeded" && receipt.CleanupVerified ? 0 : 1;
                }
                case "proof":
                {
                    var settings = Settings(options) with
                    {
                        // A proof-only cache generation makes the first attempt honestly cold.
                        CacheVersion = options.ContainsKey("cache-version")
                            ? (int)Number(options, "cache-version")
                            : 1000 + (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000),
                    };
                    var proof = new WorkContainerProofOptions(
                        Required(options, "host"), Required(options, "image"), Path.GetFullPath(Required(options, "source")),
                        Required(options, "sha"), options.GetValueOrDefault("repository-key") ?? "proof-repository",
                        options.ContainsKey("attempts") ? (int)Number(options, "attempts") : 100,
                        options.ContainsKey("parallel") ? (int)Number(options, "parallel") : 4,
                        options.ContainsKey("grant-half-cores") ? (int)Number(options, "grant-half-cores") : 3,
                        Path.GetFullPath(Required(options, "report")),
                        options.GetValueOrDefault("gate-script") ?? DefaultGateScript,
                        options.GetValueOrDefault("image-layers-local") == "true");
                    return await WorkContainerProof.RunAsync(proof, settings, output, ct);
                }
                default:
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException
                                       or IOException or JsonException)
        {
            error.WriteLine($"work-container {args[0]} failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> KubernetesAsync(string operation, Dictionary<string, string> options,
        TextWriter output, CancellationToken ct)
    {
        var budget = Settings(options);
        var cluster = KubernetesJobSettings.FromEnvironment(Environment.GetEnvironmentVariable,
            Path.Combine(budget.StateDirectory, "kubernetes"));
        cluster.Validate(requireCanary: operation == "kubernetes-run");
        using var brokerHttp = new HttpClient
        {
            BaseAddress = new Uri(Required(options, "broker-url").TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var credentials = new TaskServerWorkCredentialSource(brokerHttp, Required(options, "broker-bearer-file"),
            Required(options, "runner-id"), Required(options, "instance-id"), Required(options, "subject-id"));
        var executor = new KubernetesJobExecutor(new KubectlJobRuntime(cluster.Kubeconfig, cluster.Namespace),
            cluster, budget, credentials);
        if (operation == "kubernetes-reconcile")
        {
            var inventory = JsonSerializer.Deserialize<HostAttemptInventory>(
                await File.ReadAllTextAsync(Required(options, "inventory"), ct), Json)
                ?? throw new InvalidDataException("Attempt inventory is empty.");
            var authority = inventory.Attempts.Select(item => new WorkAttemptAuthority(item.AttemptId, item.LeaseId, item.Fence, true)).ToArray();
            var receipts = await executor.ReconcileAsync(Required(options, "host"), authority, ct);
            output.WriteLine(JsonSerializer.Serialize(receipts, Json));
            return receipts.All(item => item.CleanupVerified) ? 0 : 1;
        }
        var attempt = JsonSerializer.Deserialize<WorkContainerAttempt>(
            await File.ReadAllTextAsync(Required(options, "attempt"), ct), Json)
            ?? throw new InvalidDataException("Attempt file is empty.");
        var image = options.GetValueOrDefault("image")
            ?? RoleImageManifest.TryLoad(budget.ImageManifestPath)?.Resolve(attempt.Role)
            ?? throw new InvalidOperationException($"No pinned image for role '{attempt.Role}'.");
        var version = Number(options, "allocation-version");
        var grant = new WorkContainerGrant(version, Number(options, "acknowledged-version"),
            (int)Number(options, "grant-half-cores"));
        var receipt = await executor.RunAsync(attempt, image, grant, ct);
        output.WriteLine(JsonSerializer.Serialize(receipt, Json));
        return receipt.Outcome == "succeeded" && receipt.CleanupVerified ? 0 : 1;
    }

    /// <summary>
    /// Synthetic gate used by the proof for both executors: read the checkout,
    /// hash every tracked file, restore two package caches, and burn a fixed
    /// amount of shell CPU. It measures executor overhead, not a real suite.
    /// </summary>
    public const string DefaultGateScript = """
        set -eu
        git ls-files | wc -l >/dev/null
        git ls-files -z | xargs -0 cat | sha256sum >/dev/null
        for cache in "$npm_config_cache" "$NUGET_PACKAGES"; do
          [ -f "$cache/package.bin" ] || head -c 33554432 /dev/zero > "$cache/package.bin"
          sha256sum "$cache/package.bin" >/dev/null
        done
        i=0; while [ "$i" -lt 300000 ]; do i=$((i + 1)); done
        """;

    private static int UpdateManifest(string operation, Dictionary<string, string> options, TextWriter output)
    {
        var path = Required(options, "manifest");
        var manifest = RoleImageManifest.TryLoad(path) ?? RoleImageManifest.Empty();
        var role = Required(options, "role");
        var roles = role == "all" ? RoleImageManifest.KnownRoles : [role];
        foreach (var item in roles)
            manifest = operation == "image-promote"
                ? manifest.Promote(item, Required(options, "image"), options.GetValueOrDefault("release"))
                : manifest.Rollback(item);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, manifest.Serialize());
        File.Move(temporary, path, true);
        foreach (var item in roles) output.WriteLine($"role={item} image={manifest.Resolve(item)} previous={manifest.Roles[item].Previous ?? "none"}");
        return 0;
    }

    private static WorkContainerExecutor Executor(Dictionary<string, string> options, TextWriter output)
    {
        var settings = Settings(options);
        return new WorkContainerExecutor(new CliWorkContainerRuntime(settings.Runtime), settings, log: output.WriteLine);
    }

    private static WorkContainerSettings Settings(Dictionary<string, string> options)
    {
        var memory = HostMemory.Read();
        var stateRoot = Environment.GetEnvironmentVariable("RUNNER_STATE_DIR") is { Length: > 0 } state
            ? state : "/var/lib/agent-host/state";
        var settings = WorkContainerSettings.FromEnvironment(Environment.GetEnvironmentVariable, WorkContainerSettings.HostCores(),
            memory.TotalBytes, Path.Combine(stateRoot, "work-containers"));
        return options.TryGetValue("state-dir", out var directory) ? settings with { StateDirectory = Path.GetFullPath(directory) } : settings;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"Expected '--name value' at '{args[index]}'.");
            options[args[index][2..]] = args[index + 1];
        }
        return options;
    }

    private static string Required(Dictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && value.Length > 0
            ? value : throw new ArgumentException($"--{name} is required.");

    private static long Number(Dictionary<string, string> options, string name)
        => long.TryParse(Required(options, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw new ArgumentException($"--{name} must be a non-negative integer.");
}
