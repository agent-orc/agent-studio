using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

// clock-independent: the workspace never compares lease or subject times with now.

/// <summary>
/// AGT-3077: every Remote Review of a private website repository failed before
/// any command ran, because the review clone is credential-free on purpose.
/// These tests clone through a local Basic-auth smart-HTTP server: the scoped
/// read credential reaches only the git clone and fetch processes, never a
/// review command, and a missing credential is named instead of reading as an
/// anonymous clone failure.
/// </summary>
public sealed class ReviewPrivateRepositoryTests : IAsyncLifetime
{
    private const string RepositoryPath = "owner/private-site.git";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "review-private-repository-" + Guid.NewGuid().ToString("N"));
    // Built at runtime: no credential-shaped literal in source.
    private readonly string _token = "fixture-" + Guid.NewGuid().ToString("N");
    private BasicAuthGitServer _server = null!;
    private string _baselineSha = string.Empty;
    private string _subjectSha = string.Empty;

    private string RepositoryUrl => $"{_server.BaseUrl}{RepositoryPath}";

    public async Task InitializeAsync()
    {
        var projectRoot = Path.Combine(_root, "served");
        var origin = Path.Combine(projectRoot, RepositoryPath);
        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(projectRoot);
        await GitAsync(_root, "init", "--bare", origin);
        await GitAsync(_root, "init", "-b", "main", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "baseline product");
        await GitAsync(seed, "add", "product.txt");
        await CommitAsync(seed, "baseline");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "origin", "main");
        _baselineSha = (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
        await GitAsync(seed, "checkout", "-b", "task/private-change");
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "subject product");
        await GitAsync(seed, "add", "product.txt");
        await CommitAsync(seed, "subject");
        await GitAsync(seed, "push", "origin", "task/private-change");
        _subjectSha = (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
        _server = BasicAuthGitServer.Start(projectRoot, "reviewer", _token);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        ResilientDirectory.TryDelete(_root);
    }

    [Fact]
    public async Task Private_clone_with_scoped_credential_succeeds_and_review_commands_never_see_it()
    {
        var workspace = Workspace("attempt-private", WriteCredentials(RepositoryUrl));

        var prepared = await workspace.PrepareAsync(null!, default);
        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal(_subjectSha, prepared.ActualHead);
        Assert.Equal("Pass", evidence.Outcome);
        Assert.True(_server.AuthorizedRequests > 0);
        var commandEnvironment = await File.ReadAllTextAsync(CommandEnvironmentPath("attempt-private"));
        Assert.Contains("AGENT_REVIEW_NAMESPACE=", commandEnvironment, StringComparison.Ordinal);
        Assert.DoesNotContain(_token, commandEnvironment, StringComparison.Ordinal);
        Assert.DoesNotContain("AGENT_REVIEW_GIT_", commandEnvironment, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.ProcessEnvironment(), pair =>
            (pair.Value ?? string.Empty).Contains(_token, StringComparison.Ordinal)
            || pair.Key.StartsWith("AGENT_REVIEW_GIT_", StringComparison.Ordinal));
        var origin = await GitAsync(workspace.RepositoryPath, "config", "--get", "remote.origin.url");
        Assert.DoesNotContain(_token, origin.StdOut, StringComparison.Ordinal);
        Assert.True(await workspace.CleanupAsync());
    }

    [Fact]
    public async Task Late_baseline_fetch_keeps_the_credential_away_from_config_written_by_review_commands()
    {
        var workspace = Workspace("attempt-baseline", WriteCredentials(RepositoryUrl));
        await workspace.PrepareAsync(null!, default);
        // A review command may rewrite the workspace config and hooks before
        // the integration ref is fetched for the baseline comparison.
        var hookLog = Path.Combine(workspace.TempPath, "hook-env.txt");
        var hooks = Path.Combine(workspace.RepositoryPath, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        foreach (var hook in new[] { "reference-transaction", "post-checkout", "pre-auto-gc" })
        {
            var path = Path.Combine(hooks, hook);
            await File.WriteAllTextAsync(path, $"#!/bin/sh\nenv >> '{hookLog}'\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await GitAsync(workspace.RepositoryPath, "config", "core.askPass", Path.Combine(hooks, "reference-transaction"));
        await GitAsync(workspace.RepositoryPath, "config", "credential.helper", "!env >> '" + hookLog + "'; true");

        var material = await workspace.BuildReviewMaterialAsync(10, 200, default);

        Assert.Contains("subject product", material, StringComparison.Ordinal);
        Assert.Contains(_baselineSha[..7], material, StringComparison.Ordinal);
        if (File.Exists(hookLog))
            Assert.DoesNotContain(_token, await File.ReadAllTextAsync(hookLog), StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(workspace.AttemptRoot, ".private-fetch-*"));
        Assert.True(await workspace.CleanupAsync());
    }

    [Fact]
    public async Task Private_clone_without_credential_names_the_missing_read_access()
    {
        var workspace = Workspace("attempt-anonymous", credentialsFile: null);

        var failure = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.PrepareAsync(null!, default));

        Assert.Equal("SnapshotUnavailable", failure.Classification);
        Assert.Contains("no read access", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ReviewRepositoryCredentials.FileVariable, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, _server.AuthorizedRequests);
        await workspace.CleanupAsync();
    }

    [Fact]
    public async Task Credential_scoped_to_another_repository_is_not_used()
    {
        var workspace = Workspace(
            "attempt-other-scope",
            WriteCredentials($"{_server.BaseUrl}owner/other-site.git"));

        var failure = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.PrepareAsync(null!, default));

        Assert.Equal("SnapshotUnavailable", failure.Classification);
        Assert.Equal(0, _server.AuthorizedRequests);
        await workspace.CleanupAsync();
    }

    [Theory]
    [InlineData("owner/private-site.git", true)]
    [InlineData("Owner/Private-Site", true)]
    [InlineData("owner/other-site.git", false)]
    [InlineData("owner/private-site/extra.git", false)]
    public async Task Credential_helper_answers_only_its_own_repository(string requestedPath, bool answered)
    {
        var credential = Assert.Single(ReviewRepositoryCredentials.Parse(
            [$"http://reviewer:{_token}@127.0.0.1:{_server.Port}/{RepositoryPath}"]).Entries);
        var workspace = Workspace("attempt-helper", credentialsFile: null);
        Directory.CreateDirectory(workspace.TempPath);
        Directory.CreateDirectory(workspace.HomePath);

        var result = await ProcessRunner.RunAsync(
            "git",
            RemoteReviewWorkspace.CredentialedGitArguments(credential, "credential", "fill"),
            _root,
            stdin: $"protocol=http\nhost=127.0.0.1:{_server.Port}\npath={requestedPath}\n\n",
            environment: workspace.CredentialedGitEnvironment(credential),
            clearEnvironment: true);

        Assert.Equal(answered, result.Success);
        Assert.Equal(answered, result.StdOut.Contains($"password={_token}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Credential_helper_refuses_another_host()
    {
        var credential = Assert.Single(ReviewRepositoryCredentials.Parse(
            [$"http://reviewer:{_token}@127.0.0.1:{_server.Port}/{RepositoryPath}"]).Entries);
        var workspace = Workspace("attempt-helper-host", credentialsFile: null);
        Directory.CreateDirectory(workspace.TempPath);
        Directory.CreateDirectory(workspace.HomePath);

        var result = await ProcessRunner.RunAsync(
            "git",
            RemoteReviewWorkspace.CredentialedGitArguments(credential, "credential", "fill"),
            _root,
            stdin: $"protocol=http\nhost=example.invalid\npath={RepositoryPath}\n\n",
            environment: workspace.CredentialedGitEnvironment(credential),
            clearEnvironment: true);

        Assert.False(result.Success);
        Assert.DoesNotContain(_token, result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_probe_proves_a_working_token_and_rejects_a_revoked_one()
    {
        var working = new ReviewRepositoryReadProbe();
        var revoked = new ReviewRepositoryReadProbe();
        var key = ReviewRepositoryAccessPolicy.ReadCapability(RepositoryUrl)!;
        var log = new List<string>();

        await working.RefreshDueAsync(WriteCredentials(RepositoryUrl), log.Add, default);
        await revoked.RefreshDueAsync(WriteCredentials(RepositoryUrl, "revoked-" + _token), log.Add, default);

        Assert.Equal(ReviewRepositoryReadProbe.Ready, working.Proofs[key].Status);
        Assert.Equal(ReviewRepositoryReadProbe.Unavailable, revoked.Proofs[key].Status);
        Assert.DoesNotContain(log, line => line.Contains(_token, StringComparison.Ordinal));
        Assert.DoesNotContain(_token, revoked.Proofs[key].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_review_never_reads_the_private_credentials_file()
    {
        // A credentials file that turned malformed after start-up used to fail
        // every review, public ones included, with SnapshotUnavailable.
        var broken = Path.Combine(_root, "broken-review-git-credentials");
        await File.WriteAllTextAsync(broken, "not a credential line\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var origin = Path.Combine(_root, "served", RepositoryPath);
        var workspace = Workspace("attempt-public", broken, privateRepository: false, repositoryUrl: origin);

        var prepared = await workspace.PrepareAsync(null!, default);

        Assert.Equal(_subjectSha, prepared.ActualHead);
        Assert.Equal(0, _server.AuthorizedRequests);
        Assert.True(await workspace.CleanupAsync());
    }

    private string CommandEnvironmentPath(string attemptId) => Path.Combine(_root, attemptId + "-command-env.txt");

    private string WriteCredentials(string repositoryUrl, string? token = null)
    {
        var uri = new Uri(repositoryUrl);
        var path = Path.Combine(_root, "review-git-credentials-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, $"{uri.Scheme}://reviewer:{token ?? _token}@{uri.Authority}{uri.AbsolutePath}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private RemoteReviewWorkspace Workspace(
        string attemptId,
        string? credentialsFile,
        bool privateRepository = true,
        string? repositoryUrl = null)
    {
        repositoryUrl ??= RepositoryUrl;
        IReadOnlyList<ReviewCommandDto> commands =
        [
            new("verify", "build-tests", "sh", ["-c", $"env > '{CommandEnvironmentPath(attemptId)}'"]),
        ];
        var subject = new ReviewSubjectDto(
            "subject-" + attemptId,
            "task-" + attemptId,
            "run-" + attemptId,
            RepositoryIdentityContract.FromUrl(repositoryUrl) ?? "local-origin",
            repositoryUrl,
            _subjectSha,
            "refs/heads/task/private-change",
            null,
            null,
            "coding-host",
            "policy",
            new ReviewPlanDto(
                commands,
                ["build-tests"],
                IntegrationRef: "refs/heads/main",
                PrivateRepository: privateRepository),
            DateTime.UnixEpoch);
        var lease = new ReviewLeaseDto(
            "lease-" + attemptId,
            attemptId,
            subject.SubjectId,
            "review-executor",
            "review-instance",
            "review-host",
            1,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddMinutes(2),
            "active",
            $"review-{attemptId}-f1",
            24600);
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "review-executor",
            RunnerName = "review-executor",
            Hostname = "review-host",
            BackendName = "remote-review",
            Role = "review",
            WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = Path.Combine(_root, "review"),
            ReviewGitCredentialsFile = credentialsFile,
            BaseBranch = "main",
            ClaudeCliBin = "unused",
        };
        return new RemoteReviewWorkspace(options, subject, lease, _ => { },
            _ => Task.FromResult<FailureFingerprintHistoryDto?>(null));
    }

    private static Task<ProcessResult> CommitAsync(string repository, string message)
        => GitAsync(repository, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", message);

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }

    /// <summary>
    /// Smart-HTTP Git server on loopback that answers 401 to every request
    /// without the expected Basic credential, the way GitHub answers a private
    /// repository. Authorized requests are served by <c>git http-backend</c>.
    /// </summary>
    private sealed class BasicAuthGitServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _projectRoot;
        private readonly string _expectedAuthorization;
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private int _authorizedRequests;

        private BasicAuthGitServer(string projectRoot, string username, string password, int port)
        {
            _projectRoot = projectRoot;
            _expectedAuthorization = "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{username}:{password}"));
            Port = port;
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
        }

        public int Port { get; }
        public string BaseUrl { get; }
        public int AuthorizedRequests => Volatile.Read(ref _authorizedRequests);

        public static BasicAuthGitServer Start(string projectRoot, string username, string password)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var server = new BasicAuthGitServer(projectRoot, username, password, port);
            server._listener.Start();
            server._loop = server.AcceptAsync();
            return server;
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                if (!string.Equals(context.Request.Headers["Authorization"], _expectedAuthorization,
                        StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 401;
                    context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"private\"");
                    context.Response.Close();
                    return;
                }
                Interlocked.Increment(ref _authorizedRequests);
                await ServeAsync(context);
            }
            catch
            {
                try { context.Response.Abort(); } catch { /* connection already gone */ }
            }
        }

        private async Task ServeAsync(HttpListenerContext context)
        {
            var start = new ProcessStartInfo("git", "http-backend")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.Environment["GIT_PROJECT_ROOT"] = _projectRoot;
            start.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
            start.Environment["REQUEST_METHOD"] = context.Request.HttpMethod;
            start.Environment["PATH_INFO"] = context.Request.Url!.AbsolutePath;
            start.Environment["QUERY_STRING"] = context.Request.Url.Query.TrimStart('?');
            start.Environment["CONTENT_TYPE"] = context.Request.ContentType ?? string.Empty;
            start.Environment["REMOTE_USER"] = "reviewer";
            start.Environment["REMOTE_ADDR"] = "127.0.0.1";
            if (context.Request.Headers["Content-Encoding"] is { } encoding)
                start.Environment["HTTP_CONTENT_ENCODING"] = encoding;
            if (context.Request.Headers["Git-Protocol"] is { } protocol)
                start.Environment["GIT_PROTOCOL"] = protocol;
            using var process = Process.Start(start)!;
            var output = new MemoryStream();
            var copyOut = process.StandardOutput.BaseStream.CopyToAsync(output);
            var drainError = process.StandardError.ReadToEndAsync();
            await context.Request.InputStream.CopyToAsync(process.StandardInput.BaseStream);
            process.StandardInput.Close();
            await copyOut;
            await drainError;
            await process.WaitForExitAsync();

            var bytes = output.ToArray();
            var split = IndexOf(bytes, "\r\n\r\n"u8.ToArray());
            var separatorLength = 4;
            if (split < 0)
            {
                split = IndexOf(bytes, "\n\n"u8.ToArray());
                separatorLength = 2;
            }
            var headers = Encoding.ASCII.GetString(bytes, 0, Math.Max(split, 0));
            foreach (var line in headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    context.Response.StatusCode = int.Parse(value.Split(' ')[0]);
                else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    context.Response.ContentType = value;
                else if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    context.Response.AddHeader(name, value);
            }
            var bodyStart = split < 0 ? 0 : split + separatorLength;
            context.Response.ContentLength64 = bytes.Length - bodyStart;
            await context.Response.OutputStream.WriteAsync(bytes.AsMemory(bodyStart));
            context.Response.Close();
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var index = 0; index <= haystack.Length - needle.Length; index++)
                if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle)) return index;
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            // Close only: on Linux the managed HttpListener releases the port in Stop()
            // and binds it again in a later Close(), which fails with "Address already
            // in use" once a parallel test has taken the port.
            _listener.Close();
            await _loop;
            _stop.Dispose();
        }
    }
}
