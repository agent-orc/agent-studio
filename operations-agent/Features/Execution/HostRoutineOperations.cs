using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Agent.Features.Execution;

public sealed record ReviewedConfiguration(string SourceFile, string TargetFile, string Sha256);

/// <summary>Paths and executable identities are host-owned. Requests contain only catalogue names.</summary>
public sealed record HostRoutineSettings(string? AgentHostExecutable, string? HostAgentConfig,
    IReadOnlyDictionary<string, string> LogSources, IReadOnlyDictionary<string, ReviewedConfiguration> Configurations,
    string? RevokedKeysFile = null)
{
    public static HostRoutineSettings Empty { get; } = new(null, null,
        new Dictionary<string, string>(), new Dictionary<string, ReviewedConfiguration>());

    public static HostRoutineSettings Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;
        if (new FileInfo(path).Length > 64 * 1024)
            throw new InvalidDataException("Host operations allowlist is too large.");
        var loaded = JsonSerializer.Deserialize<HostRoutineSettings>(File.ReadAllText(path), OperationsProtocol.Json)
            ?? throw new InvalidDataException("Host operations allowlist is empty.");
        if (loaded.LogSources is null || loaded.Configurations is null
            || loaded.LogSources.Count > 16 || loaded.Configurations.Count > 16)
            throw new InvalidDataException("Host operations allowlist is too large.");
        foreach (var (name, source) in loaded.LogSources)
            if (!OperationCatalogue.ValidServiceName(name) || !SafeFile(source))
                throw new InvalidDataException("Host log source is invalid.");
        foreach (var (name, configuration) in loaded.Configurations)
            if (!OperationCatalogue.ValidServiceName(name) || !SafeFile(configuration.SourceFile)
                || !Path.IsPathFullyQualified(configuration.TargetFile) || !OperationCatalogue.ValidSha256(configuration.Sha256))
                throw new InvalidDataException("Reviewed configuration is invalid.");
        if (loaded.AgentHostExecutable is not null && (!SafeFile(loaded.AgentHostExecutable)
            || !SafeFile(loaded.HostAgentConfig)))
            throw new InvalidDataException("Host drain executable or configuration is invalid.");
        if (loaded.RevokedKeysFile is not null && (!Path.IsPathFullyQualified(loaded.RevokedKeysFile)
            || !Directory.Exists(Path.GetDirectoryName(loaded.RevokedKeysFile))))
            throw new InvalidDataException("Host revocation target is invalid.");
        return loaded;
    }

    internal static bool SafeFile(string? path) => !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path) && File.Exists(path) && new FileInfo(path).LinkTarget is null;
}

