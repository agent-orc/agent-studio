using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Magazine;
using AgentStudio.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// MAG-1 (AGT-3714): the magazine store keeps every revision immutable,
/// survives a crash between the revision and the head write, validates
/// articles by named errors, and stays out of workspace commits.
/// </summary>
public sealed class MagazineStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "magazine-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
    private static readonly MagazineScope Project = new(MagazineScopeKind.Project, "PROJ-002");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private MagazineStore Store() => new(Path.Combine(_root, MagazineStorePathPolicy.DirectoryName), _clock);

    private static MagazineUpsertRequest Article(string body, string summary = "29 cards wait for you.", string kind = "digest")
        => new("Weekly digest", summary, body, kind, null, [new MagazineSource("card", "AGT-3714")]);

    [Fact]
    public void Revisions_stay_readable_and_are_never_rewritten()
    {
        var store = Store();
        var (first, created) = store.Upsert(Project, "weekly", Article("one"), "human:owner", null);
        Assert.True(created);
        var firstFile = Path.Combine(store.Root, "project-PROJ-002", "weekly", MagazineStore.RevisionFolderName, "000001.json");
        var firstBytes = File.ReadAllBytes(firstFile);
        _clock.Advance(TimeSpan.FromHours(1));

        var (second, createdAgain) = store.Upsert(Project, "weekly", Article("two"), "service-account:bot", first.Revision);

        Assert.False(createdAgain);
        Assert.Equal(2, second.Revision);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.True(second.UpdatedAt > first.UpdatedAt);
        Assert.Equal(["human:owner", "service-account:bot"], second.Authors.Select(author => author.Principal));
        Assert.Equal(firstBytes, File.ReadAllBytes(firstFile));
        Assert.Equal("one", store.Get(Project, "weekly", 1)!.Body);
        Assert.Equal("two", store.Get(Project, "weekly")!.Body);
        Assert.Null(store.Get(Project, "weekly", 3));
        Assert.Equal([2, 1], store.History(Project, "weekly")!.Select(entry => entry.Revision));
    }

    [Fact]
    public void Persona_and_role_ids_are_saved_while_legacy_authors_stay_readable()
    {
        var store = Store();
        var first = store.Upsert(Project, "people", Article("one") with { Persona = "Aster" }, "agent:orchestrator", null).Article;
        var folder = Path.Combine(store.Root, "project-PROJ-002", "people");
        foreach (var file in new[] { Path.Combine(folder, MagazineStore.HeadFileName),
            Path.Combine(folder, MagazineStore.RevisionFolderName, "000001.json") })
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            if (json["authors"] is JsonArray authors)
                foreach (var author in authors) { author!.AsObject().Remove("personaId"); author.AsObject().Remove("roleId"); }
            if (json["author"] is JsonObject revisionAuthor)
            { revisionAuthor.Remove("personaId"); revisionAuthor.Remove("roleId"); }
            File.WriteAllText(file, json.ToJsonString());
        }
        Assert.Null(first.Author.PersonaId);
        Assert.Null(store.Get(Project, "people")!.Author.RoleId);

        var second = store.Upsert(Project, "people", Article("two") with
        {
            Persona = "Aster", PersonaId = "persona-aster", RoleId = "editor",
        }, "agent:orchestrator", first.Revision).Article;

        Assert.Equal(("Aster", "persona-aster", "editor"),
            (second.Author.Persona, second.Author.PersonaId, second.Author.RoleId));
        Assert.Equal(2, second.Authors.Count);
        Assert.Null(store.Get(Project, "people", 1)!.Author.PersonaId);
        Assert.Equal("editor", store.Get(Project, "people")!.Author.RoleId);
    }

    [Fact]
    public void A_crash_between_the_revision_and_the_head_leaves_the_head_valid_and_the_next_upsert_wins()
    {
        var store = Store();
        store.Upsert(Project, "weekly", Article("one"), "human:owner", null);
        // A revision file the head never referenced: the write before the head swap crashed.
        var orphan = Path.Combine(store.Root, "project-PROJ-002", "weekly", MagazineStore.RevisionFolderName, "000002.json");
        File.WriteAllText(orphan, "{ \"half\": ");

        Assert.Equal(1, store.Get(Project, "weekly")!.Revision);
        Assert.Equal([1], store.History(Project, "weekly")!.Select(entry => entry.Revision));

        var (next, _) = store.Upsert(Project, "weekly", Article("two"), "human:owner", 1);
        Assert.Equal(2, next.Revision);
        Assert.Equal("two", JsonDocument.Parse(File.ReadAllText(orphan)).RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public void Stale_and_missing_revisions_are_refused_without_writing()
    {
        var store = Store();
        store.Upsert(Project, "weekly", Article("one"), "human:owner", null);
        store.Upsert(Project, "weekly", Article("two"), "human:owner", 1);

        var stale = Assert.Throws<MagazineException>(() => store.Upsert(Project, "weekly", Article("stale"), "human:owner", 1));
        Assert.Equal(("revision-conflict", 409, 2), (stale.Code, stale.Status, stale.CurrentRevision));
        var missing = Assert.Throws<MagazineException>(() => store.Upsert(Project, "weekly", Article("blind"), "human:owner", null));
        Assert.Equal(("if-match-required", 428), (missing.Code, missing.Status));
        var absent = Assert.Throws<MagazineException>(() => store.Upsert(Project, "fresh", Article("x"), "human:owner", 4));
        Assert.Equal("revision-conflict", absent.Code);
        Assert.Equal(2, store.Get(Project, "weekly")!.Revision);
        Assert.Null(store.Get(Project, "fresh"));
    }

    [Theory]
    [InlineData("magazine-summary-too-long")]
    [InlineData("magazine-summary-paragraphs")]
    [InlineData("magazine-kind-invalid")]
    [InlineData("magazine-body-too-large")]
    [InlineData("magazine-credential-in-content")]
    [InlineData("magazine-source-invalid")]
    [InlineData("magazine-body-invalid")]
    public void Invalid_articles_are_refused_by_name(string code)
    {
        // Assembled at runtime: never a provider-shaped literal in source.
        var credential = string.Join("", "gh", "p_", new string('a', 36));
        var request = code switch
        {
            "magazine-summary-too-long" => Article("body", new string('s', MagazineLimits.MaxSummaryLength + 1)),
            "magazine-summary-paragraphs" => Article("body", "One.\r\n\r\nTwo."),
            "magazine-kind-invalid" => Article("body", kind: "essay"),
            "magazine-body-too-large" => Article(new string('é', MagazineLimits.MaxBodyBytes / 2 + 1)),
            "magazine-credential-in-content" => Article("Use " + credential + " to sign in."),
            "magazine-source-invalid" => Article("body") with { Sources = [new MagazineSource("commit", "abc")] },
            _ => Article("nul\0byte"),
        };

        var refused = Assert.Throws<MagazineException>(() => Store().Upsert(Project, "weekly", request, "human:owner", null));

        Assert.Equal(code, refused.Code);
        Assert.False(Directory.Exists(Path.Combine(_root, MagazineStorePathPolicy.DirectoryName, "project-PROJ-002", "weekly")));
    }

    [Fact]
    public void Prose_about_credentials_is_stored_unchanged()
    {
        const string body = "Authorization: pending the Owner.\npassword: reset requested for two users.";

        var (article, _) = Store().Upsert(Project, "security-notes", Article(body), "human:owner", null);

        Assert.Equal(body, article.Body);
    }

    [Theory]
    [InlineData("installation", MagazineScopeKind.Installation, null)]
    [InlineData("workspace:ws-default", MagazineScopeKind.Workspace, "ws-default")]
    [InlineData("project:PROJ-002", MagazineScopeKind.Project, "PROJ-002")]
    public void Scopes_parse_and_print_their_wire_form(string text, MagazineScopeKind kind, string? id)
    {
        Assert.True(MagazineScope.TryParse(text, out var scope));
        Assert.Equal((kind, id), (scope.Kind, scope.Id));
        Assert.Equal(text, scope.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("project:")]
    [InlineData("project:../etc")]
    [InlineData("project:a/b")]
    [InlineData("team:x")]
    public void Unsafe_or_unknown_scopes_are_refused(string text)
        => Assert.False(MagazineScope.TryParse(text, out _));

    [Theory]
    [InlineData(".magazine", true)]
    [InlineData(".magazine/project-PROJ-002/weekly/article.json", true)]
    [InlineData("./.magazine/installation/a/revisions/000001.json", true)]
    [InlineData("projects/demo/.magazine/x", false)]
    [InlineData(".magazines/x", false)]
    public void The_store_folder_is_recognized_only_at_the_workspace_root(string path, bool expected)
        => Assert.Equal(expected, MagazineStorePathPolicy.IsMagazineStorePath(path));

    [Fact]
    public void The_workspace_ignores_the_store_folder()
    {
        Directory.CreateDirectory(_root);

        Assert.True(CliOutputLogMaintenanceService.EnsureRotationIgnored(_root));

        Assert.Equal(1, File.ReadLines(Path.Combine(_root, ".gitignore")).Count(line => line == MagazineStorePathPolicy.IgnorePattern));
    }
}
