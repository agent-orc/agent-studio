using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ResearchMagazineDeclarationTests(MagazineEndpointTests.Host host)
    : IClassFixture<MagazineEndpointTests.Host>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // clock-independent: reporting dates are serialized data and are never compared with now.
    private MagazineDeliverableRequest Audience => new("workspace:" + host.AlphaWorkspaceId,
        PersonaId: "security-officer", RoleId: "security-officer",
        ReportingWindow: new(new DateOnly(2026, 11, 21), new DateOnly(2026, 11, 27)));

    [Fact]
    public async Task Normal_create_and_detail_keep_the_declaration_and_commission_prompt()
    {
        var id = await Create("Magazine commission", Audience with { Scope = Audience.Scope.ToUpperInvariant() });
        var detail = await Detail(id);
        var declaration = detail.GetProperty("info").GetProperty("magazineDeliverable")
            .Deserialize<MagazineDeliverableDeclaration>(Json)!;

        Assert.Equal(Audience.Scope, declaration.Scope);
        Assert.Equal(Audience.PersonaId, declaration.PersonaId);
        Assert.Equal(Audience.RoleId, declaration.RoleId);
        Assert.Equal(Audience.ReportingWindow, declaration.ReportingWindow);
        Assert.StartsWith("human:", declaration.AuthorizedBy, StringComparison.Ordinal);
        Assert.Equal("awaiting-publication", declaration.PublicationStatus);
        Assert.Equal(MagazineDeliverableContract.ResultFiles, declaration.ResultFiles);
        var viewer = await host.Send("GET", $"/api/tasks/{id}?project={host.AlphaId}", null, host.AlphaViewerSession);
        Assert.Equal(HttpStatusCode.OK, viewer.Status);
        using var readerBody = JsonDocument.Parse(viewer.Body);
        Assert.Equal(Audience.Scope, readerBody.RootElement.GetProperty("info")
            .GetProperty("magazineDeliverable").GetProperty("scope").GetString());
        Assert.Equal(detail.GetProperty("info").GetProperty("key").GetString()!.ToLowerInvariant(), declaration.Slug);

        var task = host.Services.GetRequiredService<TaskScannerService>().FindJob(id)!;
        var prompt = File.ReadAllText(Path.Combine(task.FolderPath, "prompt.md"));
        foreach (var expected in new[] { "Acceptance authorizes publication", Audience.Scope,
                     "results/report.html", "results/article.md", "results/article.json", "results/items.json",
                     "`title`, `summary`, `kind`, `persona`, and `sources`", "integer `importance`" })
            Assert.Contains(expected, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_undeclared_research_card_round_trips_and_can_be_attached_once_by_key()
    {
        var id = await Create("Existing weekly report", null);
        var info = (await Detail(id)).GetProperty("info");
        Assert.False(info.TryGetProperty("magazineDeliverable", out _));
        var key = info.GetProperty("key").GetString()!;
        var task = host.Services.GetRequiredService<TaskScannerService>().FindJob(key)!;
        var submitted = Path.Combine(task.FolderPath, "results", "article.json");
        Directory.CreateDirectory(Path.GetDirectoryName(submitted)!);
        const string article = """{"title":"Weekly research","scope":"installation","slug":"other"}""";
        File.WriteAllText(submitted, article);

        var path = $"/api/tasks/{key}/magazine-deliverable";
        var attach = new AttachMagazineDeliverableRequest(Audience with { Slug = "weekly-report" });
        Assert.Equal(HttpStatusCode.OK, (await host.Send("PUT", path, attach, host.OwnerSession)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.Send("PUT", path, attach, host.OwnerSession)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Send("PUT", path,
            new AttachMagazineDeliverableRequest(Audience with { Slug = "different" }), host.OwnerSession)).Status);

        var declaration = (await Detail(key)).GetProperty("info").GetProperty("magazineDeliverable");
        Assert.Equal(Audience.Scope, declaration.GetProperty("scope").GetString());
        Assert.Equal("weekly-report", declaration.GetProperty("slug").GetString());
        Assert.Equal(article, File.ReadAllText(submitted));
    }

    [Fact]
    public async Task Intake_refuses_wrong_mode_malformed_audience_invalid_window_and_missing_target_authority()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await SendCreate("coding", Audience)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendCreate("research", Audience with { Scope = "workspace:../outside" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendCreate("research", Audience with
        {
            ReportingWindow = new(new DateOnly(2026, 11, 28), new DateOnly(2026, 11, 21)),
        })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendCreate("research", Audience with { PersonaId = "unknown" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendCreate("research", null, host.AlphaOperatorToken)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendCreate("research", Audience, host.AlphaOperatorToken)).Status);
    }

    [Fact]
    public async Task Historical_attachment_confirms_the_exact_acceptance_commit_and_audience()
    {
        var id = await Create("Accepted historical research", null);
        var task = host.Services.GetRequiredService<TaskScannerService>().FindJob(id)!;
        var taskPath = Path.Combine(task.FolderPath, "task.json");
        Assert.True(TaskJsonFile.UpdateField(task.FolderPath, "state", "5-human-review",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        foreach (var result in MagazineDeliverableContract.ResultFiles)
        {
            var file = Path.Combine(task.FolderPath, result);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "reviewed");
        }
        var root = task.WatchPath;
        var relativeFolder = Path.GetRelativePath(root, task.FolderPath);
        var relativeMetadata = Path.Combine(relativeFolder, Path.GetFileName(taskPath));
        var relativeResults = Path.Combine(relativeFolder, "results");
        Git(root, "init", "-q");
        Git(root, "add", relativeMetadata, relativeResults);
        Git(root, "commit", "-qm", "Review bundle");
        Assert.True(TaskJsonFile.UpdateField(task.FolderPath, "state", "6-completed",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        Git(root, "add", relativeMetadata);
        Git(root, "commit", "-qm", "Accept research");
        var version = "git-" + Git(root, "rev-parse", "HEAD");
        var scanner = host.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache(changedPath: taskPath);
        scanner.PublishCoreFromKnownFolder(task.FolderPath);

        var path = $"/api/tasks/{task.Key}/magazine-deliverable";
        var attach = new AttachMagazineDeliverableRequest(Audience, version);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Send("PUT", path, attach, host.OwnerSession)).Status);
        var later = Path.Combine(task.FolderPath, MagazineDeliverableContract.ResultFiles[0]);
        File.WriteAllText(later, "edited after acceptance");
        Git(root, "add", relativeResults);
        Git(root, "commit", "-qm", "Edit the work copy");
        var laterVersion = "git-" + Git(root, "rev-parse", "HEAD");
        Assert.Equal(HttpStatusCode.Conflict, (await host.Send("POST", path + "/preview",
            attach with { AcceptedResultVersion = laterVersion }, host.OwnerSession)).Status);
        var shown = await host.Send("POST", path + "/preview", attach, host.OwnerSession);
        Assert.Equal(HttpStatusCode.OK, shown.Status);
        var preview = JsonSerializer.Deserialize<MagazineDeliverablePreview>(shown.Body, Json)!;
        Assert.Equal(Audience.Scope, preview.Scope);
        Assert.Equal(version, preview.AcceptedResultVersion);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Send("PUT", path,
            attach with { MagazineDeliverable = Audience with { Scope = "installation" }, Confirmation = preview.Confirmation },
            host.OwnerSession)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.Send("PUT", path,
            attach with { Confirmation = preview.Confirmation }, host.OwnerSession)).Status);
        Assert.Equal(version, (await Detail(id)).GetProperty("info").GetProperty("magazineDeliverable")
            .GetProperty("acceptedResultVersion").GetString());
        Assert.Equal("edited after acceptance", File.ReadAllText(later));
    }

    private async Task<string> Create(string title, MagazineDeliverableRequest? declaration)
    {
        var response = await SendCreate("research", declaration, title: title);
        Assert.True(response.Status == HttpStatusCode.OK, response.Body);
        using var body = JsonDocument.Parse(response.Body);
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private Task<MagazineEndpointTests.Reply> SendCreate(string mode, MagazineDeliverableRequest? declaration,
        string? bearer = null, string title = "Invalid commission")
        => host.Send("POST", "/api/tasks", new
        {
            title, mode, project = host.AlphaId, promptMarkdown = "Research the week.",
            magazineDeliverable = declaration, cliType = "codex", model = "gpt-6-sol", thinkingLevel = "medium",
        }, bearer is null ? host.OwnerSession : null, bearer);

    private async Task<JsonElement> Detail(string id)
    {
        var response = await host.Send("GET", $"/api/tasks/{id}?project={host.AlphaId}", null, host.OwnerSession);
        Assert.True(response.Status == HttpStatusCode.OK, response.Body);
        using var body = JsonDocument.Parse(response.Body);
        return body.RootElement.Clone();
    }

    private static string Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Fixture";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.test";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000) && process.ExitCode == 0, error);
        return output.Trim();
    }
}
