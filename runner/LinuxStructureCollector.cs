using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Read-only observations from the runner's namespaces and the Studio slice.</summary>
internal sealed class LinuxStructureCollector(string proc, string sys, string dockerData, long clockTicks)
{
    private Dictionary<string, (long Usage, DateTime At)> _previous = new(StringComparer.Ordinal);
    private Dictionary<string, (long Usage, DateTime At)> _current = new(StringComparer.Ordinal);
    private DateTime _at;
    public static LinuxStructureCollector Local() => new("/proc", "/sys", "/var/lib/docker", sysconf(2));

    public HostStructureReading Sample(RunnerOptions options, HostTelemetrySnapshotDto telemetry, int effectiveLimit)
    {
        var cards = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (options.Role == "coding" && Directory.Exists(options.StateDir))
                foreach (var slot in new RunnerStateStore(options.StateDir).LoadAll())
                    cards[WorkerCgroup.WorkerPrefix + WorkerCgroup.NameFor(slot.WorkerDirectory)] = slot.TaskKey;
            if (options.Role == "review" && Directory.Exists(Path.Combine(options.StateDir, "reviews")))
                foreach (var slot in new ReviewStateStore(options.StateDir).LoadAll())
                    cards[WorkerCgroup.WorkerPrefix + WorkerCgroup.NameFor(slot.WorkerDirectory)] = slot.Claim.Attempt!.TaskId;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // State can disappear during settlement. The cgroup counters remain independently readable.
        }
        lock (this)
            return Read(DateTime.UtcNow, options.RunnerId, options.RunnerName, options.Role, telemetry.Filesystems ?? [], cards)
                with { SlotLimit = options.HostMaxParallelism, EffectiveSlotLimit = effectiveLimit };
    }

    internal HostStructureReading Read(DateTime at, string reporter, string name, string role,
        IReadOnlyList<HostFilesystemUsageDto> mounts, IReadOnlyDictionary<string, string> cards)
    {
        (_previous, _current) = (_current, _previous);
        _current.Clear();
        _at = at;
        var groups = Path.Combine(sys, "fs/cgroup");
        var relative = ReadFile(Path.Combine(proc, "self/cgroup"))?.Split('\n')
            .FirstOrDefault(line => line.StartsWith("0::/", StringComparison.Ordinal))?[4..];
        // A cgroup namespace can expose an ancestor using "..". Never follow it outside the mount.
        var own = relative is null || relative.Split('/').Contains("..") ? null : Path.Combine(groups, relative);
        var unit = Path.GetFileName(own) == WorkerCgroup.DaemonLeafName ? Path.GetDirectoryName(own) : own;
        var workers = new Dictionary<string, HostWorkerReading>();
        foreach (var (worker, card) in cards) workers[worker] = new(card, null);
        foreach (var dir in Dirs(unit, WorkerCgroup.WorkerPrefix + "*"))
            workers[Path.GetFileName(dir)] = new(cards.GetValueOrDefault(Path.GetFileName(dir)), Counters(dir));

        var studio = Path.Combine(groups, "studio.slice");
        var containers = Directories(studio, "docker-*.scope")?.Select(Container).ToArray();
        var postSlice = Path.Combine(groups, "system.slice", @"system-agent\x2dpost\x2dmerge\x2dwindow.slice");
        var post = Dirs(postSlice, "agent-post-merge-window@*.service")
            .Select(dir => new HostUnitReading(Path.GetFileName(dir), Counters(dir))).ToArray();
        if (post.Length == 0 && Directory.Exists(postSlice))
            post = [new("post-merge-window", Counters(postSlice))];
        return new(at, reporter, at.AddSeconds(90), name, role, ReadFile(Path.Combine(sys, "class/dmi/id/product_name"))?.Trim(),
            Devices(mounts), unit is null || !Directory.Exists(unit) ? null : Counters(unit), workers.Values.ToArray(), containers, post);
    }

    private HostBlockDevice[] Devices(IReadOnlyList<HostFilesystemUsageDto> mounts)
    {
        var mountInfo = (ReadFile(Path.Combine(proc, "self/mountinfo")) ?? "").Split('\n')
            .Select(line => line.Split(' ')).Where(fields => fields.Length > 6).ToArray();
        return Dirs(Path.Combine(sys, "block"), "*").Where(dir => !File.Exists(Path.Combine(dir, "loop/backing_file"))
                && Long(ReadFile(Path.Combine(dir, "size"))) > 0)
            .Select(dir =>
            {
                var devices = Dirs(dir, "*").Select(child => ReadFile(Path.Combine(child, "dev"))?.Trim())
                    .Append(ReadFile(Path.Combine(dir, "dev"))?.Trim()).OfType<string>().ToHashSet();
                var paths = mountInfo.Where(row => devices.Contains(row[2])).Select(row => row[4].Replace(@"\040", " ")).ToHashSet();
                return new HostBlockDevice(Path.GetFileName(dir), Long(ReadFile(Path.Combine(dir, "size"))) * 512,
                    ReadFile(Path.Combine(dir, "md/level"))?.Trim(), Dirs(Path.Combine(dir, "slaves"), "*").Select(p => Path.GetFileName(p)).ToArray(),
                    mounts.Where(m => paths.Contains(m.Path)).ToArray());
            }).ToArray();
    }

    private StudioContainerReading Container(string dir)
    {
        var id = Path.GetFileName(dir)[7..^6];
        string? role = null;
        double? age = null;
        foreach (var pid in (ReadFile(Path.Combine(dir, "cgroup.procs")) ?? "").Split('\n').Take(128))
        {
            if (!int.TryParse(pid, out var processId) || processId <= 0) continue;
            var process = Path.Combine(proc, pid);
            var namespaceIds = ReadFile(Path.Combine(process, "status"))?.Split('\n')
                .FirstOrDefault(line => line.StartsWith("NSpid:", StringComparison.Ordinal))?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (namespaceIds?.LastOrDefault() != "1") continue;
            var args = ReadFile(Path.Combine(process, "cmdline"))?.Split('\0') ?? [];
            var mounts = ReadFile(Path.Combine(process, "mountinfo")) ?? "";
            if (args.Any(arg => Path.GetFileName(arg) == "OrchestratorApi.dll")) role = "api";
            else if (args.Any(arg => Path.GetFileName(arg) == "orchestrator-engine.dll")) role = "engine";
            else if (mounts.Contains("/Caddyfile.frontend /etc/caddy/Caddyfile ", StringComparison.Ordinal)) role = "web";
            else if (mounts.Split('\n').Any(line => new[] {"public", "private", "cloudflare"}.Any(mode =>
                line.Contains($"/Caddyfile.{mode} /etc/caddy/Caddyfile ", StringComparison.Ordinal)))) role = "edge";
            var stat = ReadFile(Path.Combine(process, "stat"));
            if (stat?.LastIndexOf(')') is >= 0 and var end && clockTicks > 0 &&
                Long(stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(19)) is { } start &&
                double.TryParse(ReadFile(Path.Combine(proc, "uptime"))?.Split(' ')[0], CultureInfo.InvariantCulture, out var up))
                age = Math.Max(0, up - (double)start / clockTicks);
            break;
        }
        string? image = null;
        int? restarts = null;
        if (ReadFile(Path.Combine(dockerData, "containers", id, "config.v2.json")) is { } metadata)
        {
            try
            {
                using var json = JsonDocument.Parse(metadata);
                var root = json.RootElement;
                if (root.TryGetProperty("Config", out var config) && config.TryGetProperty("Image", out var tag))
                    image = tag.GetString();
                if (root.TryGetProperty("RestartCount", out var count) && count.TryGetInt32(out var n)) restarts = n;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* Other counters survive. */ }
        }
        return new(id, role, Counters(dir), age, image, restarts);
    }

    private CgroupReading Counters(string dir)
    {
        var usage = Long(ReadFile(Path.Combine(dir, "cpu.stat"))?.Split('\n').FirstOrDefault(row => row.StartsWith("usage_usec ", StringComparison.Ordinal))?[11..]);
        double? cores = null;
        if (usage is { } value)
        {
            if (_previous.TryGetValue(dir, out var previous) && _at > previous.At && value >= previous.Usage)
                cores = (value - previous.Usage) / ((_at - previous.At).TotalSeconds * 1_000_000);
            _current[dir] = (value, _at);
        }
        return new(usage, Long(ReadFile(Path.Combine(dir, "memory.current"))), cores);
    }

    private static long? Long(string? value) => long.TryParse(value, out var n) && n >= 0 ? n : null;
    private static string[] Dirs(string? dir, string pattern) => Directories(dir, pattern) ?? [];
    private static string[]? Directories(string? dir, string pattern)
    {
        try { return dir is null ? null : Directory.GetDirectories(dir, pattern).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    [DllImport("libc")] private static extern long sysconf(int name);
}
