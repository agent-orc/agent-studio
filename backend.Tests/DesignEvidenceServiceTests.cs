using AgentStudio.Design;
using AgentStudio.Security;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DesignEvidenceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "design-evidence-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(_root, "repository");
    private string Outside => Path.Combine(_root, "outside");

    public DesignEvidenceServiceTests()
    {
        Directory.CreateDirectory(Repository);
        Directory.CreateDirectory(Outside);
    }

    private DesignEvidenceService Service(string? workspace = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "alpha",
            ["WatchPaths:0:Path"] = workspace ?? Repository,
            ["WatchPaths:0:RepositoryPath"] = Repository,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new DesignEvidenceService(scanner, NullLogger<DesignEvidenceService>.Instance);
    }

    [Theory]
    [InlineData(".md")]
    [InlineData(".json")]
    public void Leaf_links_are_refused_by_reads_lists_overview_and_acceptance(string extension)
    {
        var target = Write(Outside, "private" + extension, "---\ntitle: Outside title\nsummary: Outside summary\n---\nPrivate fixture body\n");
        var before = File.ReadAllBytes(target);
        foreach (var relative in new[] { "council/note.md", "references/note.md", "brief.md", "loop.md" })
            LinkFile("design/" + relative, target);
        var service = Service();

        Assert.Null(service.ReadCouncilNote("alpha", "note.md"));
        Assert.Null(service.ReadReference("alpha", "note.md"));
        Assert.Null(service.AcceptCouncilNote("alpha", "note.md"));
        Assert.Empty(service.ListCouncilNotes("alpha").Notes);
        Assert.Empty(service.ListReferences("alpha").References);
        var overview = service.GetOverview("alpha");
        Assert.False(overview.BriefExists);
        Assert.Null(overview.BriefSummary);
        Assert.Null(overview.StatusDetail);
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData("design")]
    [InlineData("design/council")]
    [InlineData("design/references")]
    public void Ancestor_links_are_refused_and_outside_files_stay_identical(string relative)
    {
        var targetDirectory = Path.Combine(Outside, "linked");
        var note = Write(targetDirectory, relative == "design" ? "council/note.md" : "note.md", "---\nsummary: Outside summary\n---\nOutside\n");
        if (relative == "design")
        {
            Write(targetDirectory, "references/note.md", File.ReadAllText(note));
            Write(targetDirectory, "brief.md", File.ReadAllText(note));
            Write(targetDirectory, "loop.md", File.ReadAllText(note));
        }
        var before = Directory.GetFiles(Outside, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var link = Path.Combine(Repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, targetDirectory);
        var service = Service();

        Assert.Null(service.ReadCouncilNote("alpha", "note.md"));
        Assert.Null(service.ReadReference("alpha", "note.md"));
        Assert.Null(service.AcceptCouncilNote("alpha", "note.md"));
        Assert.Empty(service.ListCouncilNotes("alpha").Notes);
        Assert.Empty(service.ListReferences("alpha").References);
        Assert.Null(service.GetOverview("alpha").BriefSummary);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Evidence_is_anchored_to_registered_repository_instead_of_task_workspace()
    {
        var workspace = Path.Combine(_root, "workspace");
        Write(workspace, "design/council/note.md", "Workspace decoy");
        var note = Write(Repository, "design/council/note.md", "Repository note");
        var service = Service(workspace);

        Assert.Equal(Path.Combine(Repository, "design"), service.ResolveDesignDir("alpha"));
        Assert.Equal("Repository note", service.ReadCouncilNote("alpha", "note.md"));
        Assert.NotNull(service.AcceptCouncilNote("alpha", "note.md"));
        Assert.Contains("acceptedAt:", File.ReadAllText(note));
        Assert.Equal("Workspace decoy", File.ReadAllText(Path.Combine(workspace, "design/council/note.md")));
    }

    [Theory]
    [InlineData("screenshot", "references/planned.png", "references/planned.png", false)]
    [InlineData("screenshot", "council/planned.png", "council/planned.png", false)]
    [InlineData("image", "/references/planned.png/", "references/planned.png", false)]
    [InlineData("image", "references\\planned.png", "references/planned.png", false)]
    [InlineData("screenshot", "planned.png", "planned.png", false)]
    [InlineData("image", "planned.png", "references/planned.png", true)]
    [InlineData("screenshot", "references/planned.png", "references/planned.png", true)]
    public void Explicit_screenshot_metadata_keeps_existing_resolution_even_before_image_creation(
        string field, string raw, string expected, bool imageExists)
    {
        Write(Repository, "design/references/reference.md", $"---\n{field}: {raw}\n---\nReference\n");
        if (imageExists) Write(Repository, "design/references/planned.png", "Disposable image fixture");
        var service = Service();

        var reference = Assert.Single(service.ListReferences("alpha").References);

        Assert.Equal(expected, reference.ScreenshotRelPath);
        Assert.True(reference.ParseOk);
        Assert.Equal(1, service.GetOverview("alpha").ReferencesCount);
    }

    [Fact]
    public void Sibling_image_inference_uses_only_a_regular_link_free_file()
    {
        Write(Repository, "design/references/reference.md", "Reference\n");
        var target = Write(Outside, "private.png", "Disposable outside image fixture");
        var before = File.ReadAllBytes(target);
        LinkFile("design/references/reference.png", target);
        Write(Repository, "design/references/reference.jpg", "Disposable inside image fixture");

        var reference = Assert.Single(Service().ListReferences("alpha").References);

        Assert.Equal("references/reference.jpg", reference.ScreenshotRelPath);
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_list_omits_evidence_replaced_by_a_link_before_open(bool reference)
    {
        var folder = reference ? "references" : "council";
        Write(Repository, $"design/{folder}/note.md", "Inside note");
        var target = Write(Outside, "private.json", "Disposable outside fixture");
        var before = File.ReadAllBytes(target);
        var service = Service();
        service.BeforeEvidenceOpen = () => Substitute("leaf", folder, target);

        if (reference) Assert.Empty(service.ListReferences("alpha").References);
        else Assert.Empty(service.ListCouncilNotes("alpha").Notes);

        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Acceptance_does_not_modify_an_outside_target(bool ancestor)
    {
        var target = Write(Outside, "note.md", "Disposable outside fixture\n");
        var before = File.ReadAllBytes(target);
        if (ancestor)
        {
            Directory.CreateDirectory(Path.Combine(Repository, "design"));
            Directory.CreateSymbolicLink(Path.Combine(Repository, "design/council"), Outside);
        }
        else LinkFile("design/council/note.md", target);

        var response = Service().AcceptCouncilNote("alpha", "note.md");

        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Null(response);
    }

    [Theory]
    [InlineData("leaf", false)]
    [InlineData("design", false)]
    [InlineData("council", false)]
    [InlineData("leaf", true)]
    [InlineData("design", true)]
    [InlineData("references", true)]
    public void Substituting_a_link_after_parent_open_never_discloses_outside_text(string segment, bool reference)
    {
        var folder = reference ? "references" : "council";
        var relative = $"design/{folder}/note.md";
        Write(Repository, relative, "Inside note");
        var target = Write(Outside, $"{folder}/note.md", "Private outside fixture");
        var service = Service();
        service.BeforeEvidenceOpen = () => Substitute(segment, folder, target);

        var text = reference ? service.ReadReference("alpha", "note.md") : service.ReadCouncilNote("alpha", "note.md");

        Assert.True(text is null or "Inside note");
        Assert.Equal("Private outside fixture", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("leaf")]
    [InlineData("design")]
    [InlineData("council")]
    public void Substituting_a_link_after_acceptance_validation_cannot_redirect_the_atomic_write(string segment)
    {
        Write(Repository, "design/council/note.md", "Inside note");
        var target = Write(Outside, "council/note.md", "Private outside fixture");
        var before = File.ReadAllBytes(target);
        var service = Service();
        service.BeforeAcceptReplace = () => Substitute(segment, "council", target);

        service.AcceptCouncilNote("alpha", "note.md");

        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(Outside, "*.tmp", SearchOption.AllDirectories));
    }

    private void Substitute(string segment, string folder, string target)
    {
        if (segment == "leaf")
        {
            var leaf = Path.Combine(Repository, $"design/{folder}/note.md");
            File.Delete(leaf);
            File.CreateSymbolicLink(leaf, target);
            return;
        }
        var path = Path.Combine(Repository, segment == "design" ? "design" : $"design/{folder}");
        Directory.Move(path, path + "-original");
        Directory.CreateSymbolicLink(path, segment == "design" ? Outside : Path.GetDirectoryName(target)!);
    }

    [Fact]
    public void Links_within_the_repository_and_linked_repository_roots_are_also_refused()
    {
        var target = Write(Repository, "private.md", "Internal fixture");
        LinkFile("design/council/note.md", target);
        Assert.Null(Service().ReadCouncilNote("alpha", "note.md"));
        Assert.Null(Service().AcceptCouncilNote("alpha", "note.md"));

        Directory.Move(Repository, Repository + "-original");
        Directory.CreateSymbolicLink(Repository, Repository + "-original");
        Assert.Null(Service().ReadCouncilNote("alpha", "note.md"));
        Assert.Null(Service().AcceptCouncilNote("alpha", "note.md"));
        Assert.False(Service().GetOverview("alpha").Exists);
    }

    [Fact]
    public void A_named_pipe_is_refused_without_reading_or_stamping_it()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(Repository, "design/council/note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(0, MkFifo(path, 0x180));

        Assert.Null(Service().ReadCouncilNote("alpha", "note.md"));
        Assert.Null(Service().AcceptCouncilNote("alpha", "note.md"));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    [Theory]
    [InlineData("A normal note\n")]
    [InlineData("---\ntitle: Normal note\n---\nBody\n")]
    [InlineData("---\naccepted_at: old\n---\nBody\n")]
    public void Regular_note_gets_exactly_one_stamp_when_accepted_repeatedly(string original)
    {
        var note = Write(Repository, "design/council/note.md", original);
        Write(Repository, "design/references/reference.md", "---\ntitle: Reference\nkind: accepted\n---\nBody\n");
        var service = Service();

        Assert.Equal(original, service.ReadCouncilNote("alpha", "note.md"));
        Assert.Contains("Reference", service.ReadReference("alpha", "reference.md"));
        Assert.NotNull(service.AcceptCouncilNote("alpha", "note.md"));
        var response = Assert.IsType<AcceptCouncilNoteResponse>(service.AcceptCouncilNote("alpha", "note.md"));
        var stamped = File.ReadAllText(note);
        Assert.Single(stamped.Split('\n'), line => line.StartsWith("acceptedAt:", StringComparison.Ordinal));
        Assert.DoesNotContain("accepted_at:", stamped);
        Assert.Contains(response.AcceptedAt, stamped);
        Assert.Contains(original.Contains("Body", StringComparison.Ordinal) ? "Body\n" : original, stamped);
        Assert.Single(service.ListCouncilNotes("alpha").Notes);
        Assert.Single(service.ListReferences("alpha").References);
        Assert.Equal(1, service.GetOverview("alpha").CouncilAcceptedCount);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(note)!, "*.tmp"));
    }

    [Theory]
    [InlineData("../note.md")]
    [InlineData("nested/note.md")]
    [InlineData("nested\\note.md")]
    [InlineData("note.json")]
    [InlineData("missing.md")]
    public void Invalid_or_missing_names_are_refused(string name)
    {
        var service = Service();
        Assert.Null(service.ReadCouncilNote("alpha", name));
        Assert.Null(service.ReadReference("alpha", name));
        Assert.Null(service.AcceptCouncilNote("alpha", name));
    }

    private void LinkFile(string relative, string target)
    {
        var path = Path.Combine(Repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, target);
    }

    private static string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

public sealed class DesignEvidenceAdmissionTests : IClassFixture<NetworkedSecurityEndpointTests.PrivateEdgeHost>
{
    private readonly NetworkedSecurityEndpointTests.PrivateEdgeHost _host;

    public DesignEvidenceAdmissionTests(NetworkedSecurityEndpointTests.PrivateEdgeHost host)
    {
        _host = host;
        host.Reset();
    }

    [Fact]
    public async Task Http_routes_preserve_read_write_and_operator_admission_while_refusing_linked_evidence()
    {
        var factory = _host.Ready("198.51.100.7", null);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("design.owner", "disposable owner passphrase!", null));
        var invitation = store.InviteUser(new InviteUserRequest("design.viewer", "Viewer", StudioRoles.Viewer, ["*"]), owner.User.Id);
        var viewer = store.AcceptInvitation(new AcceptInvitationRequest(invitation.Token, "disposable viewer passphrase!"));
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("design observer", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.Workspace)]), owner.User.Id);
        var session = store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest("design fixture", 1), owner.User.Id);
        var design = Path.Combine(_host.ProjectPath, "design");
        Directory.CreateDirectory(Path.Combine(design, "council"));
        Directory.CreateDirectory(Path.Combine(design, "references"));
        var note = Path.Combine(design, "council/note.md");
        var reference = Path.Combine(design, "references/reference.md");
        File.WriteAllText(note, "Inside note");
        File.WriteAllText(reference, "Inside reference");
        const string noteRoute = "/api/projects/networked-smoke/design/council/note.md";
        const string referenceRoute = "/api/projects/networked-smoke/design/references/reference.md";

        async Task<HttpResponseMessage> Send(HttpMethod method, string route, string? token = null, string? csrf = null, bool bearer = false)
        {
            using var request = new HttpRequestMessage(method, route);
            if (bearer) request.Headers.Add("Authorization", "Bearer " + token);
            else if (token is not null)
            {
                request.Headers.Add("Cookie", AccessSecurityStore.SessionCookieName + "=" + token);
                request.Headers.Add("Origin", "https://studio.test");
                request.Headers.Add("X-CSRF-Token", csrf);
            }
            return await client.SendAsync(request);
        }

        using (var anonymous = await Send(HttpMethod.Get, noteRoute)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var route in new[] { noteRoute, referenceRoute })
        {
            using var read = await Send(HttpMethod.Get, route, viewer.SessionToken, viewer.CsrfToken);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
        using (var refused = await Send(HttpMethod.Post, noteRoute + "/accept", viewer.SessionToken, viewer.CsrfToken))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Inside note", File.ReadAllText(note));
        using (var observed = await Send(HttpMethod.Get, noteRoute, session.Secret, bearer: true))
            Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
        using (var refused = await Send(HttpMethod.Post, noteRoute + "/accept", session.Secret, bearer: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(OperatorPolicyReasons.NotCatalogued, body.GetProperty("error").GetString());
        }
        using (var accepted = await Send(HttpMethod.Post, noteRoute + "/accept", owner.SessionToken, owner.CsrfToken))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Single(File.ReadAllLines(note), line => line.StartsWith("acceptedAt:", StringComparison.Ordinal));
        Assert.Equal(IamPermissions.ProjectRead, RoutePermissionCatalog.Resolve("GET", "/api/projects/{}/design/council/{}")?.Permission);
        Assert.Equal(IamPermissions.ProjectRead, RoutePermissionCatalog.Resolve("GET", "/api/projects/{}/design/references/{}")?.Permission);
        Assert.Equal(IamPermissions.ProjectRunsOperate, RoutePermissionCatalog.Resolve("POST", "/api/projects/{}/design/council/{}/accept")?.Permission);
        Assert.Null(OperatorOperationCatalog.ResolveWrite("POST", "/api/projects/{}/design/council/{}/accept"));

        var outside = Path.Combine(_host.Root, "outside-fixture.json");
        var before = System.Text.Encoding.UTF8.GetBytes("Disposable outside fixture " + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(outside, before);
        File.Delete(note);
        File.Delete(reference);
        File.CreateSymbolicLink(note, outside);
        File.CreateSymbolicLink(reference, outside);
        foreach (var route in new[] { noteRoute, referenceRoute })
        {
            using var rejected = await Send(HttpMethod.Get, route, viewer.SessionToken, viewer.CsrfToken);
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        }
        using (var rejected = await Send(HttpMethod.Post, noteRoute + "/accept", owner.SessionToken, owner.CsrfToken))
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(outside));
    }
}
