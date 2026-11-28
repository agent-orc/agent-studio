using System.Net.Sockets;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Runs the blocking command on behalf of one coding attempt without exposing runner credentials.</summary>
internal sealed class AuthorCheckCommandHost : IAsyncDisposable
{
    private sealed record Command(Guid RequestId, string Name, string[] Additions, bool Reconnect);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DirectoryInfo _directory;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _captures = new(1);
    private readonly Task _loop;

    public string SocketPath { get; }

    private AuthorCheckCommandHost(TaskServerClient client, AuthorCheckParent parent, string worktree, CancellationToken ct)
    {
        _directory = Directory.CreateTempSubdirectory("studio-check-");
        SocketPath = Path.Combine(_directory.FullName, "command.sock");
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(8);
        }
        catch
        {
            _listener.Dispose();
            _directory.Delete(recursive: true);
            throw;
        }
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = ServeAsync(client, parent, worktree);
    }

    public static AuthorCheckCommandHost? Start(
        TaskServerClient client, RunOutboxAuthority? authority, string? projectId, string worktree, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || authority is null
            || (Environment.GetEnvironmentVariable("RUNNER_AUTHOR_CHECK_ATTEMPT") != authority.RunId
                && (projectId is null || Environment.GetEnvironmentVariable("RUNNER_AUTHOR_CHECK_PROJECT") != projectId)))
            return null;
        return new(client, new(authority.RunId, authority.RunnerId, authority.InstanceId, authority.LeaseId, authority.Fence),
            worktree, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        try { await _loop; }
        finally
        {
            _directory.Delete(recursive: true);
            _captures.Dispose();
            _stop.Dispose();
        }
    }

    private async Task ServeAsync(TaskServerClient client, AuthorCheckParent parent, string worktree)
    {
        var handlers = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                handlers.RemoveAll(task => task.IsCompleted);
                if (handlers.Count >= 8)
                    await Task.WhenAny(handlers).WaitAsync(_stop.Token);
                else
                    handlers.Add(HandleAsync(await _listener.AcceptAsync(_stop.Token), client, parent, worktree));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        finally { await Task.WhenAll(handlers); }
    }

    private async Task HandleAsync(Socket socket, TaskServerClient client, AuthorCheckParent parent, string worktree)
    {
        using var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            using var inputTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            inputTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var size = new byte[4];
            await stream.ReadExactlyAsync(size, inputTimeout.Token);
            var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(size);
            if (count is < 1 or > 16 * 1024) return;
            var payload = new byte[count];
            await stream.ReadExactlyAsync(payload, inputTimeout.Token);
            var command = JsonSerializer.Deserialize<Command>(payload, Json);
            if (command is null || command.Name != AuthorCheckProtocol.Name || command.Additions is null
                || command.RequestId == Guid.Empty || command.Additions.Length > 64
                || command.Additions.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 1024))
                throw new InvalidDataException("The pilot accepts only the named build and explicit file additions.");

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            wait.CancelAfter(TimeSpan.FromMinutes(35));
            var ct = wait.Token;
            var receipt = await SubmitOrReconnectAsync(client, parent, command, worktree, ct);
            while (receipt.Status is "queued" or "running")
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                try { receipt = await client.AuthorCheckReceiptAsync(parent, receipt.CheckId, ct); }
                catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            }
            await JsonSerializer.SerializeAsync(stream, new { receipt }, Json, _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or OperationCanceledException
                                   or TaskServerException or HttpRequestException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            if (_stop.IsCancellationRequested) return;
            var message = ex is InvalidDataException ? ex.Message
                : ex is TaskServerException server ? server.ErrorCode ?? "server-request-failed" : "command-host-unavailable";
            try { await JsonSerializer.SerializeAsync(stream, new { error = message }, Json, _stop.Token); }
            catch (Exception failure) when (failure is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task<AuthorCheckReceipt> SubmitOrReconnectAsync(
        TaskServerClient client, AuthorCheckParent parent, Command command, string worktree, CancellationToken ct)
    {
        var requestKey = command.RequestId.ToString("N");
        await _captures.WaitAsync(ct);
        try
        {
            if (command.Reconnect)
                return await client.AuthorCheckReceiptAsync(parent, requestKey, ct);
            var profile = await client.AuthorCheckProfileAsync(parent, ct);
            var capture = Directory.CreateDirectory(Path.Combine(_directory.FullName, command.RequestId.ToString("N")));
            try
            {
                var snapshot = await AuthorCheckGit.CaptureAsync(worktree, capture.FullName, profile.Base, command.Additions, ct);
                var submission = new AuthorCheckSubmission(parent, command.RequestId, command.Name, snapshot);
                var bundle = Path.Combine(capture.FullName, "source.bundle");
                try { return await client.SubmitAuthorCheckAsync(submission, bundle, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is (HttpRequestException or TaskCanceledException))
                {
                    // The first upload may already have committed. Replay the same immutable submission.
                    return await client.SubmitAuthorCheckAsync(submission, bundle, ct);
                }
            }
            finally { capture.Delete(recursive: true); }
        }
        finally { _captures.Release(); }
    }
}
