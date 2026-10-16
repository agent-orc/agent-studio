using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Setup;

/// <summary>Issues a short-lived enrolment code through the native Task Server.</summary>
internal static class JoinTokenIssuer
{
    internal sealed record Request(string AuthorityUrl, string HostUrl, string Route, string RunnerId,
        string ReleaseVersion, string ManagementTokenFile, string? OwnerSessionFile, string OutputFile,
        string SshTarget = "agent-runner", string? OwnerName = null, string? OwnerPassword = null);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("join-token --runner-name NAME --join-token-file PATH [--route reverse-tunnel|lan|https] [--server-url URL] [--ssh-target HOST]");
                return 0;
            }
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("The join-token command runs on a Windows or Linux Task Server machine.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Join-token options must be --name value pairs.");
                values.Add(args[i], args[i + 1]);
            }
            string Required(string key) => values.TryGetValue(key, out var value) ? value
                : throw new ArgumentException($"join-token requires {key}.");
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "--runner-name", "--role", "--token-file", "--owner-session-file", "--join-token-file",
                "--route", "--server-url", "--authority-url", "--release-version", "--ssh-target",
            };
            if (values.Keys.FirstOrDefault(key => !allowed.Contains(key)) is { } unknown)
                throw new ArgumentException($"Unknown join-token option: {unknown}");
            var hostName = SetupValidation.RequireSimpleName(Required("--runner-name"), "Agent Host name");
            var role = values.GetValueOrDefault("--role", "coding");
            if (role is not ("coding" or "review"))
                throw new ArgumentException("--role must be coding or review.");
            var runnerId = role == "coding" ? hostName : $"{hostName}-review";
            var prompter = new ConsolePrompter(false);
            var route = values.GetValueOrDefault("--route")
                ?? prompter.Ask("Reachability (reverse-tunnel, lan, https)",
                    OperatingSystem.IsWindows() ? "reverse-tunnel" : "https");
            if (route == "reverse-tunnel" && !OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Automatic reverse tunnel setup requires the Windows tunnel keeper.");
            var hostUrl = route == "reverse-tunnel"
                ? values.GetValueOrDefault("--server-url", "http://127.0.0.1:15031")
                : values.GetValueOrDefault("--server-url")
                  ?? prompter.Ask("Task Server URL visible from the Linux host (https://...)");
            var request = new Request(
                values.GetValueOrDefault("--authority-url", WindowsServiceLayout.TaskServerUrl), hostUrl,
                route, runnerId,
                SetupOptions.NormalizeVersion(values.GetValueOrDefault("--release-version")) ?? ReleaseArtifacts.CurrentVersion(),
                values.GetValueOrDefault("--token-file", OperatingSystem.IsWindows()
                    ? Path.Combine(WindowsServiceLayout.ForHost(Path.GetTempPath()).ConfigRoot, "studio.token")
                    : Path.Combine(InstallPaths.Load().OrchestratorConfig, "studio.token")),
                values.GetValueOrDefault("--owner-session-file"), Required("--join-token-file"),
                values.GetValueOrDefault("--ssh-target", "agent-runner"),
                values.ContainsKey("--owner-session-file") ? null : prompter.Ask("Studio owner username"),
                values.ContainsKey("--owner-session-file") ? null : prompter.Secret("Studio owner password"));
            if (route == "reverse-tunnel" && OperatingSystem.IsWindows())
            {
                var scripts = Path.Combine(WindowsServiceLayout.ForHost(Path.GetTempPath()).InstallBase,
                    "current", "deploy-windows", "agent-runner-tunnel", "register-tunnel-keeper.ps1");
                await new ProcessRunner(false).RequireAsync("powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scripts,
                     "-SshTarget", request.SshTarget, "-RemotePort", "15031", "-OrchestratorPort", "5071",
                     "-TaskName", TunnelTaskName(hostName)]);
            }
            await IssueAsync(request);
            Console.WriteLine($"Join token written to {Path.GetFullPath(request.OutputFile)}. Transfer the protected file to the Linux host and run install.sh --join --join-token-file PATH.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Join token issuance failed: {error.Message}");
            return 1;
        }
    }

    internal static string TunnelTaskName(string hostName)
        => $"AgentRunner-TunnelKeeper-{SetupValidation.RequireSimpleName(hostName, "Agent Host name")}";

    internal static async Task<JoinPayload> IssueAsync(Request request, HttpClient? client = null,
        TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(request.OutputFile);
        if (File.Exists(path))
            throw new IOException("Join token file already exists. Choose a new output path.");
        var host = SetupValidation.RequireServerUrl(request.HostUrl, allowLoopbackHttp: true);
        _ = SetupValidation.RequireServerUrl(request.AuthorityUrl, allowLoopbackHttp: true);
        if (request.Route is not ("reverse-tunnel" or "lan" or "https"))
            throw new ArgumentException("--route must be reverse-tunnel, lan, or https.");
        if (request.Route == "reverse-tunnel" && (!host.IsLoopback || host.Scheme != "http"))
            throw new ArgumentException("A reverse tunnel must use its Linux loopback HTTP endpoint.");
        if (request.Route != "reverse-tunnel" && host.Scheme != "https")
            throw new ArgumentException("LAN and HTTPS routes require an HTTPS Task Server URL.");
        SetupSecrets.RequireProtected(request.ManagementTokenFile, "Management token file");
        if (request.OwnerSessionFile is not null)
            SetupSecrets.RequireProtected(request.OwnerSessionFile, "Owner session file");
        var management = (await File.ReadAllTextAsync(request.ManagementTokenFile, cancellationToken)).Trim();
        var session = request.OwnerSessionFile is null ? null
            : (await File.ReadAllTextAsync(request.OwnerSessionFile, cancellationToken)).Trim();
        if (management.Length == 0 || request.OwnerSessionFile is not null && session?.Length == 0)
            throw new InvalidDataException("The management token and owner session files must be nonempty.");
        var ownsClient = client is null;
        client ??= new HttpClient { BaseAddress = new Uri(request.AuthorityUrl), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var health = await client.GetAsync("/healthz", cancellationToken);
            health.EnsureSuccessStatusCode();
            if (session is null)
            {
                using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/studio/auth/login")
                {
                    Content = JsonContent.Create(new StudioLoginRequest(
                        request.OwnerName ?? throw new ArgumentException("Studio owner username is required."),
                        request.OwnerPassword ?? throw new ArgumentException("Studio owner password is required."))),
                };
                login.Headers.Authorization = new AuthenticationHeaderValue("Bearer", management);
                login.Headers.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
                login.Headers.Add(TaskServerProtocol.ClientVersionHeaderName, ReleaseArtifacts.CurrentVersion());
                login.Headers.Add("X-Client-Id", AuthorityApi.ClientId);
                using var loginResponse = await client.SendAsync(login, cancellationToken);
                if (!loginResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Studio owner login failed ({(int)loginResponse.StatusCode}).");
                session = (await loginResponse.Content.ReadFromJsonAsync<StudioAuthSessionDto>(cancellationToken: cancellationToken))?.SessionToken
                    ?? throw new InvalidDataException("Task Server returned no owner session.");
            }
            using var issue = new HttpRequestMessage(HttpMethod.Post, "/api/v1/management/enrolments")
            {
                Content = JsonContent.Create(new CreateEnrolmentRequest(
                    $"runner:{request.RunnerId}", TaskServerPrincipalKinds.Runner, request.RunnerId, 600)),
            };
            issue.Headers.Authorization = new AuthenticationHeaderValue("Bearer", management);
            issue.Headers.Add("X-Studio-Session-Token", session);
            issue.Headers.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            issue.Headers.Add(TaskServerProtocol.ClientVersionHeaderName, ReleaseArtifacts.CurrentVersion());
            issue.Headers.Add("X-Client-Id", AuthorityApi.ClientId);
            using var response = await client.SendAsync(issue, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Task Server refused enrolment issuance ({(int)response.StatusCode}). Verify the owner session and management token.");
            var issued = await response.Content.ReadFromJsonAsync<IssuedEnrolment>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Task Server returned an empty enrolment.");
            if (issued.RunnerId != request.RunnerId || issued.Kind != TaskServerPrincipalKinds.Runner)
                throw new InvalidDataException("Task Server returned the wrong Runner identity.");
            var payload = new JoinPayload(2, host.ToString().TrimEnd('/'), issued.EnrolmentCode,
                request.ReleaseVersion, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime,
                issued.InstallationId, issued.RunnerId, issued.ExpiresAt);
            await using (var reserved = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Reserve the name before setting permissions; no secret exists in the blank file.
            }
            if (OperatingSystem.IsWindows()) RestrictWindowsFile(path);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Join token path changed during creation.");
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(file))
            {
                await writer.WriteLineAsync(JoinTokenCodec.Encode(payload));
                await writer.FlushAsync(cancellationToken);
            }
            SetupSecrets.RequireProtected(path, "Join token file");
            return payload;
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsFile(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Could not identify the join token file owner.");
        var acl = new FileSecurity();
        acl.SetOwner(owner);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
}
