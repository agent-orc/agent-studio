using System.Net;
using System.Text;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// A host's App tokens on disk, and what real Git does with them. Tokens are
/// generated per test run.
/// </summary>
public sealed class RepositoryCredentialSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repository-credential-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
    private string StateDir => Path.Combine(_root, "state");
    private string GitStore => RepositoryCredentialPaths.GitStore(StateDir);
    private string ReviewStore => RepositoryCredentialPaths.ReviewStore(StateDir);

    public RepositoryCredentialSyncTests() => Directory.CreateDirectory(StateDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string NewToken() => "token-" + Guid.NewGuid().ToString("N");

    private RepositoryCredential Credential(string url, string token, string access = RepositoryAccess.Write)
        => new(url, GitHubAppBroker.CredentialUsername, token, _clock.GetUtcNow().UtcDateTime.AddHours(1), access);

    private (RepositoryCredentialSync Sync, StubServer Server) Build(string role, string? keyFile = null)
    {
        var server = new StubServer();
        var options = new RunnerOptions
        {
            ServerUrl = "http://127.0.0.1:5030",
            RunnerId = "runner-1",
            RunnerName = "runner-1",
            Hostname = "host",
            BackendName = "backend",
            BaseBranch = "main",
            WorkDir = Path.Combine(_root, "work"),
            StateDir = StateDir,
            Role = role,
            RepositoryKeyFile = keyFile,
        };
        var client = new TaskServerClient(new HttpClient(server) { BaseAddress = new Uri(options.ServerUrl) }, options.RunnerId);
        return (new RepositoryCredentialSync(options, client, _ => { }, _clock), server);
    }

    [Theory]
    [InlineData("coding", RepositoryAccess.Write)]
    [InlineData("integration", RepositoryAccess.Write)]
    [InlineData("review", RepositoryAccess.Read)]
    [InlineData("post", RepositoryAccess.Read)]
    public async Task The_role_decides_what_a_host_asks_for(string role, string access)
    {
        var (sync, server) = Build(role);
        server.Respond(HttpStatusCode.NoContent);

        await sync.SyncOnceAsync(default);

        Assert.Equal("/api/v1/runners/runner-1/repository-credentials", server.LastPath);
        using var body = JsonDocument.Parse(server.LastBody!);
        Assert.Equal(access, body.RootElement.GetProperty("access").GetString());
    }

    [Fact]
    public async Task A_host_key_travels_as_a_bearer_and_never_into_a_file()
    {
        var keyFile = Path.Combine(_root, "host.key");
        var key = "key-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(keyFile, key + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var (sync, server) = Build("coding", keyFile);
        server.Respond([Credential("https://github.com/owner/site.git", NewToken())]);

        await sync.SyncOnceAsync(default);

        Assert.Equal("Bearer " + key, server.LastAuthorization);
        Assert.DoesNotContain(key, File.ReadAllText(GitStore), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_file_others_can_read_is_refused_and_keeps_the_old_tokens_until_expiry()
    {
        if (OperatingSystem.IsWindows()) return;
        var keyFile = Path.Combine(_root, "open.key");
        File.WriteAllText(keyFile, "key");
        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.OtherRead);
        var (sync, server) = Build("coding", keyFile);

        var wait = await sync.SyncOnceAsync(default);

        Assert.Null(server.LastPath);
        Assert.Equal(TimeSpan.FromSeconds(60), wait);
    }

    [Fact]
    public async Task Coding_tokens_land_in_an_owner_only_store_and_no_review_store()
    {
        var token = NewToken();
        var (sync, server) = Build("coding");
        server.Respond([Credential("https://github.com/Owner/Site.git", token)]);

        var wait = await sync.SyncOnceAsync(default);

        Assert.Equal(TimeSpan.FromSeconds(GitHubAppBroker.HostRefreshSeconds), wait);
        var store = File.ReadAllLines(GitStore);
        Assert.Contains($"https://x-access-token:{token}@github.com/Owner/Site.git", store);
        Assert.Contains($"https://x-access-token:{token}@github.com/Owner/Site", store);
        Assert.Contains($"https://x-access-token:{token}@github.com/owner/site.git", store);
        Assert.False(File.Exists(ReviewStore));
        var config = File.ReadAllText(RepositoryCredentialPaths.GitConfig(StateDir));
        Assert.Contains("[credential \"https://github.com/Owner/Site.git\"]", config, StringComparison.Ordinal);
        Assert.DoesNotContain(token, config, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(GitStore));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(GitStore)!));
        }
    }

    [Fact]
    public async Task A_review_host_proves_its_tokens_like_configured_credentials()
    {
        var appToken = NewToken();
        var configuredToken = NewToken();
        var (sync, server) = Build("review");
        server.Respond([Credential("https://github.com/Owner/Site.git", appToken, RepositoryAccess.Read)]);
        var file = Path.Combine(_root, "review.credentials");
        File.WriteAllLines(file,
        [
            $"https://reader:{configuredToken}@github.com/owner/site",
            $"https://reader:{configuredToken}@github.com/owner/other",
        ]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await sync.SyncOnceAsync(default);

        var effective = ReviewRepositoryCredentials.LoadForHost(StateDir, file);
        Assert.Equal(2, effective.Entries.Count);
        // The App token wins: it is the one being renewed.
        var site = effective.Find("https://github.com/owner/site.git")!;
        Assert.Equal(appToken, site.Password);
        Assert.Equal("x-access-token", site.Username);
        Assert.Equal(configuredToken, effective.Find("https://github.com/owner/other")!.Password);
        Assert.Contains("review:repository-read:github.com/owner/site", effective.CapabilityKeys);
        Assert.Contains("review:repository-read:github.com/owner/other", effective.CapabilityKeys);
    }

    [Fact]
    public async Task Without_app_tokens_a_host_keeps_its_configured_credentials()
    {
        var file = Path.Combine(_root, "review.credentials");
        File.WriteAllText(file, $"https://reader:{NewToken()}@github.com/owner/site\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var effective = ReviewRepositoryCredentials.LoadForHost(StateDir, file);

        Assert.Equal("reader", Assert.Single(effective.Entries).Username);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_disconnected_app_or_a_revoked_host_loses_its_tokens_at_once(HttpStatusCode status)
    {
        var (sync, server) = Build("review");
        server.Respond([Credential("https://github.com/owner/site.git", NewToken(), RepositoryAccess.Read)]);
        await sync.SyncOnceAsync(default);
        Assert.True(File.Exists(GitStore));
        Assert.True(File.Exists(ReviewStore));

        server.Respond(status);
        await sync.SyncOnceAsync(default);

        Assert.False(File.Exists(GitStore));
        Assert.False(File.Exists(RepositoryCredentialPaths.GitConfig(StateDir)));
        Assert.False(File.Exists(ReviewStore));
    }

    [Fact]
    public async Task An_unreadable_answer_is_a_failed_round_and_not_the_end_of_renewal()
    {
        var (sync, server) = Build("coding");
        server.RespondRaw("not json");

        var wait = await sync.SyncOnceAsync(default);

        Assert.Equal(TimeSpan.FromSeconds(60), wait);
    }

    [Fact]
    public async Task While_the_server_is_away_tokens_stay_until_they_expire()
    {
        var (sync, server) = Build("coding");
        server.Respond([Credential("https://github.com/owner/site.git", NewToken())]);
        await sync.SyncOnceAsync(default);

        server.Respond(HttpStatusCode.ServiceUnavailable);
        var wait = await sync.SyncOnceAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(60), wait);
        Assert.True(File.Exists(GitStore));

        _clock.Advance(TimeSpan.FromMinutes(61));
        await sync.SyncOnceAsync(default);
        Assert.False(File.Exists(GitStore));
    }

    [Fact]
    public async Task Git_uses_each_token_for_its_own_repository_only()
    {
        var site = NewToken();
        var core = NewToken();
        var environment = Installed(
            Credential("https://github.com/Owner/Site.git", site),
            Credential("https://github.com/owner/core", core));

        Assert.Contains("password=" + site, (await Fill("Owner/Site.git", environment)).StdOut);
        Assert.Contains("password=" + site, (await Fill("owner/site", environment)).StdOut);
        Assert.Contains("password=" + core, (await Fill("owner/core.git", environment)).StdOut);
        Assert.Contains("username=x-access-token", (await Fill("owner/core", environment)).StdOut);
        Assert.DoesNotContain("password=", (await Fill("owner/unrelated.git", environment)).StdOut);
    }

    [Fact]
    public async Task A_token_stays_off_every_other_host()
    {
        var token = NewToken();
        var environment = Installed(Credential("https://github.com/owner/site.git", token));

        var filled = await ProcessRunner.RunAsync("git", ["credential", "fill"], workingDirectory: _root,
            stdin: "protocol=https\nhost=github.com.evil.example\npath=owner/site.git\n\n", environment: environment);

        Assert.DoesNotContain(token, filled.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_used_token_never_lands_in_the_hosts_long_lived_store()
    {
        var token = NewToken();
        var longLived = Path.Combine(_root, "long-lived-credentials");
        var before = $"https://person:{NewToken()}@github.com/owner/other.git\n";
        File.WriteAllText(longLived, before);
        var environment = Installed([$"[credential]\n\thelper = store --file={longLived.Replace('\\', '/')}\n"],
            Credential("https://github.com/owner/site.git", token));

        // What Git does after a successful push, and after a refused one.
        var approve = await Report("approve", "owner/site.git", token, environment);
        var reject = await Report("reject", "owner/site.git", token, environment);

        Assert.Equal(0, approve.ExitCode);
        Assert.Equal(0, reject.ExitCode);
        Assert.Equal(before, File.ReadAllText(longLived));
        Assert.Contains(token, File.ReadAllText(Path.Combine(_root, "git-credentials")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_wide_credential_line_still_serves_a_repository_the_app_misses()
    {
        var oldToken = NewToken();
        var longLived = Path.Combine(_root, "long-lived-credentials");
        File.WriteAllText(longLived, $"https://person:{oldToken}@github.com\n");
        var environment = Installed([$"[credential]\n\thelper = store --file={longLived.Replace('\\', '/')}\n"],
            Credential("https://github.com/owner/site.git", NewToken()));

        var filled = await Fill("owner/other.git", environment);

        Assert.Contains("username=person", filled.StdOut);
        Assert.Contains("password=" + oldToken, filled.StdOut);
    }

    [Fact]
    public async Task A_helper_for_one_other_repository_keeps_working()
    {
        var firstRun = NewToken();
        var helperStore = Path.Combine(_root, "first-run-credentials");
        File.WriteAllText(helperStore, $"https://x-access-token:{firstRun}@github.com/owner/first.git\n");
        var environment = Installed(
            [$"[credential \"https://github.com/owner/first.git\"]\n\thelper = store --file={helperStore.Replace('\\', '/')}\n\tuseHttpPath = true\n"],
            Credential("https://github.com/owner/site.git", NewToken()));

        var filled = await Fill("owner/first.git", environment);

        Assert.Contains("password=" + firstRun, filled.StdOut);
    }

    [Fact]
    public async Task The_app_wins_over_the_hosts_older_credential_for_a_covered_repository()
    {
        var token = NewToken();
        var oldToken = NewToken();
        var longLived = Path.Combine(_root, "long-lived-credentials");
        File.WriteAllText(longLived, $"https://person:{oldToken}@github.com/owner/site.git\n");
        var environment = Installed(
            [$"[credential]\n\thelper = store --file={longLived.Replace('\\', '/')}\n\tuseHttpPath = true\n"],
            Credential("https://github.com/owner/site.git", token));

        var filled = await Fill("owner/site.git", environment);

        Assert.Contains("password=" + token, filled.StdOut);
        Assert.DoesNotContain(oldToken, filled.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_that_selects_its_own_helper_is_not_overridden()
    {
        var token = NewToken();
        var own = NewToken();
        var ownStore = Path.Combine(_root, "own-credentials");
        File.WriteAllText(ownStore, $"https://candidate:{own}@github.com/owner/site.git\n");
        var environment = Installed(Credential("https://github.com/owner/site.git", token));

        var filled = await ProcessRunner.RunAsync("git",
            ["-c", "credential.helper=", "-c", $"credential.helper=store --file='{ownStore.Replace('\\', '/')}'", "credential", "fill"],
            workingDirectory: _root,
            stdin: "protocol=https\nhost=github.com\npath=owner/site.git\n\n",
            environment: environment);

        Assert.Contains("password=" + own, filled.StdOut);
        Assert.DoesNotContain(token, filled.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_state_directory_still_yields_an_absolute_include()
    {
        Assert.True(Path.IsPathFullyQualified(RepositoryCredentialPaths.GitConfig("relative/state")));
        Assert.True(Path.IsPathFullyQualified(RepositoryCredentialPaths.GitStore("relative/state")));
    }

    [Fact]
    public void A_process_that_isolates_its_git_configuration_stays_untouched()
    {
        var environment = new Dictionary<string, string?> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "0" };

        RepositoryGitCredentialFiles.Include(environment, "/state/git-credentials.config");

        Assert.Equal("0", environment["GIT_CONFIG_COUNT"]);
        Assert.False(environment.ContainsKey("GIT_CONFIG_KEY_0"));
    }

    [Fact]
    public void The_config_is_included_once_after_what_the_process_already_holds()
    {
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.longpaths",
            ["GIT_CONFIG_VALUE_0"] = "true",
        };

        RepositoryGitCredentialFiles.Include(environment, "C:\\state\\git-credentials.config");
        RepositoryGitCredentialFiles.Include(environment, "C:\\state\\git-credentials.config");

        Assert.Equal("2", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("core.longpaths", environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("include.path", environment["GIT_CONFIG_KEY_1"]);
        Assert.Equal("C:/state/git-credentials.config", environment["GIT_CONFIG_VALUE_1"]);
    }

    [Fact]
    public async Task A_path_with_a_space_and_a_quote_reaches_the_store()
    {
        var token = NewToken();
        var folder = Path.Combine(_root, "state dir's");
        Directory.CreateDirectory(folder);
        var environment = Installed(folder, [], Credential("https://github.com/owner/site.git", token));

        Assert.Contains("password=" + token, (await Fill("owner/site.git", environment)).StdOut);
    }

    private Dictionary<string, string?> Installed(params RepositoryCredential[] credentials)
        => Installed(_root, [], credentials);

    private Dictionary<string, string?> Installed(IReadOnlyList<string> hostConfig, params RepositoryCredential[] credentials)
        => Installed(_root, hostConfig, credentials);

    /// <summary>The two files a sync writes, plus a host configuration, as one process sees them.</summary>
    private Dictionary<string, string?> Installed(string folder, IReadOnlyList<string> hostConfig, params RepositoryCredential[] credentials)
    {
        var store = Path.Combine(folder, "git-credentials");
        var config = store + ".config";
        RepositoryGitCredentialFiles.WriteProtected(store, RepositoryGitCredentialFiles.RenderGitStore(credentials));
        RepositoryGitCredentialFiles.WriteProtected(config, RepositoryGitCredentialFiles.RenderGitConfig(store, credentials));
        var global = Path.Combine(_root, "global.gitconfig");
        File.WriteAllText(global, string.Concat(hostConfig));
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_GLOBAL"] = global,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["HOME"] = _root,
        };
        RepositoryGitCredentialFiles.Include(environment, config);
        return environment;
    }

    private Task<ProcessResult> Fill(string path, IReadOnlyDictionary<string, string?> environment)
        => ProcessRunner.RunAsync("git", ["credential", "fill"], workingDirectory: _root,
            stdin: $"protocol=https\nhost=github.com\npath={path}\n\n",
            environment: environment);

    private Task<ProcessResult> Report(string action, string path, string token, IReadOnlyDictionary<string, string?> environment)
        => ProcessRunner.RunAsync("git", ["credential", action], workingDirectory: _root,
            stdin: $"protocol=https\nhost=github.com\npath={path}\nusername=x-access-token\npassword={token}\n\n",
            environment: environment);

    private sealed class StubServer : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.NoContent;
        private string? _body;

        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastAuthorization { get; private set; }

        public void Respond(HttpStatusCode status)
        {
            _status = status;
            _body = null;
        }

        public void RespondRaw(string body)
        {
            _status = HttpStatusCode.OK;
            _body = body;
        }

        public void Respond(IReadOnlyList<RepositoryCredential> credentials)
        {
            _status = HttpStatusCode.OK;
            _body = JsonSerializer.Serialize(new RepositoryCredentialsResponse(credentials, GitHubAppBroker.HostRefreshSeconds),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(_status);
            if (_body is not null) response.Content = new StringContent(_body, Encoding.UTF8, "application/json");
            return response;
        }
    }
}
