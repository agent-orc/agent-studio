using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Registry;

public sealed record CreateRepositoryRequest(string Owner, string Name, string Visibility, string Template);
public sealed record ProvisionedRepository(string Url, string CheckoutPath);

public sealed class RepositoryProvisioningException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IProjectRepositoryProvisioner
{
    Task<ProvisionedRepository> CreateAsync(CreateRepositoryRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(CreateRepositoryRequest request, string checkoutPath, CancellationToken cancellationToken);
}

public interface IProvisioningCredentialSource
{
    string GetToken();
}

public sealed class HostProvisioningCredentialSource : IProvisioningCredentialSource
{
    public string GetToken() =>
        Environment.GetEnvironmentVariable("AGENT_STUDIO_GITHUB_PROVISIONING_TOKEN") is { Length: > 0 } token
            ? token
            : throw new RepositoryProvisioningException("missing-credential",
                "GitHub repository creation is not configured. Ask an operator to set AGENT_STUDIO_GITHUB_PROVISIONING_TOKEN on the Studio host.");
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

/// <summary>Host-custody GitHub boundary. The token is read only from the host environment.</summary>
public sealed class GitHubProjectRepositoryProvisioner(
    IHttpClientFactory clients, IConfiguration configuration, IProvisioningCredentialSource credentials,
    IProjectRepositoryCheckout checkout, ILogger<GitHubProjectRepositoryProvisioner> logger)
    : IProjectRepositoryProvisioner
{
    private static readonly Uri Api = new("https://api.github.com/");
    private readonly string _templateRoot = configuration["ProjectOnboarding:TemplateRoot"]
        ?? Path.Combine(AppContext.BaseDirectory, "project-templates");

    public async Task<ProvisionedRepository> CreateAsync(CreateRepositoryRequest request, CancellationToken cancellationToken)
    {
        var validation = ProjectRepositoryTemplates.Validate(request);
        if (validation is not null) throw new RepositoryProvisioningException("invalid-request", validation);
        var token = credentials.GetToken();
        var files = ProjectRepositoryTemplates.ReadFiles(request.Template, _templateRoot);
        using var client = clients.CreateClient("github-project-provisioning");
        var owner = await SendAsync(client, token, HttpMethod.Get, "user", null, cancellationToken);
        var userLogin = owner.GetProperty("login").GetString();
        var route = string.Equals(userLogin, request.Owner, StringComparison.OrdinalIgnoreCase)
            ? "user/repos" : $"orgs/{request.Owner}/repos";
        var created = false;
        var checkoutPath = CheckoutPath(request);
        if (Directory.Exists(checkoutPath) || File.Exists(checkoutPath))
            throw new RepositoryProvisioningException("checkout-exists",
                "The local repository path already exists. Choose another repository name.");
        try
        {
            var repository = await SendAsync(client, token, HttpMethod.Post, route,
                new { name = request.Name, @private = request.Visibility == "private", auto_init = true }, cancellationToken);
            created = true;
            var url = repository.GetProperty("html_url").GetString()!;
            var branch = repository.GetProperty("default_branch").GetString()!;
            var prefix = $"repos/{request.Owner}/{request.Name}";
            var reference = await SendAsync(client, token, HttpMethod.Get,
                $"{prefix}/git/ref/heads/{branch}", null, cancellationToken);
            var parent = reference.GetProperty("object").GetProperty("sha").GetString()!;
            var parentCommit = await SendAsync(client, token, HttpMethod.Get,
                $"{prefix}/git/commits/{parent}", null, cancellationToken);
            var baseTree = parentCommit.GetProperty("tree").GetProperty("sha").GetString()!;
            var tree = await SendAsync(client, token, HttpMethod.Post, $"{prefix}/git/trees",
                new { base_tree = baseTree, tree = files.Select(file => new
                {
                    path = file.Key, mode = file.Key == ".agent-studio/prepare" ? "100755" : "100644",
                    type = "blob", content = file.Value,
                }).ToArray() }, cancellationToken);
            var commit = await SendAsync(client, token, HttpMethod.Post, $"{prefix}/git/commits",
                new { message = $"Initialize {request.Template} project", tree = tree.GetProperty("sha").GetString(), parents = new[] { parent } }, cancellationToken);
            var sha = commit.GetProperty("sha").GetString()!;
            if (branch == "main")
                await SendAsync(client, token, HttpMethod.Patch, $"{prefix}/git/refs/heads/main",
                    new { sha }, cancellationToken);
            else
            {
                await SendAsync(client, token, HttpMethod.Post, $"{prefix}/git/refs",
                    new { @ref = "refs/heads/main", sha }, cancellationToken);
                await SendAsync(client, token, HttpMethod.Patch, prefix,
                    new { default_branch = "main" }, cancellationToken);
            }
            await SendAsync(client, token, HttpMethod.Post, $"{prefix}/git/refs",
                new { @ref = "refs/heads/develop", sha }, cancellationToken);
            await checkout.CloneAsync(url, token, checkoutPath, cancellationToken);
            return new ProvisionedRepository(url, checkoutPath);
        }
        catch
        {
            if (created)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await DeleteRemoteAsync(client, token, request, cleanupTimeout.Token); }
                catch (Exception cleanupError)
                {
                    logger.LogWarning(cleanupError, "Repository cleanup failed for {Owner}/{Name}", request.Owner, request.Name);
                }
            }
            throw;
        }
    }

    public async Task DeleteAsync(CreateRepositoryRequest request, string checkoutPath, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient("github-project-provisioning");
        await DeleteRemoteAsync(client, credentials.GetToken(), request, cancellationToken);
        if (Directory.Exists(checkoutPath)) Directory.Delete(checkoutPath, recursive: true);
    }

    private static async Task DeleteRemoteAsync(HttpClient client, string token,
        CreateRepositoryRequest request, CancellationToken cancellationToken)
    {
        await SendAsync(client, token, HttpMethod.Delete,
            $"repos/{request.Owner}/{request.Name}", null, cancellationToken);
    }

    private string CheckoutPath(CreateRepositoryRequest request)
    {
        var root = configuration["ProjectOnboarding:RepositoryRoot"];
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentStudio", "repositories");
        return Path.Combine(root, request.Owner, request.Name);
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, string token, HttpMethod method,
        string route, object? body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, new Uri(Api, route));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.ParseAdd("application/vnd.github+json");
        message.Headers.UserAgent.ParseAdd("AgentStudio-ProjectOnboarding/1.0");
        message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) message.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode == HttpStatusCode.UnprocessableEntity ? "name-taken"
                : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "missing-rights"
                : "github-error";
            var detail = code switch
            {
                "name-taken" => "That repository name is already taken for this owner. Choose another name.",
                "missing-rights" => "The installation cannot create repositories for this owner. Ask an owner to grant repository creation rights.",
                _ => $"GitHub could not complete repository setup (HTTP {(int)response.StatusCode}). Retry or ask an operator to check GitHub access.",
            };
            throw new RepositoryProvisioningException(code, detail);
        }
        if (response.Content.Headers.ContentLength == 0) return default;
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
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
        start.Environment["GIT_CONFIG_COUNT"] = "1";
        start.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
        start.Environment["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:" + token));
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