public static class HostRoutineOperations
{
    public static async Task<OperationResult> ExecuteAsync(OperationAttempt attempt, string agentId, string bootId,
        HostRoutineSettings settings, Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ToolRun>> run,
        TimeProvider clock, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        if (attempt.AgentId != agentId || attempt.BootId != bootId || attempt.Fence < 1
            || attempt.State is not ("running" or "cancelling") || attempt.LeaseUntil <= started
            || attempt.Command.Deadline <= started || OperationsProtocol.Digest(attempt.Command.Input) != attempt.Command.InputDigest)
            throw new InvalidOperationException("Host operation is outside the assignment or lease.");
        if (attempt.State == "cancelling") return Result(attempt, "cancelled", "cancelled-before-execution", Empty(), started, clock.GetUtcNow());

        var input = attempt.Command.Input;
        switch (attempt.Command.OperationId)
        {
            case "host.drain":
            case "host.resume":
            {
                var role = input.GetProperty("role").GetString()!;
                if (role is not ("coding" or "review" or "post") || settings.AgentHostExecutable is null
                    || !HostRoutineSettings.SafeFile(settings.AgentHostExecutable)
                    || !HostRoutineSettings.SafeFile(settings.HostAgentConfig))
                    return Result(attempt, "failed", "host-role-not-allowlisted", Empty(), started, clock.GetUtcNow());
                var info = new ProcessStartInfo(settings.AgentHostExecutable!) { UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "host-agent", attempt.Command.OperationId == "host.drain" ? "drain-role" : "enable-role",
                    settings.HostAgentConfig!, role }) info.ArgumentList.Add(arg);
                var result = await run(info, TimeSpan.FromSeconds(45), ct);
                var succeeded = result.ExitCode == 0 && !result.TimedOut;
                return Result(attempt, succeeded ? "succeeded" : "failed",
                    succeeded ? "completed" : result.TimedOut ? "host-role-timeout" : "host-role-failed",
                    succeeded ? JsonSerializer.SerializeToElement(new { role, drained = attempt.Command.OperationId == "host.drain" }) : Empty(),
                    started, clock.GetUtcNow());
            }
            case "host.logs.collect":
            {
                var source = input.GetProperty("source").GetString()!;
                if (!settings.LogSources.TryGetValue(source, out var path) || !HostRoutineSettings.SafeFile(path))
                    return Result(attempt, "failed", "log-source-not-allowlisted", Empty(), started, clock.GetUtcNow());
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(-Math.Min(stream.Length, 8192), SeekOrigin.End);
                var bytes = new byte[(int)Math.Min(stream.Length, 8192)];
                var count = await stream.ReadAsync(bytes, ct);
                var value = new string(Encoding.UTF8.GetString(bytes, 0, count)
                    .Select(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')
                        ? '?' : character).ToArray());
                OperationResult result;
                do
                {
                    result = Result(attempt, "succeeded", "completed",
                        JsonSerializer.SerializeToElement(new { source, text = value }), started, clock.GetUtcNow());
                    if (JsonSerializer.SerializeToUtf8Bytes(result, OperationsProtocol.Json).Length <= 7000) break;
                    value = value[(value.Length / 2)..];
                } while (value.Length > 0);
                return result;
            }
            case "host.configuration.apply":
            {
                var name = input.GetProperty("configuration").GetString()!;
                var digest = input.GetProperty("sha256").GetString()!;
                if (!settings.Configurations.TryGetValue(name, out var config) || config.Sha256 != digest
                    || !HostRoutineSettings.SafeFile(config.SourceFile) || !Path.IsPathFullyQualified(config.TargetFile)
                    || (File.Exists(config.TargetFile) && new FileInfo(config.TargetFile).LinkTarget is not null))
                    return Result(attempt, "failed", "configuration-not-reviewed", Empty(), started, clock.GetUtcNow());
                var sourceInfo = new FileInfo(config.SourceFile);
                if (sourceInfo.Length is <= 0 or > 1024 * 1024)
                    return Result(attempt, "failed", "configuration-size", Empty(), started, clock.GetUtcNow());
                var bytes = await File.ReadAllBytesAsync(config.SourceFile, ct);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest)
                    return Result(attempt, "failed", "configuration-digest-mismatch", Empty(), started, clock.GetUtcNow());
                var targetDirectory = Path.GetDirectoryName(config.TargetFile)!;
                if (!Directory.Exists(targetDirectory) || new DirectoryInfo(targetDirectory).LinkTarget is not null)
                    return Result(attempt, "failed", "configuration-target-invalid", Empty(), started, clock.GetUtcNow());
                var temporary = Path.Combine(targetDirectory, ".operations-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using (var stream = new FileStream(temporary, options))
                    {
                        await stream.WriteAsync(bytes, ct);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, config.TargetFile, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return Result(attempt, "succeeded", "completed", JsonSerializer.SerializeToElement(new { configuration = name, applied = true }), started, clock.GetUtcNow());
            }
            default: throw new InvalidOperationException("The agent does not execute this operation.");
        }
    }

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });
    private static OperationResult Result(OperationAttempt attempt, string outcome, string classification,
        JsonElement output, DateTimeOffset started, DateTimeOffset finished)
        => new(outcome, attempt.Command.InputDigest, classification, output, [], true, started, finished);
}
