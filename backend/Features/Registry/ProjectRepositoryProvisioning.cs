using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Persistence;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Registry;

public sealed record CreateRepositoryRequest(string Owner, string Name, string Visibility, string Template);
public sealed record ProvisionedRepository(string Url, string CheckoutPath)
{
    internal RepositoryCreationAttempt Attempt { get; init; } = null!;
}

internal enum RepositoryCreationPhase { Creating, PromotingCheckout, RollingBackCheckout }

internal sealed record RepositoryCreationAttempt(CreateRepositoryRequest Request, Guid Id, long LoginId, long? RepositoryId,
    RepositoryCreationPhase Phase = RepositoryCreationPhase.Creating)
{
    public string Description => $"Created by Agent Studio (provisioning attempt {Id:N})";
}

public sealed class RepositoryProvisioningException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IProjectRepositoryProvisioner
{
    Task<ProvisionedRepository> CreateAsync(CreateRepositoryRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(ProvisionedRepository repository, CancellationToken cancellationToken);
}

public interface IProjectRepositoryCheckout
{
    Task CloneAsync(string url, string token, string path, CancellationToken cancellationToken);
}

public static partial class ProjectRepositoryTemplates
{
    public static readonly string[] Ids = ["static-web", "container-service"];

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex RepositoryPattern();

    public static string? Validate(CreateRepositoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Owner) || !OwnerPattern().IsMatch(request.Owner))
            return "Choose a valid GitHub user or organisation.";
        if (string.IsNullOrWhiteSpace(request.Name) || !RepositoryPattern().IsMatch(request.Name)
            || request.Name is "." or "..")
            return "Use a repository name with letters, numbers, dots, underscores or hyphens.";
        if (request.Visibility is not ("public" or "private")) return "Choose public or private visibility.";
        if (!Ids.Contains(request.Template, StringComparer.Ordinal)) return "Choose an available project template.";
        return null;
    }

    public static IReadOnlyDictionary<string, string> ReadFiles(string template, string root)
    {
        if (!Ids.Contains(template, StringComparer.Ordinal))
            throw new ArgumentException("Unknown project template.", nameof(template));
        var directory = Path.Combine(root, template);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Project template '{template}' is unavailable on this host.");
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "node_modules" or "dist"))
            .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), File.ReadAllText,
                StringComparer.Ordinal);
    }
}

