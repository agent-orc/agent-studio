using System.Globalization;
using System.Text.Json;

namespace AgentRunner.WorkContainers;

public sealed record WorkRuntimeResult(bool Ok, string Output)
{
    public static readonly WorkRuntimeResult Success = new(true, "");
}

/// <summary>A managed container with the grant it reserves while not exited.</summary>
public sealed record ManagedWorkContainer(ManagedWorkResource Resource, int GrantHalfCores, string State)
{
    public bool HoldsGrant => State is "created" or "running" or "restarting" or "paused" or "configured" or "initialized";
}

public sealed record ManagedWorkInventory(
    IReadOnlyList<ManagedWorkContainer> Containers,
    IReadOnlyList<ManagedWorkResource> Volumes)
{
    public int ReservedHalfCores => Containers.Where(item => item.HoldsGrant).Sum(item => item.GrantHalfCores);
}

/// <summary>
/// The container runtime as the work executor uses it. The production
/// implementation shells out to the Docker or Podman CLI on the host; the
/// work container itself never receives a runtime socket.
/// </summary>
public interface IWorkContainerRuntime
{
    Task<bool> IsAvailableAsync(CancellationToken ct);
    Task<bool> ImagePresentAsync(string image, CancellationToken ct);
    Task<WorkRuntimeResult> PullAsync(string image, CancellationToken ct);
    Task<WorkRuntimeResult> CreateVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<WorkRuntimeResult> CreateContainerAsync(IReadOnlyList<string> createArguments, CancellationToken ct);
    Task<WorkRuntimeResult> StartAsync(string container, CancellationToken ct);
    Task<WorkRuntimeResult> InjectGitTokenAsync(string container, string token, CancellationToken ct)
        => throw new NotSupportedException("This work-container runtime cannot inject attempt credentials.");

    /// <summary>Blocks until the container exits and returns its exit code.</summary>
    Task<int?> WaitAsync(string container, CancellationToken ct);

    /// <summary>Complete log with runtime timestamps, read before removal.</summary>
    Task<WorkRuntimeResult> LogsAsync(string container, CancellationToken ct);
    Task<WorkRuntimeResult> RemoveContainerAsync(string container, CancellationToken ct);
    Task<WorkRuntimeResult> RemoveVolumeAsync(string volume, CancellationToken ct);
    Task<ManagedWorkInventory> ListManagedAsync(string hostId, string? attemptId, CancellationToken ct);
}

