using AgentStudio.Readiness;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The readiness Git facts (AGT-3412): Git runs at most once per repository
/// per minute however often the report is read, and the outputs of
/// <c>git config --show-origin</c> and <c>git for-each-ref</c> are parsed into
/// the facts the policy decides from.
/// </summary>
public sealed class ReadinessGitFactsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "readiness-git-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public ReadinessGitFactsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CountingGit : IReadinessGit
    {
        public List<(string Directory, string Command)> Calls { get; } = [];
        public Func<IReadOnlyList<string>, ReadinessGitResult> Answer { get; set; } = arguments => arguments[0] switch
        {
            "config" => new ReadinessGitResult(0, "file:/etc/gitconfig\tuser.name Agent Studio\nfile:/etc/gitconfig\tuser.email studio@agent-studio.invalid\n", ""),
            "commit-tree" => new ReadinessGitResult(0, "6185b47919fe87ca3fa640c2a822ad08a3989073\n", ""),
            "for-each-ref" => new ReadinessGitResult(0,
                "refs/heads/main\t\nrefs/remotes/origin/HEAD\trefs/remotes/origin/main\nrefs/remotes/origin/main\t\n", ""),
            _ => new ReadinessGitResult(0, "", ""),
        };

        public ReadinessGitResult Run(string workingDirectory, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            Calls.Add((workingDirectory, arguments[0]));
            return Answer(arguments);
        }
    }

    private string Checkout(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    [Fact]
    public void Repository_refs_spawn_git_once_per_repository_per_minute()
    {
        var git = new CountingGit();
        var facts = new ReadinessGitFacts(git, _clock);
        var alpha = Checkout("alpha");
        var bravo = Checkout("bravo");

        for (var read = 0; read < 5; read++)
        {
            facts.Refs(alpha);
            facts.Refs(bravo + Path.DirectorySeparatorChar + ".");
            _clock.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(2, git.Calls.Count(call => call.Command == "for-each-ref"));

        _clock.Advance(TimeSpan.FromSeconds(10));
        facts.Refs(alpha);
        Assert.Equal(2, git.Calls.Count(call => call.Directory == Path.GetFullPath(alpha)));
        Assert.Equal(1, git.Calls.Count(call => call.Directory == Path.GetFullPath(bravo)));
    }

    [Fact]
    public void The_service_user_probe_runs_once_per_minute()
    {
        var git = new CountingGit();
        var facts = new ReadinessGitFacts(git, _clock);

        var first = facts.ServiceUser();
        _clock.Advance(TimeSpan.FromSeconds(59));
        var second = facts.ServiceUser();

        Assert.Same(first.Identity, second.Identity);
        Assert.Equal(["init", "config", "commit-tree"], git.Calls.Select(call => call.Command));
        Assert.Equal("Agent Studio", first.Identity.Name);
        Assert.Equal("file:/etc/gitconfig", first.Identity.EmailOrigin);
        Assert.Equal(new CommitProbeFact(true, true, "6185b47919fe87ca3fa640c2a822ad08a3989073"), first.Commit);
        Assert.False(Directory.Exists(git.Calls[0].Directory), "the scratch repository is removed");

        _clock.Advance(TimeSpan.FromSeconds(1));
        facts.ServiceUser();
        Assert.Equal(6, git.Calls.Count);
    }

    [Fact]
    public void A_missing_or_foreign_checkout_starts_no_git()
    {
        var git = new CountingGit();
        var facts = new ReadinessGitFacts(git, _clock);
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.Same(RepositoryRefsFact.Missing, facts.Refs(Path.Combine(_root, "absent")));
        Assert.Same(RepositoryRefsFact.NotGit, facts.Refs(plain));
        Assert.Empty(git.Calls);
    }

    [Fact]
    public void A_refused_commit_tree_keeps_the_git_message()
    {
        var git = new CountingGit();
        var answer = git.Answer;
        git.Answer = arguments => arguments[0] switch
        {
            "config" => new ReadinessGitResult(1, "", ""),
            "commit-tree" => new ReadinessGitResult(128, "", "Committer identity unknown\n\nfatal: unable to auto-detect email address\n"),
            _ => answer(arguments),
        };

        var (identity, commit) = new ReadinessGitFacts(git, _clock).ServiceUser();

        Assert.Equal(new GitIdentityFact(null, null, null, null), identity);
        Assert.True(commit.Ran);
        Assert.False(commit.Succeeded);
        Assert.StartsWith("Committer identity unknown", commit.Output);
    }

    [Fact]
    public void A_failing_git_init_leaves_both_facts_unknown()
    {
        var git = new CountingGit { Answer = _ => new ReadinessGitResult(-1, "", "git: not found", Ran: false) };

        var (identity, commit) = new ReadinessGitFacts(git, _clock).ServiceUser();

        Assert.Equal("git: not found", identity.Error);
        Assert.False(commit.Ran);
        Assert.Single(git.Calls);
    }

    [Fact]
    public void Show_origin_output_parses_to_name_email_and_their_files()
    {
        var identity = ReadinessGitFacts.ParseIdentity(
            "file:/etc/gitconfig\tuser.name Agent Studio\n"
            + "file:/etc/gitconfig\tuser.email studio@agent-studio.invalid\n"
            + "file:/data/home/.gitconfig\tuser.name Ops Team\r\n");

        Assert.Equal(new GitIdentityFact("Ops Team", "file:/data/home/.gitconfig",
            "studio@agent-studio.invalid", "file:/etc/gitconfig"), identity);
    }

    [Fact]
    public void For_each_ref_output_parses_refs_and_origin_head()
    {
        var refs = ReadinessGitFacts.ParseRefs(
            "refs/heads/main\t\n"
            + "refs/remotes/origin/HEAD\trefs/remotes/origin/develop\n"
            + "refs/remotes/origin/develop\t\n"
            + "refs/agent-studio/integration/develop\t\n");

        Assert.Equal("develop", refs.OriginHead);
        Assert.True(refs.HasOrigin);
        Assert.Equal(["refs/agent-studio/integration/develop", "refs/heads/main", "refs/remotes/origin/develop"],
            refs.Refs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_real_checkout_reports_its_lane_and_origin_head()
    {
        var git = new ProcessReadinessGit();
        var origin = Path.Combine(_root, "origin.git");
        var checkout = Path.Combine(_root, "checkout");
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        Git(_root, "clone", "-q", origin, checkout);
        Git(checkout, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "seed");
        Git(checkout, "push", "-q", "origin", "HEAD:main");
        Git(checkout, "remote", "set-head", "origin", "main");
        Git(checkout, "update-ref", "refs/agent-studio/integration/main", "origin/main");

        var refs = new ReadinessGitFacts(git, _clock).Refs(checkout);

        Assert.Null(refs.Error);
        Assert.Equal("main", refs.OriginHead);
        Assert.Contains("refs/remotes/origin/main", refs.Refs);
        Assert.Contains("refs/agent-studio/integration/main", refs.Refs);
    }

    internal static void Git(string directory, params string[] arguments)
    {
        var result = new ProcessReadinessGit().Run(directory, arguments);
        Assert.True(result.Success, $"git {string.Join(' ', arguments)}: {result.Error}");
    }
}
