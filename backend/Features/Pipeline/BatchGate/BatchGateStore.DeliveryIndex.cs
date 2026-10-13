using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed partial class BatchGateStore
{
    private readonly object _deliveryIndexLock = new();
    private Dictionary<string, List<BatchGateManifest>>? _deliveryIndex;
    private readonly HashSet<string> _unreadableDeliveryManifests = new(StringComparer.Ordinal);

    /// <summary>
    /// Closed manifests are immutable. Read retained manifests once per store
    /// lifetime, then update the index in CloseManifest. Verdicts and replays
    /// remain live reads, including those from abandoned batches.
    /// </summary>
    public IReadOnlyList<BatchGateManifest> FindDeliveryManifests(
        string project, string branch, IReadOnlyList<string> deliveryShas,
        Action<Exception> onUnreadable)
    {
        lock (_deliveryIndexLock)
        {
            if (_deliveryIndex is null)
            {
                _deliveryIndex = new(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(_root))
                {
                    foreach (var directory in Directory.EnumerateDirectories(_root))
                    {
                        var path = Path.Combine(directory, "manifest.json");
                        if (!File.Exists(path)) continue;
                        TryIndexDeliveryManifest(path, onUnreadable);
                    }
                }
            }
            else
            {
                foreach (var path in _unreadableDeliveryManifests.ToArray())
                    TryIndexDeliveryManifest(path, onUnreadable);
            }

            return deliveryShas
                .SelectMany(sha => _deliveryIndex.GetValueOrDefault(sha) ?? [])
                .DistinctBy(manifest => manifest.BatchId)
                .Where(manifest => string.Equals(manifest.Scope.Project, project, StringComparison.Ordinal)
                    && IntegrationVerificationProjection.SameBranch(manifest.Scope.IntegrationBranch, branch))
                .OrderByDescending(manifest => manifest.ClosedAtUtc)
                .ToArray();
        }
    }

    private void TryIndexDeliveryManifest(string path, Action<Exception> onUnreadable)
    {
        try
        {
            IndexDeliveryManifest(Read<BatchGateManifest>(path));
            _unreadableDeliveryManifests.Remove(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            if (_unreadableDeliveryManifests.Add(path)) onUnreadable(ex);
        }
    }

    private void IndexDeliveryManifest(BatchGateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.BatchId)
            || !IsSha(manifest.BaseSha)
            || manifest.Members is null || manifest.Scope is null
            || manifest.Members.Any(member => member is null
                || !IsSha(member.ResultSha) || !IsSha(member.ReviewSubjectSha))
            || manifest.MembershipDigest != BatchGatePolicy.MembershipDigest(
                manifest.BaseSha, manifest.Scope, manifest.Members))
            throw new InvalidDataException("The retained batch manifest has an invalid membership digest.");
        AddToDeliveryIndex(manifest);
    }

    private void AddToDeliveryIndex(BatchGateManifest manifest)
    {
        foreach (var sha in manifest.Members.Select(member => member.ResultSha).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_deliveryIndex!.TryGetValue(sha, out var manifests))
                _deliveryIndex[sha] = manifests = [];
            manifests.Add(manifest);
        }
    }
}
