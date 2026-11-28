using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;
using Protocol = AgentStudio.TaskServer.Contracts.RunnerCredentialRenewalProtocol;

namespace AgentRunner;

/// <summary>Registers a verifier only after its bearer is staged on the same filesystem as the live file.</summary>
internal sealed class RunnerCredentialRenewalHandler(
    string path, Uri authority, HttpMessageHandler inner, TimeProvider clock,
    Action<FileStream, byte[]>? writeStaged = null)
    : DelegatingHandler(inner)
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private (string Id, long Timestamp)? _lastAttempt;
    private bool _renewing;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bearer = request.Headers.Authorization?.Parameter;
        var localRequest = IsAuthority(request.RequestUri);
        var response = await base.SendAsync(request, ct);
        if (!localRequest || !IsAuthority(response.RequestMessage?.RequestUri ?? request.RequestUri)
            || response.StatusCode == HttpStatusCode.Unauthorized
            || !Protocol.TryReadCredentialId(bearer, out var predecessor)
            || ReadState(response) is not (Protocol.Due or Protocol.Failing) || !TryBegin(predecessor))
            return response;

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(AttemptTimeout);
            var status = await RenewAsync(bearer!, predecessor, deadline.Token, ct);
            response.Headers.Remove(Protocol.StatusHeader);
            response.Headers.TryAddWithoutValidation(Protocol.StatusHeader, status);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
        finally
        {
            lock (_gate) _renewing = false;
        }
    }

    private bool IsAuthority(Uri? uri)
        => uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps
            && uri.IdnHost == authority.IdnHost && uri.Port == authority.Port;

    internal static string? ReadState(HttpResponseMessage response)
        => response.Headers.TryGetValues(Protocol.StatusHeader, out var states) ? states.FirstOrDefault() : null;

    private bool TryBegin(string predecessor)
    {
        lock (_gate)
        {
            if (_renewing || (_lastAttempt is { } last && last.Id == predecessor
                             && clock.GetElapsedTime(last.Timestamp) < RetryInterval))
                return false;
            _lastAttempt = (predecessor, clock.GetTimestamp());
            _renewing = true;
            return true;
        }
    }

    private async Task<string> RenewAsync(string bearer, string predecessor, CancellationToken deadline, CancellationToken ct)
    {
        string failure;
        try
        {
            if (RunnerOptions.ReadAuthTokenFile(path) != bearer) return Protocol.Scheduled;
            var staged = path + ".renew-" + predecessor;
            var candidate = ReadOrStage(staged);
            if (!Protocol.TryReadCredentialId(candidate, out var id))
                throw new IOException("The staged runner credential is invalid.");

            using var response = await PostAsync(bearer, new(id, OpaqueSecretHash.Compute(candidate)), deadline);
            if (response.StatusCode == HttpStatusCode.NoContent
                && response.Headers.TryGetValues(Protocol.StagedHeader, out var stagedIds) && stagedIds.SequenceEqual([id]))
            {
                ct.ThrowIfCancellationRequested();
                // The source is already private and flushed. The rename leaves either the old
                // file or the complete successor at the live path, including across restarts.
                if (RunnerOptions.ReadAuthTokenFile(path) == bearer) File.Move(staged, path, overwrite: true);
                return Protocol.Scheduled;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) return Protocol.Failing;
            failure = Protocol.RequestFailed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = Protocol.FileUnavailable;
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            failure = Protocol.RequestFailed;
        }

        try
        {
            if (!deadline.IsCancellationRequested && RunnerOptions.ReadAuthTokenFile(path) == bearer)
            {
                using var report = await PostAsync(bearer, new(Failure: failure), deadline);
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException
                                      || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Protocol.Failing;
        }
        return Protocol.Failing;
    }

    private async Task<HttpResponseMessage> PostAsync(string bearer, RunnerCredentialRenewalRequest renewal, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(authority, Protocol.Path))
        {
            Content = JsonContent.Create(renewal),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await base.SendAsync(request, ct);
    }

    private string ReadOrStage(string staged)
    {
        if (!File.Exists(staged))
        {
            var temp = staged + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temp, options))
                {
                    var content = System.Text.Encoding.UTF8.GetBytes(Protocol.NewCredential() + Environment.NewLine);
                    if (writeStaged is null) file.Write(content);
                    else writeStaged(file, content);
                    file.Flush(flushToDisk: true);
                }
                try { File.Move(temp, staged); }
                catch (IOException) when (File.Exists(staged))
                {
                    // Another client sharing this credential file already staged the candidate.
                }
            }
            finally { File.Delete(temp); }
        }
        return RunnerOptions.ReadAuthTokenFile(staged);
    }
}