/// <summary>Docker or Podman CLI implementation. Every process goes through <see cref="ProcessRunner"/>.</summary>
public sealed class CliWorkContainerRuntime(string runtime) : IWorkContainerRuntime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(30);
    private const int InventoryAttempts = 5;

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try { return (await Run(["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(10), ct)).Ok; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    public async Task<bool> ImagePresentAsync(string image, CancellationToken ct)
        => (await Run(["image", "inspect", "--format", "{{.Id}}", image], CommandTimeout, ct)).Ok;

    public Task<WorkRuntimeResult> PullAsync(string image, CancellationToken ct)
        => Run(["pull", "--quiet", image], PullTimeout, ct);

    public Task<WorkRuntimeResult> CreateVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var arguments = new List<string> { "volume", "create" };
        foreach (var (key, value) in labels) arguments.AddRange(["--label", $"{key}={value}"]);
        arguments.Add(name);
        return Run(arguments, CommandTimeout, ct);
    }

    public Task<WorkRuntimeResult> CreateContainerAsync(IReadOnlyList<string> createArguments, CancellationToken ct)
        => Run(createArguments, CommandTimeout, ct);

    public Task<WorkRuntimeResult> StartAsync(string container, CancellationToken ct)
        => Run(["start", container], CommandTimeout, ct);

    public async Task<WorkRuntimeResult> InjectGitTokenAsync(string container, string token, CancellationToken ct)
    {
        // The token exists only on stdin of the runtime and the file inside the
        // container's private tmpfs. It never becomes an argument or environment value.
        var result = await ProcessRunner.RunAsync(runtime,
            ["exec", "--interactive", "--user", WorkContainerLaunchPlanner.ContainerUser,
                container, "/bin/sh", "-c", "umask 077; cat > /run/agent-secrets/.github-token-next && mv /run/agent-secrets/.github-token-next /run/agent-secrets/github-token"],
            stdin: token, clearEnvironment: true, ct: ct);
        return new WorkRuntimeResult(result.Success, result.Success ? "" : "Attempt secret injection failed.");
    }

    public async Task<int?> WaitAsync(string container, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(runtime, ["wait", container], ct: ct);
        return result.Success && int.TryParse(result.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? code : null;
    }

    public Task<WorkRuntimeResult> LogsAsync(string container, CancellationToken ct)
        => Run(["logs", "--timestamps", container], CommandTimeout, ct, mergeStdErr: true);

    public Task<WorkRuntimeResult> RemoveContainerAsync(string container, CancellationToken ct)
        => Run(["rm", "--force", "--volumes", container], CommandTimeout, ct);

    public Task<WorkRuntimeResult> RemoveVolumeAsync(string volume, CancellationToken ct)
        => Run(["volume", "rm", "--force", volume], CommandTimeout, ct);

    public async Task<ManagedWorkInventory> ListManagedAsync(string hostId, string? attemptId, CancellationToken ct)
    {
        var filters = new List<string>
        {
            "--filter", $"label={WorkContainerLaunchPlanner.ManagedLabel}=1",
            "--filter", $"label={WorkContainerLaunchPlanner.HostLabel}={hostId}",
        };
        if (attemptId is not null) filters.AddRange(["--filter", $"label={WorkContainerLaunchPlanner.AttemptLabel}={attemptId}"]);
        // A listing can race a concurrent removal inside the runtime (observed on
        // Docker 29 with the containerd snapshotter: "rw layer snapshot not
        // found"). Retry a few times; an unreadable inventory must still never
        // look like an empty host, so the last failure is thrown.
        WorkRuntimeResult containers = WorkRuntimeResult.Success, volumes = WorkRuntimeResult.Success;
        for (var attempt = 1; attempt <= InventoryAttempts; attempt++)
        {
            containers = await Run(["ps", "--all", "--no-trunc", .. filters, "--format", "{{json .}}"], CommandTimeout, ct);
            volumes = containers.Ok
                ? await Run(["volume", "ls", .. filters, "--format", "{{json .}}"], CommandTimeout, ct)
                : containers;
            if (containers.Ok && volumes.Ok) return ParseInventory(containers.Output, volumes.Output);
            if (attempt < InventoryAttempts) await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
        }
        throw new InvalidOperationException($"{runtime} inventory unreadable after {InventoryAttempts} attempts: "
            + (containers.Ok ? volumes.Output : containers.Output));
    }

    /// <summary>Parses Docker (labels as a string) and Podman (labels as an object) JSON lines.</summary>
    internal static ManagedWorkInventory ParseInventory(string containerLines, string volumeLines)
    {
        var containers = new List<ManagedWorkContainer>();
        foreach (var line in Lines(containerLines))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var labels = Labels(root);
            var name = root.TryGetProperty("Names", out var names)
                ? names.ValueKind == JsonValueKind.Array ? names[0].GetString() ?? "" : names.GetString() ?? ""
                : "";
            var state = (root.TryGetProperty("State", out var stateElement) ? stateElement.GetString() : null)?.ToLowerInvariant() ?? "unknown";
            containers.Add(new ManagedWorkContainer(
                Resource("container", name, labels, state == "running"),
                int.TryParse(labels.GetValueOrDefault(WorkContainerLaunchPlanner.GrantLabel), out var grant) ? grant : 0,
                state));
        }
        var volumes = new List<ManagedWorkResource>();
        foreach (var line in Lines(volumeLines))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            volumes.Add(Resource("volume", root.GetProperty("Name").GetString() ?? "", Labels(root), false));
        }
        return new ManagedWorkInventory(containers, volumes);
    }

    private static ManagedWorkResource Resource(string kind, string name, IReadOnlyDictionary<string, string> labels, bool running)
        => new(kind, name,
            labels.GetValueOrDefault(WorkContainerLaunchPlanner.AttemptLabel) ?? "",
            labels.GetValueOrDefault(WorkContainerLaunchPlanner.LeaseLabel) ?? "",
            long.TryParse(labels.GetValueOrDefault(WorkContainerLaunchPlanner.FenceLabel), out var fence) ? fence : 0,
            running);

    private static Dictionary<string, string> Labels(JsonElement root)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("Labels", out var element)) return labels;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) labels[property.Name] = property.Value.GetString() ?? "";
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            foreach (var pair in (element.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var equal = pair.IndexOf('=');
                if (equal > 0) labels[pair[..equal]] = pair[(equal + 1)..];
            }
        }
        return labels;
    }

    private static IEnumerable<string> Lines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith('{'));

    private async Task<WorkRuntimeResult> Run(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, bool mergeStdErr = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var result = await ProcessRunner.RunAsync(runtime, arguments, ct: deadline.Token);
        if (result.Success) return new(true, mergeStdErr ? result.StdOut + result.StdErr : result.StdOut);
        var detail = result.StdErr.Trim();
        return new(false, $"{runtime} {arguments[0]} exited {result.ExitCode}: {(detail.Length <= 400 ? detail : detail[^400..])}");
    }
}
