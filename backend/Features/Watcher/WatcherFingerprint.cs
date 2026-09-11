using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Watcher;

/// <summary>
/// Deterministic, model-free fingerprint computation. Two observations with
/// the same detector class, project, and fingerprint key always collapse
/// into the same <see cref="WatcherCase"/>, restart or not (§2 "Case contract").
/// </summary>
public static class WatcherFingerprint
{
    public static string Compute(string detectorClass, string project, string fingerprintKey)
    {
        var raw = $"{detectorClass}|{project}|{fingerprintKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"{ShortCode(detectorClass)}-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    public static string Compute(WatcherSignalObservation observation) =>
        Compute(observation.DetectorClass, observation.Project, observation.FingerprintKey);

    private static string ShortCode(string detectorClass) => detectorClass switch
    {
        WatcherDetectorClasses.Repetition => "rep",
        WatcherDetectorClasses.Contradiction => "con",
        WatcherDetectorClasses.Silence => "sil",
        WatcherDetectorClasses.Drift => "drf",
        WatcherDetectorClasses.Hygiene => "hyg",
        _ => "unk",
    };
}
