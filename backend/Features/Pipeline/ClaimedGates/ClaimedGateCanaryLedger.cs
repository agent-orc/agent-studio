using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record ClaimedGateCanaryPass(string GateId, string Kind, string? Project, string SubjectId,
    string TestedSha, string Executor, DateTimeOffset At);

/// <summary>
/// Durable record of claimed verification gates that passed with complete
/// bridge-equivalent evidence (DP-S4). The compatibility bridge stays the
/// fallback for non-canary projects until the required passes are recorded.
/// </summary>
public sealed class ClaimedGateCanaryLedger
{
    public const string RequiredPassesKey = "RemoteGate:ClaimedCanaryRequiredPasses";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _required;
    private int _passes;

    public ClaimedGateCanaryLedger(string root, IConfiguration? configuration = null)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "claimed-gate-canary.jsonl");
        _required = Math.Clamp(configuration?.GetValue<int?>(RequiredPassesKey) ?? 1, 1, 100);
        if (File.Exists(_path))
            _passes = File.ReadLines(_path).Count(line => !string.IsNullOrWhiteSpace(line));
    }

    public bool Passed
    {
        get { lock (_gate) return _passes >= _required; }
    }

    public void Record(ClaimedGateCanaryPass pass)
    {
        lock (_gate)
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(pass, Json);
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
            _passes++;
        }
    }
}
