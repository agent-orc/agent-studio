using System.Text.Json;
using AgentStudio.Security;

namespace AgentStudio.Operator;

public sealed record AttentionDismissRequest(string[]? Ids, string? Reason);

/// <summary>Dismissals last for an observed occurrence. The publisher retires a dismissal
/// after a successful source read observes that the cause cleared, even with no reader online.</summary>
public sealed class OperatorAttentionDismissals
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly ControlPlaneAuditLog _audit;
    private Dictionary<string, string> _dismissed;

    public OperatorAttentionDismissals(IConfiguration config, ControlPlaneAuditLog audit)
    {
        _audit = audit;
        _file = Path.Combine(OperatorDecisionOptions.Folder(config), "attention-dismissals.json");
        _dismissed = File.Exists(_file)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file))! : [];
    }

    public IReadOnlySet<string> Hidden()
    {
        lock (_gate) return _dismissed.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public void Observe(IReadOnlyList<OperatorDecisionFact> facts, IReadOnlySet<string> observedTypes)
    {
        lock (_gate)
        {
            if (_dismissed.Count == 0) return;
            var open = facts.Select(fact => fact.Key).ToHashSet(StringComparer.Ordinal);
            var remaining = _dismissed.Where(pair => !observedTypes.Contains(pair.Value) || open.Contains(pair.Key))
                .ToDictionary();
            if (remaining.Count != _dismissed.Count) Save(remaining);
        }
    }

    public void Dismiss(IReadOnlyList<OperatorAttentionItem> items, string actor, string reason)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, string>(_dismissed);
            foreach (var item in items) next[item.Id] = item.Type;
            _audit.Append(new(ControlPlaneAuditStreams.Operator, actor, OperatorOperationIds.AttentionDismiss, "dismissed",
                Details: new { reason, items = items.Select(item => new { item.Id, item.Type, item.Project }).ToArray() }));
            Save(next);
        }
    }

    private void Save(Dictionary<string, string> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next));
        File.Move(temp, _file, overwrite: true);
        _dismissed = next;
    }
}
