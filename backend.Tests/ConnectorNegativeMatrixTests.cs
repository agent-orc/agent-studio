using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentStudio.Connector;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;
using CreateWorkspaceRequest = AgentStudio.TaskServer.Contracts.CreateWorkspaceRequest;
using static AgentStudio.Tests.ConnectorBrowser;

namespace AgentStudio.Tests;

/// <summary>
/// The connector negative-test matrix (gate 4 of
/// docs/operations/remote-task-server-local-studio.md) against a real Task
/// Server process in bearer mode. The connector runs in-process with its
/// production transport and, on Linux, its production owner-only credential
/// file. Every row is written to connector-negative-matrix.md/.json in the
/// deployment regression scenario report directory, so the release evidence
/// bundle carries it next to the scenario report.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class ConnectorNegativeMatrixTests
{
    private const string Origin = ConnectorOptions.DefaultStudioOrigin;

    [Fact(Timeout = 240000)]
    public async Task Connector_negative_matrix_against_a_real_task_server()
    {
        var root = RepositoryRoot();
        using var work = new TempDir("connector-matrix-");
        var studioToken = $"studio.{Guid.NewGuid():N}";
        var engineToken = $"engine.{Guid.NewGuid():N}";
        var serverUrl = $"http://127.0.0.1:{BuiltProcessLauncher.FreePort()}";
        using var server = BuiltProcessLauncher.StartBuilt(
            root,
            "task-server",
            "task-server.dll",
            new Dictionary<string, string?>
            {
                ["AUTH"] = "bearer",
                ["STUDIO_AUTH_TOKEN"] = studioToken,
                ["ENGINE_AUTH_TOKEN"] = engineToken,
            },
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", Path.Combine(work.Path, "data"));
        await ProcessWaiters.WaitForHttpAsync(serverUrl + "/readyz", server);

        var credential = new CredentialStore(Path.Combine(work.Path, "studio-connector.credential"));
        credential.Write(studioToken);
        var matrix = new MatrixReport(serverUrl);

        using (var direct = new HttpClient { BaseAddress = new Uri(serverUrl) })
        {
            direct.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            using var absent = await direct.GetAsync("/api/v1/workspaces");
            await matrix.RecordAsync("absent-bearer", "Task Server direct read without a bearer", 401, "authentication-required", absent);

            using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/v1/workspaces");
            invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-" + studioToken);
            using var invalidResponse = await direct.SendAsync(invalid);
            await matrix.RecordAsync("invalid-bearer", "Task Server direct read with an invalid bearer", 401, "authentication-required", invalidResponse);
        }

        await using (var connector = ConnectorUnderTest.Boot(Setup(serverUrl, credential)))
        {
            var client = connector.Client;
            var csrf = await CsrfAsync(client);

            using (var accepted = Mutation(HttpMethod.Post, "/api/workspaces", Origin, csrf, new CreateWorkspaceRequest("matrix-control")))
                await matrix.RecordAsync("control", "Studio mutation with Origin, session, and CSRF reaches the Task Server", 201, null, await client.SendAsync(accepted));

            credential.Remove();
            using (var read = new HttpRequestMessage(HttpMethod.Get, "/api/workspaces"))
            {
                await matrix.RecordAttachAsync("absent-bearer", "Connector without a stored Studio credential",
                    ConnectorCredentialFailureCodes.Unavailable, await client.SendAsync(read));
            }

            credential.Write("invalid-" + studioToken);
            using (var read = new HttpRequestMessage(HttpMethod.Get, "/api/workspaces"))
            {
                await matrix.RecordAttachAsync("invalid-bearer", "Connector holding a credential the Task Server rejects",
                    ConnectorAttachFailureCodes.CredentialRejected, await client.SendAsync(read));
            }

            credential.Write(studioToken);
            using (var rotated = new HttpRequestMessage(HttpMethod.Get, "/api/workspaces"))
                await matrix.RecordAsync("control", "Credential rotated back in place is used without a restart", 200, null, await client.SendAsync(rotated));

            using (var crossOrigin = Mutation(HttpMethod.Post, "/api/workspaces", "http://evil.example", csrf, new CreateWorkspaceRequest("cross-origin")))
                await matrix.RecordAsync("cross-origin", "Mutation from a foreign Origin", 403, ConnectorRequestPolicy.OriginRejected, await client.SendAsync(crossOrigin));
            using (var foreignLoopback = Mutation(HttpMethod.Post, "/api/workspaces", "http://127.0.0.1:8080", csrf, new CreateWorkspaceRequest("foreign-loopback")))
                await matrix.RecordAsync("cross-origin", "Mutation from an unconfigured loopback Origin", 403, ConnectorRequestPolicy.OriginRejected, await client.SendAsync(foreignLoopback));
            using (var noOrigin = Mutation(HttpMethod.Post, "/api/workspaces", null, csrf, new CreateWorkspaceRequest("no-origin")))
                await matrix.RecordAsync("cross-origin", "Mutation without an Origin header", 403, ConnectorRequestPolicy.OriginRequired, await client.SendAsync(noOrigin));
            using (var noSession = Mutation(HttpMethod.Post, "/api/workspaces", Origin, csrf, new CreateWorkspaceRequest("no-session")))
            {
                using var fresh = connector.NewCookielessBrowser();
                await matrix.RecordAsync("csrf", "Mutation without a connector session", 401, ConnectorRequestPolicy.SessionRequired, await fresh.SendAsync(noSession));
            }
            using (var missing = Mutation(HttpMethod.Post, "/api/workspaces", Origin, null, new CreateWorkspaceRequest("missing-csrf")))
                await matrix.RecordAsync("csrf", "Mutation without the CSRF token", 403, ConnectorRequestPolicy.CsrfRejected, await client.SendAsync(missing));

            using var otherBrowser = connector.NewBrowser();
            using var otherSession = await SessionAsync(otherBrowser);
            var otherCookie = ReadCookie(otherSession, ConnectorSessionStore.SessionCookieName);
            var otherToken = ReadCookie(otherSession, ConnectorSessionStore.CsrfCookieName);
            using (var crossSession = Mutation(HttpMethod.Post, "/api/workspaces", Origin, csrf, new CreateWorkspaceRequest("replayed")))
                await matrix.RecordAsync("csrf", "Replayed token: another session's CSRF token", 403, ConnectorRequestPolicy.CsrfRejected, await otherBrowser.SendAsync(crossSession));
            using (var logout = Mutation(HttpMethod.Delete, "/connector/session", Origin, otherToken))
                Assert.Equal(HttpStatusCode.NoContent, (await otherBrowser.SendAsync(logout)).StatusCode);
            using (var afterLogout = Mutation(HttpMethod.Post, "/api/workspaces", Origin, otherToken, new CreateWorkspaceRequest("after-logout")))
            {
                afterLogout.Headers.Add("Cookie", $"{ConnectorSessionStore.SessionCookieName}={otherCookie}; {ConnectorSessionStore.CsrfCookieName}={otherToken}");
                using var replay = connector.NewCookielessBrowser();
                await matrix.RecordAsync("csrf", "Replayed token: session cookie and token after logout", 401, ConnectorRequestPolicy.SessionRequired, await replay.SendAsync(afterLogout));
            }
        }

        foreach (var (probe, range, expected) in new[]
                 {
                     ("Connector offering only /api/v1 protocol 3-4", new ConnectorProtocolRange(3, 4, 1, 1), ConnectorAttachFailureCodes.ProtocolIncompatible),
                     ("Connector offering only Studio hub protocol 2", new ConnectorProtocolRange(1, 2, 2, 2), ConnectorAttachFailureCodes.HubProtocolIncompatible),
                 })
        {
            await using var mismatched = ConnectorUnderTest.Boot(Setup(serverUrl, credential) with { Protocols = range });
            var mismatchCsrf = await CsrfAsync(mismatched.Client);
            using var request = Mutation(HttpMethod.Post, "/api/workspaces", Origin, mismatchCsrf, new CreateWorkspaceRequest("mismatch"));
            await matrix.RecordAttachAsync("protocol-mismatch", probe, expected, await mismatched.Client.SendAsync(request));
        }

        using (var verify = new HttpClient { BaseAddress = new Uri(serverUrl) })
        {
            verify.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", studioToken);
            verify.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            var names = (await verify.GetFromJsonAsync<JsonElement>("/api/v1/workspaces"))
                .EnumerateArray()
                .Select(workspace => workspace.GetProperty("name").GetString())
                .ToArray();
            matrix.Record(
                "control",
                "Only the accepted control mutation created a workspace",
                "exactly one workspace, named matrix-control",
                names.Length == 0 ? "no workspaces" : string.Join(", ", names),
                names is ["matrix-control"]);
        }

        var (markdown, json) = matrix.Write(root);
        Assert.True(matrix.Passed, $"Connector negative matrix failed; see {markdown} and {json}.{Environment.NewLine}{matrix.Failures}");
    }

    private static ConnectorTestSetup Setup(string serverUrl, CredentialStore credential) => new(
        Credentials: credential.Source,
        Settings: new Dictionary<string, string?>
        {
            ["Connector:Upstream:Mode"] = "local",
            ["Connector:Upstream:BaseUrl"] = serverUrl,
            ["Connector:Upstream:MaskedName"] = "matrix-task-server",
            ["Connector:CredentialFile"] = credential.Path,
            ["Connector:CredentialRefreshSeconds"] = "0",
        });

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            for (var current = start; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            }
        }
        throw new DirectoryNotFoundException("Repository root with agent-taskboard.sln was not found.");
    }

    /// <summary>
    /// The operator's credential store for the connector under test: the
    /// production owner-only file on Linux and macOS. Windows CI has no
    /// writable Credential Manager entry for the test user, so there the same
    /// rotations go through an in-memory source.
    /// </summary>
    private sealed class CredentialStore(string path)
    {
        private readonly MutableCredentialSource? _windows = OperatingSystem.IsWindows() ? new MutableCredentialSource(null) : null;

        public string Path { get; } = path;
        public IConnectorCredentialSource? Source => _windows;

        public void Write(string value)
        {
            if (_windows is not null)
            {
                _windows.Rotate(value);
                return;
            }
            File.WriteAllText(Path, value);
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Remove()
        {
            if (_windows is not null) _windows.Rotate(null);
            else File.Delete(Path);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(string prefix) => Path = Directory.CreateTempSubdirectory(prefix).FullName;
        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException exception) { SilentCatch.Note(exception, "Matrix temp cleanup is best effort."); }
        }
    }

    private sealed record MatrixRow(string Category, string Probe, string Expected, string Observed, bool Passed);

    private sealed class MatrixReport(string serverUrl)
    {
        private readonly List<MatrixRow> _rows = [];

        public bool Passed => _rows.All(row => row.Passed);
        public string Failures => string.Join(Environment.NewLine, _rows.Where(row => !row.Passed)
            .Select(row => $"- {row.Category}: {row.Probe}: expected {row.Expected}, observed {row.Observed}"));

        public void Record(string category, string probe, string expected, string observed, bool passed)
            => _rows.Add(new MatrixRow(category, probe, expected, observed, passed));

        public async Task RecordAsync(string category, string probe, int status, string? code, HttpResponseMessage response)
        {
            using (response)
            {
                var observedCode = await CodeAsync(response);
                var passed = (int)response.StatusCode == status && (code is null || code == observedCode);
                Record(category, probe, Describe(status, code), Describe((int)response.StatusCode, observedCode), passed);
            }
        }

        public async Task RecordAttachAsync(string category, string probe, string reason, HttpResponseMessage response)
        {
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync();
                string? code = null, observedReason = null, message = null;
                try
                {
                    var json = JsonDocument.Parse(body).RootElement;
                    code = json.TryGetProperty("code", out var c) ? c.GetString() : null;
                    observedReason = json.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    message = json.TryGetProperty("message", out var m) ? m.GetString() : null;
                }
                catch (JsonException exception)
                {
                    SilentCatch.Note(exception, "A non-JSON body is recorded as a failed row.");
                }
                var passed = response.StatusCode == HttpStatusCode.ServiceUnavailable
                    && code == ConnectorProxy.AttachRefusedCode
                    && observedReason == reason
                    && !string.IsNullOrWhiteSpace(message);
                Record(
                    category,
                    probe,
                    $"503 {ConnectorProxy.AttachRefusedCode} / {reason}",
                    $"{(int)response.StatusCode} {code} / {observedReason}: {message}",
                    passed);
            }
        }

        public (string Markdown, string Json) Write(string root)
        {
            var directory = Environment.GetEnvironmentVariable("SCENARIO_REPORT_DIR")
                ?? Environment.GetEnvironmentVariable("JOB_RESULTS_DIR")
                ?? System.IO.Path.Combine(root, "artifacts", "scenario-reports");
            Directory.CreateDirectory(directory);
            var markdownPath = System.IO.Path.Combine(directory, "connector-negative-matrix.md");
            var jsonPath = System.IO.Path.Combine(directory, "connector-negative-matrix.json");

            var markdown = new StringBuilder()
                .AppendLine("# Connector negative-test matrix")
                .AppendLine()
                .AppendLine($"Generated {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} by `ConnectorNegativeMatrixTests` against a real `task-server.dll` in bearer mode at {serverUrl}.")
                .AppendLine($"Connector {ConnectorUpstreamTransport.ConnectorVersion}, /api/v1 protocol {TaskServerProtocol.MinimumSupported}-{TaskServerProtocol.MaximumSupported}, Studio hub protocol {TaskServerHubProtocol.MinimumSupported}-{TaskServerHubProtocol.MaximumSupported}. Credential store: {(OperatingSystem.IsWindows() ? "in-memory (Windows CI)" : "owner-only file (0600)")}.")
                .AppendLine()
                .AppendLine($"Result: **{(Passed ? "Passed" : "Failed")}** ({_rows.Count(row => row.Passed)}/{_rows.Count} rows).")
                .AppendLine()
                .AppendLine("| Category | Probe | Expected | Observed | Result |")
                .AppendLine("|---|---|---|---|---|");
            foreach (var row in _rows)
                markdown.AppendLine($"| {row.Category} | {Cell(row.Probe)} | {Cell(row.Expected)} | {Cell(row.Observed)} | {(row.Passed ? "Passed" : "Failed")} |");
            File.WriteAllText(markdownPath, markdown.ToString());
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(
                new { passed = Passed, generatedAtUtc = DateTimeOffset.UtcNow, rows = _rows },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return (markdownPath, jsonPath);
        }

        private static string Describe(int status, string? code) => code is null ? status.ToString() : $"{status} {code}";
        private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
    }
}
