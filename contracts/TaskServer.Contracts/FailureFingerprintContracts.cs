namespace AgentStudio.TaskServer.Contracts;

public sealed record RecordFailureFingerprintRequest(
    string Fingerprint,
    string CardKey,
    string Executor,
    string Source,
    string ReportKey);

public sealed record FailureFingerprintHistoryDto(
    string Fingerprint,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Count,
    IReadOnlyList<string> Executors,
    IReadOnlyList<string> CardKeys);
