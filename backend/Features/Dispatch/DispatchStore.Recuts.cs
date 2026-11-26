using AgentStudio.OrchestrationDecisions;
using AgentStudio.Shared;

namespace AgentStudio.Dispatch;

public sealed record DispatchWaveCard(string Key, IReadOnlyList<string> FindingRefs);
public sealed record DispatchRecutRequest(IReadOnlyList<DispatchWaveCard> Cards, bool Apply = false);
public sealed record DispatchRecutText(string Title, string Prompt, bool Stub);
public sealed record DispatchRecutOriginal(TaskInfo Info, string Prompt, IReadOnlyList<string> FindingRefs);
public sealed record DispatchRecutReceipt(string Id, string Project, string Workspace, string Wave,
    DateTimeOffset At, string State, DispatchRecutFacts Facts, DispatchRecutPlan Plan,
    IReadOnlyList<DispatchRecutOriginal> Originals, IReadOnlyDictionary<string, DispatchRecutText> Texts,
    IReadOnlyDictionary<string, string> Replacements, IReadOnlyList<string> ProposalIds,
    DateTimeOffset? UndoUntil = null, string? Error = null, long? LeaseFence = null);

public sealed partial class DispatchStore
{
    public DispatchRecutReceipt? ReadRecut(string project, string id)
    {
        lock (PersonaGate(project)) return Read<DispatchRecutReceipt>(Path.Combine(Project(project), "recuts", Key(id) + ".json"));
    }
    public IReadOnlyList<DispatchRecutReceipt> ReadRecuts(string project)
    {
        lock (PersonaGate(project))
        {
            var path = Path.Combine(Project(project), "recuts");
            return !Directory.Exists(path) ? [] : Directory.EnumerateFiles(path, "*.json")
                .Select(Read<DispatchRecutReceipt>).OfType<DispatchRecutReceipt>().OrderBy(r => r.At).ToArray();
        }
    }
    public void WriteRecut(DispatchRecutReceipt receipt)
    {
        lock (PersonaGate(receipt.Project))
            Write(Path.Combine(Project(receipt.Project), "recuts", Key(receipt.Id) + ".json"), receipt);
    }

    public void LogRecut(DispatchRecutReceipt receipt)
    {
        lock (PersonaGate(receipt.Project))
        {
            var id = receipt.Id + ":" + receipt.State;
            var log = ReadDispatcherLog(receipt.Project);
            if (log.Any(e => e.Id == id)) return;
            var path = Path.Combine(Project(receipt.Project), "dispatcher.jsonl");
            var reason = $"Packer {receipt.State}: {receipt.Plan.Actions.Count(a => a.Kind == "package")} packages; "
                + $"{receipt.Plan.Edges.Count} chain edges; {receipt.ProposalIds.Count} owner proposals.";
            var digest = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(receipt.Facts, Json))));
            var row = new DispatcherLogEntryV1(id, receipt.Project, "packer", "dispatch.recut." + receipt.State,
                receipt.Id, receipt.At, receipt.Id, "package-then-chain-v1", "2026.11.1",
                string.Join(',', receipt.Plan.Actions.Select(a => a.Reason).Distinct()), digest, [], [], reason,
                !receipt.Facts.LiveAuthority, null, log.LastOrDefault()?.Digest, "")
            { ExpectedEffect = "Retain source scope; package shared files and serialize remaining overlap. Undo until " + receipt.UndoUntil?.ToString("O") };
            row = row with { Digest = LogDigest(row) };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(row, Json) + "\n");
            _dispatcherHeads[path] = (new FileInfo(path).Length, row.Digest);
        }
    }
}