/// <summary>Owns the provisioning credential and compensates a failed creation attempt.</summary>
public sealed class GitHubProjectRepositoryProvisioner(
    IHttpClientFactory clients, IConfiguration configuration, IProvisioningCredentialSource credentials,
    IProjectRepositoryCheckout checkout, IProjectRepositoryAppCoverage coverage,
    IAtomicJsonFileWriter filesWriter, ILogger<GitHubProjectRepositoryProvisioner> logger)
    : IProjectRepositoryProvisioner
{
    private const string ClientName = "github-project-provisioning";
    private const string AttemptMarker = "agent-studio-provisioning-attempt";
    private static readonly Uri Api = new("https://api.github.com/");
    private readonly string _templateRoot = configuration["ProjectOnboarding:TemplateRoot"]
        ?? Path.Combine(AppContext.BaseDirectory, "project-templates");

    public async Task<ProvisionedRepository> CreateAsync(CreateRepositoryRequest request, CancellationToken ct)
    {
        var validation = ProjectRepositoryTemplates.Validate(request);
        if (validation is not null) throw new RepositoryProvisioningException("invalid-request", validation);
        RequireAllowedOwner(request.Owner);

        try { return await CreateCoreAsync(request, ct); }
        catch (RepositoryProvisioningException) { throw; }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("GitHub provisioning failed for {Owner}/{Name} ({FailureType})",
                request.Owner, request.Name, exception.GetType().Name);
            throw new RepositoryProvisioningException("setup-failed", "Studio could not complete GitHub repository setup. Ask an operator to check provisioning.");
        }
    }

    private async Task<ProvisionedRepository> CreateCoreAsync(CreateRepositoryRequest request, CancellationToken ct)
    {
        var finalPath = CheckoutPath(request);
        var journal = JournalPath(request);
        using var lease = LockAttempt(journal);
        var pending = ReadAttempt(journal);
        if (Directory.Exists(finalPath) || File.Exists(finalPath))
        {
            // A checkout promoted to its final path without an explicit rollback can already be in use.
            if (pending is not { } rollback || !SameRequest(rollback.Request, request)
                || rollback.Phase != RepositoryCreationPhase.RollingBackCheckout
                || !OwnsCheckout(finalPath, rollback))
                throw CheckoutExists();
        }

        var token = await credentials.GetTokenAsync("github-repository-provision", ct);
        var files = ProjectRepositoryTemplates.ReadFiles(request.Template, _templateRoot);
        using var client = clients.CreateClient(ClientName);
        var user = await SendAsync(client, token, HttpMethod.Get, "user", null, ct);
        var userLogin = RequiredString(user, "login");
        var userId = PositiveId(user);
        var personalOwner = string.Equals(userLogin, request.Owner, StringComparison.OrdinalIgnoreCase);

        if (pending is not null)
        {
            if (!SameRequest(pending.Request, request) || pending.LoginId != userId)
                throw new RepositoryProvisioningException("attempt-conflict",
                    "An incomplete creation attempt uses this repository name. Retry its original request or ask an operator to check provisioning.");
            if (pending.Phase == RepositoryCreationPhase.PromotingCheckout)
            {
                if (!Directory.Exists(StagedCheckoutPath(journal, pending.Id)))
                    throw new RepositoryProvisioningException("attempt-conflict",
                        "The previous checkout may already be in use. Ask an operator to check the creation attempt.");
                pending = pending with { Phase = RepositoryCreationPhase.RollingBackCheckout };
                filesWriter.WriteJson(journal, pending);
            }
            await RecoverAsync(client, token, pending, journal, ct);
        }

        var attempt = new RepositoryCreationAttempt(request, Guid.NewGuid(), userId, null);
        var repository = new GitHubRepositoryName(request.Owner, request.Name);
        var prefix = RepositoryRoute(request);
        var createRoute = personalOwner ? "user/repos" : $"orgs/{request.Owner}/repos";
        filesWriter.WriteJson(journal, attempt);
        try
        {
            JsonElement created;
            try
            {
                created = await SendAsync(client, token, HttpMethod.Post, createRoute,
                    new { name = request.Name, description = attempt.Description, @private = request.Visibility == "private", auto_init = true },
                    ct, creating: true);
            }
            catch (RepositoryProvisioningException ex) when (ex.Code is "name-taken" or "missing-rights")
            {
                File.Delete(journal);
                throw;
            }

            attempt = attempt with { RepositoryId = PositiveId(created) };
            if (!MatchesRepository(created, attempt))
                throw new RepositoryProvisioningException("repository-mismatch", "GitHub returned a different repository for this creation attempt.");
            filesWriter.WriteJson(journal, attempt);

            await SeedAsync(client, token, prefix, RequiredString(created, "default_branch"), request.Template, files, ct);
            var stagedCheckout = StagedCheckoutPath(journal, attempt.Id);
            await checkout.CloneAsync(repository.CloneUrl, token, stagedCheckout, ct);
            var marker = MarkerPath(stagedCheckout);
            await File.WriteAllTextAsync(marker, attempt.Id.ToString("N"), ct);
            await HandOffAsync(client, token, repository, personalOwner, attempt.RepositoryId.Value, ct);
            ct.ThrowIfCancellationRequested();

            attempt = attempt with { Phase = RepositoryCreationPhase.PromotingCheckout };
            filesWriter.WriteJson(journal, attempt);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            try { Directory.Move(stagedCheckout, finalPath); }
            catch (IOException) when (Directory.Exists(finalPath) || File.Exists(finalPath)) { throw CheckoutExists(); }
            Directory.Delete(StagingPath(journal, attempt.Id));
            File.Delete(journal);
            return new ProvisionedRepository($"https://github.com/{request.Owner}/{request.Name}", finalPath) { Attempt = attempt };
        }
        catch
        {
            if (File.Exists(journal))
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    if (attempt.Phase == RepositoryCreationPhase.PromotingCheckout)
                    {
                        attempt = attempt with { Phase = RepositoryCreationPhase.RollingBackCheckout };
                        filesWriter.WriteJson(journal, attempt);
                    }
                    await RecoverAsync(client, token, attempt, journal, cleanupTimeout.Token);
                }
                catch (Exception exception)
                {
                    logger.LogWarning("GitHub provisioning cleanup is pending for {Owner}/{Name}, attempt {Attempt} ({FailureType})",
                        request.Owner, request.Name, attempt.Id, exception.GetType().Name);
                }
            }
            throw;
        }
    }

    public async Task DeleteAsync(ProvisionedRepository repository, CancellationToken ct)
    {
        var attempt = repository.Attempt with { Phase = RepositoryCreationPhase.RollingBackCheckout };
        var request = attempt.Request;
        RequireAllowedOwner(request.Owner);
        var journal = JournalPath(request);
        using var lease = LockAttempt(journal);
        if (ReadAttempt(journal) is { } pending && pending.Id != attempt.Id)
            throw new RepositoryProvisioningException("attempt-conflict", "A different GitHub provisioning attempt is active.");

        // Persist the rollback before removing either resource so a repeated request can finish it.
        filesWriter.WriteJson(journal, attempt);
        using var client = clients.CreateClient(ClientName);
        var token = await credentials.GetTokenAsync("github-repository-rollback", ct);
        var user = await SendAsync(client, token, HttpMethod.Get, "user", null, ct);
        if (PositiveId(user) != attempt.LoginId)
            throw new RepositoryProvisioningException("attempt-conflict", "The provisioning credential belongs to a different GitHub account.");
        await RecoverAsync(client, token, attempt, journal, ct);
    }

    private async Task HandOffAsync(HttpClient client, string token, GitHubRepositoryName repository,
        bool personalOwner, long repositoryId, CancellationToken ct)
    {
        try
        {
            var initial = await coverage.CheckAsync(repository, personalOwner, ct);
            if (initial.InstallationId is not > 0)
                throw AppCoverageRequired();
            if (initial.Covered) return;

            await SendAsync(client, token, HttpMethod.Put,
                FormattableString.Invariant($"user/installations/{initial.InstallationId}/repositories/{repositoryId}"),
                null, ct, allowNotModified: true);
            var verified = await coverage.CheckAsync(repository, personalOwner, ct);
            if (verified.Covered && verified.InstallationId == initial.InstallationId) return;
        }
        catch (Exception exception) when (exception is not RepositoryProvisioningException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("GitHub App coverage could not be checked for {Owner}/{Name} ({FailureType})",
                repository.Owner, repository.Name, exception.GetType().Name);
        }
        throw AppCoverageRequired();
    }

    private static RepositoryProvisioningException AppCoverageRequired() => new("app-coverage-required",
        "Studio could not verify GitHub App coverage. Ask an owner to check the adopted installation and its repository access.");

    private async Task RecoverAsync(
        HttpClient client, string token, RepositoryCreationAttempt attempt, string journal, CancellationToken ct)
    {
        try
        {
            var finalPath = CheckoutPath(attempt.Request);
            if (attempt.Phase == RepositoryCreationPhase.RollingBackCheckout && OwnsCheckout(finalPath, attempt))
                Directory.Delete(finalPath, recursive: true);

            var stage = StagingPath(journal, attempt.Id);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
        finally
        {
            var route = RepositoryRoute(attempt.Request);
            var remote = await SendAsync(client, token, HttpMethod.Get, route, null, ct, allowNotFound: true);
            if (remote.ValueKind != JsonValueKind.Undefined)
            {
                if (!MatchesRepository(remote, attempt))
                    throw new RepositoryProvisioningException("name-taken",
                        "The repository at this name belongs to a different creation attempt.");
                await SendAsync(client, token, HttpMethod.Delete, route, null, ct, allowNotFound: true);
            }
        }
        File.Delete(journal);
    }

    private static bool OwnsCheckout(string path, RepositoryCreationAttempt attempt)
    {
        var marker = MarkerPath(path);
        return File.Exists(marker) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            && !File.GetAttributes(Path.GetDirectoryName(marker)!).HasFlag(FileAttributes.ReparsePoint)
            && File.ReadAllText(marker) == attempt.Id.ToString("N");
    }

    private static async Task SeedAsync(HttpClient client, string token, string prefix, string branch, string template,
        IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var branchRoute = string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
        var reference = await SendAsync(client, token, HttpMethod.Get, $"{prefix}/git/ref/heads/{branchRoute}", null, ct);
        var parent = RequiredString(reference.GetProperty("object"), "sha");
        var parentCommit = await SendAsync(client, token, HttpMethod.Get, $"{prefix}/git/commits/{parent}", null, ct);
        var baseTree = RequiredString(parentCommit.GetProperty("tree"), "sha");
        var tree = await SendAsync(client, token, HttpMethod.Post, $"{prefix}/git/trees", new
        {
            base_tree = baseTree,
            tree = files.Select(file => new
            {
                path = file.Key, mode = file.Key == ".agent-studio/prepare" ? "100755" : "100644",
                type = "blob", content = file.Value,
            }).ToArray(),
        }, ct);
        var commit = await SendAsync(client, token, HttpMethod.Post, $"{prefix}/git/commits",
            new { message = $"Initialize {template} project", tree = RequiredString(tree, "sha"), parents = new[] { parent } }, ct);
        var sha = RequiredString(commit, "sha");
        await SeedBranchAsync(client, token, prefix, "main", branch, sha, ct);
        if (branch != "main")
            await SendAsync(client, token, HttpMethod.Patch, prefix, new { default_branch = "main" }, ct);
        await SeedBranchAsync(client, token, prefix, "develop", branch, sha, ct);
    }

    private static Task<JsonElement> SeedBranchAsync(HttpClient client, string token, string prefix, string target,
        string initialBranch, string sha, CancellationToken ct)
        => initialBranch == target
            ? SendAsync(client, token, HttpMethod.Patch, $"{prefix}/git/refs/heads/{target}", new { sha }, ct)
            : SendAsync(client, token, HttpMethod.Post, $"{prefix}/git/refs", new { @ref = $"refs/heads/{target}", sha }, ct);

    private void RequireAllowedOwner(string owner)
    {
        if (!configuration.GetSection("ProjectOnboarding:AllowedOwners").GetChildren()
                .Any(item => string.Equals(item.Value?.Trim(), owner, StringComparison.OrdinalIgnoreCase)))
            throw new RepositoryProvisioningException("owner-not-allowed",
                "This GitHub owner is not enabled for repository provisioning. Ask an operator to check the allowed owners.");
    }

    private string RepositoryRoot() => configuration["ProjectOnboarding:RepositoryRoot"] is { } root
        && !string.IsNullOrWhiteSpace(root) ? root : LocalStateRoot.Combine("AgentStudio", "repositories");

    private string CheckoutPath(CreateRepositoryRequest request) => Path.Combine(RepositoryRoot(), request.Owner, request.Name);

    private string JournalPath(CreateRepositoryRequest request) =>
        Path.Combine(RepositoryRoot(), ".provisioning", request.Owner.ToLowerInvariant(), request.Name.ToLowerInvariant() + ".json");

    private static string StagingPath(string journal, Guid id) => Path.Combine(Path.GetDirectoryName(journal)!, id.ToString("N"));
    private static string StagedCheckoutPath(string journal, Guid id) => Path.Combine(StagingPath(journal, id), "checkout");
    private static string MarkerPath(string checkoutPath) => Path.Combine(checkoutPath, ".git", AttemptMarker);

    private static FileStream LockAttempt(string journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        try { return new FileStream(journal + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            throw new RepositoryProvisioningException("attempt-busy", "Studio is already handling this repository name. Retry in a moment.");
        }
    }

    private static RepositoryCreationAttempt? ReadAttempt(string journal)
    {
        if (!File.Exists(journal)) return null;
        return JsonSerializer.Deserialize<RepositoryCreationAttempt>(File.ReadAllText(journal))
            ?? throw new InvalidDataException("The provisioning attempt is unreadable.");
    }

    private static string RepositoryRoute(CreateRepositoryRequest request) => $"repos/{request.Owner}/{request.Name}";
    private static bool SameRequest(CreateRepositoryRequest original, CreateRepositoryRequest current)
        => original.Visibility == current.Visibility && original.Template == current.Template
            && string.Equals(original.Owner, current.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(original.Name, current.Name, StringComparison.OrdinalIgnoreCase);

    private static string RequiredString(JsonElement json, string name) => json.GetProperty(name).GetString() is { Length: > 0 } value
        ? value : throw new InvalidDataException("GitHub returned incomplete provisioning metadata.");

    private static long PositiveId(JsonElement json) => json.GetProperty("id").GetInt64() is > 0 and var id
        ? id : throw new InvalidDataException("GitHub returned an invalid identifier.");

    private static bool MatchesRepository(JsonElement remote, RepositoryCreationAttempt attempt)
        => string.Equals(RequiredString(remote, "full_name"), $"{attempt.Request.Owner}/{attempt.Request.Name}", StringComparison.OrdinalIgnoreCase)
            && remote.GetProperty("description").GetString() == attempt.Description
            && (attempt.RepositoryId is null || PositiveId(remote) == attempt.RepositoryId);

    private static RepositoryProvisioningException CheckoutExists() => new("checkout-exists",
        "The local repository path already exists. Choose another repository name.");

    private static async Task<JsonElement> SendAsync(HttpClient client, string token, HttpMethod method,
        string route, object? body, CancellationToken ct, bool creating = false, bool allowNotFound = false,
        bool allowNotModified = false)
    {
        using var message = new HttpRequestMessage(method, new Uri(Api, route));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.ParseAdd("application/vnd.github+json");
        message.Headers.UserAgent.ParseAdd("AgentStudio-ProjectOnboarding/1.0");
        message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) message.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(message, ct);
        var status = response.StatusCode;
        if (status == HttpStatusCode.NoContent || (allowNotFound && status == HttpStatusCode.NotFound)
            || (allowNotModified && status == HttpStatusCode.NotModified)) return default;
        if (!response.IsSuccessStatusCode)
        {
            if (creating && status == HttpStatusCode.UnprocessableEntity)
                throw new RepositoryProvisioningException("name-taken", "GitHub rejected this repository name or it is already in use.");
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new RepositoryProvisioningException("missing-rights",
                    "GitHub denied a provisioning operation. Ask an operator to check the provisioning grant for this owner.");
            throw new RepositoryProvisioningException("github-error",
                $"GitHub could not complete repository setup (HTTP {(int)status}). Retry or ask an operator to check GitHub access.");
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.Clone();
    }
}

public sealed class GitProjectRepositoryCheckout : IProjectRepositoryCheckout
{
    public async Task CloneAsync(string url, string token, string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
            throw new RepositoryProvisioningException("checkout-exists", "The local repository path already exists. Choose another repository name.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var start = new ProcessStartInfo("git")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("clone");
        start.ArgumentList.Add("--branch");
        start.ArgumentList.Add("develop");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(url);
        start.ArgumentList.Add(path);
        start.Environment.Remove(SecretProvisioningCredentialSource.TokenName);
        start.Environment.Remove("GIT_CONFIG_PARAMETERS");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_TRACE_REDACT"] = "1";
        start.Environment["GIT_CONFIG_COUNT"] = "2";
        start.Environment["GIT_CONFIG_KEY_0"] = $"http.{url}.extraheader";
        start.Environment["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:" + token));
        start.Environment["GIT_CONFIG_KEY_1"] = "http.followRedirects";
        start.Environment["GIT_CONFIG_VALUE_1"] = "false";
        var result = await AgentStudio.Git.GitProcessBoundary.RunAsync(
            start, stdin: null, AgentStudio.Git.GitProcessBoundary.LongOperationTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.FailureKind is AgentStudio.Git.GitProcessFailureKind.StartFailure
            or AgentStudio.Git.GitProcessFailureKind.ResourceExhaustion)
            throw new RepositoryProvisioningException("clone-failed", "Git could not start the checkout. Check the Studio host installation.");
        if (!result.Success)
            throw new RepositoryProvisioningException("clone-failed", "The repository was created, but Studio could not check it out locally. Check host Git access and retry.");
    }
}
