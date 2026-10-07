using System.Text.Json;

namespace AgentRunner;

/// <summary>Calls a fixed workstation command over protected SSH. Only public key metadata crosses.</summary>
public sealed class WorkstationGitHubAdministration(
    string sshHost, Func<string, CancellationToken, Task<ProcessResult>>? execute = null)
    : IRepositoryKeyAdministration
{
    private readonly string _sshHost = ValidHost(sshHost);

    public async Task<RegisteredRepositoryKey?> FindByFingerprintAsync(
        RepositoryRotationRequest request, string fingerprint, CancellationToken ct)
        => (await SendAsync(new("find", request.HostId, request.Owner, request.Repository,
            request.OperationId, request.RequiresPush, fingerprint, null, null), ct)).Key;

    public async Task<RegisteredRepositoryKey?> RegisterAsync(
        RepositoryRotationRequest request, RepositoryKeyCandidate candidate, CancellationToken ct)
        => (await SendAsync(new("register", request.HostId, request.Owner, request.Repository,
            request.OperationId, request.RequiresPush, candidate.Fingerprint,
            candidate.PublicKey, null), ct)).Key;

    public async Task DeleteAsync(RepositoryRotationRequest request, long keyId, CancellationToken ct)
        => _ = await SendAsync(new("delete", request.HostId, request.Owner, request.Repository,
            request.OperationId, request.RequiresPush, null, null, keyId), ct);

    private async Task<WorkstationAdminReply> SendAsync(WorkstationAdminRequest request, CancellationToken ct)
    {
        var serialized = JsonSerializer.Serialize(request);
        ProcessResult process;
        try
        {
            process = execute is not null
                ? await execute(serialized, ct)
                : await ProcessRunner.RunAsync("ssh",
                    ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "--", _sshHost,
                        "agent-host", "--repository-admin"],
                    stdin: serialized, ct: ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new RepositoryAdminAuthorityException();
        }
        if (process.ExitCode is not (0 or 3))
            throw new RepositoryAdminAuthorityException();
        var reply = JsonSerializer.Deserialize<WorkstationAdminReply>(process.StdOut)
            ?? throw new InvalidOperationException("Protected workstation administration response was empty.");
        if (reply.Status == "authority-required") throw new RepositoryAdminAuthorityException();
        if (reply.Status is not ("ok" or "missing"))
            throw new InvalidOperationException("Protected workstation administration response was invalid.");
        return reply;
    }

    private static string ValidHost(string host)
    {
        if (host.Length is < 1 or > 200 || !char.IsAsciiLetterOrDigit(host[0])
            || host.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.')))
            throw new ArgumentException("Workstation SSH host must be a configured host alias.");
        return host;
    }
}

public sealed record WorkstationAdminRequest(
    string Action, string HostId, string Owner, string Repository, string OperationId,
    bool RequiresPush, string? Fingerprint, string? PublicKey, long? KeyId);
public sealed record WorkstationAdminReply(string Status, RegisteredRepositoryKey? Key);
