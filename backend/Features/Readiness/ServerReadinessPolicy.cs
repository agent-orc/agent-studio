using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Readiness;

/// <summary>
/// Pure decision of every readiness line (AGT-3412). The move to the server on
/// 1 November 2026 hit one gap after another, and each showed up only as a
/// failing card: no Git identity for the service user, unknown repository
/// visibility, state written into the container layer, missing integration
/// lanes in fresh clones, an unset integration branch, stale locks. Each check
/// here names the gap, what it breaks, and the exact step that closes it.
/// <para>AGT-3580: the remedy is prose. A fix Studio can make itself carries a
/// typed <see cref="RemedyAction"/> (the owning project setting or surface); a
/// fix that needs a shell carries its command separately, which the UI shows
/// only as a copyable detail.</para>
/// </summary>
public static class ServerReadinessPolicy
{
    /// <summary>The identity the API image writes to <c>/etc/gitconfig</c> (backend/Dockerfile).</summary>
    public const string ImageGitUserName = "Agent Studio";
    public const string ImageGitUserEmail = "studio@agent-studio.invalid";
    public const string ImageGitConfigFile = "/etc/gitconfig";

    /// <summary>A host that has not claimed work for this long is reported as not seen.</summary>
    public static readonly TimeSpan HostSeenWindow = TimeSpan.FromMinutes(10);

    /// <summary>Same threshold as <c>GitStaleLockPolicy.DefaultThreshold</c>: younger locks may belong to a running git.</summary>
    public static readonly TimeSpan StaleLockAge = TimeSpan.FromMinutes(10);

    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    public static ServerReadinessReport Evaluate(ServerReadinessFacts facts)
    {
        var checks = new List<ReadinessCheck>
        {
            GitIdentity(facts.Identity),
            CommitterIdentity(facts.CommitProbe),
        };
        checks.AddRange(facts.DataFolders.Select(folder => DataFolder(folder, facts.RunsInContainer)));
        checks.AddRange(facts.StateFolders.Select(StateFolder));
        checks.Add(GitLocks(ReadinessScopes.Server, "workspace", facts.WorkspaceLocks));
        checks.Add(GateWorker(facts.Gate, facts.Now));
        checks.AddRange(ExecutionHosts(facts.Hosts, facts.Now));
        foreach (var project in facts.Projects.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (project.Refs.CheckoutExists)
                checks.Add(DataFolder(project.Checkout, facts.RunsInContainer, ReadinessScopes.Project, project.Name));
            checks.Add(RepositoryVisibility(project));
            checks.Add(AppCoverage(project.Name, facts.App));
            var branch = IntegrationBranch(project);
            checks.Add(branch.Check);
            if (branch.Branch is { } resolved) checks.Add(IntegrationLane(project, resolved));
            checks.Add(GitLocks(ReadinessScopes.Project, project.Name, project.Locks));
        }
        return ServerReadinessReport.From(facts.Now, checks);
    }

    private static ReadinessCheck Line(string id, string title, string scope, string? subject,
        string state, string meaning, string? remedy = null, string? command = null, RemedyAction? action = null,
        ReadinessConfigEntry? configEntry = null)
        => state == ReadinessStates.Pass
            ? new(id, title, scope, subject, state, meaning, null)
            : new(id, title, scope, subject, state, meaning, remedy, command, action, configEntry);

    private const string IdentityRemedy = "Set the Git identity of the service user in the API container, from the stack directory.";

    private static string IdentityCommand()
        => $"docker compose exec api git config --global user.name \"{ImageGitUserName}\" "
           + $"&& docker compose exec api git config --global user.email \"{ImageGitUserEmail}\"";

    public static ReadinessCheck GitIdentity(GitIdentityFact fact)
    {
        const string id = "git-identity";
        const string title = "Git identity of the service user";
        if (fact.Error is { } error)
            return Line(id, title, ReadinessScopes.Server, null, ReadinessStates.Unknown,
                $"Git did not answer when asked for user.name and user.email: {error}",
                "Check that Git runs in the API container.",
                "docker compose exec api git --version");
        var missing = new[]
        {
            string.IsNullOrWhiteSpace(fact.Name) ? "user.name" : null,
            string.IsNullOrWhiteSpace(fact.Email) ? "user.email" : null,
        }.OfType<string>().ToArray();
        if (missing.Length > 0)
            return Line(id, title, ReadinessScopes.Server, null, ReadinessStates.Fail,
                $"Git has no {string.Join(" and no ", missing)} for the service user, so every commit that takes "
                + "its identity from the Git configuration (direct merges, bound-index commits) fails with "
                + "\"Committer identity unknown\".",
                IdentityRemedy, IdentityCommand());
        var origin = string.Equals(fact.NameOrigin, fact.EmailOrigin, StringComparison.Ordinal)
            ? DescribeOrigin(fact.NameOrigin)
            : $"{DescribeOrigin(fact.NameOrigin)} and {DescribeOrigin(fact.EmailOrigin)}";
        return Line(id, title, ReadinessScopes.Server, null, ReadinessStates.Pass,
            $"Commits are made as {fact.Name} <{fact.Email}>, from {origin}.");
    }

    private static string DescribeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return "an unknown source";
        var file = origin.StartsWith("file:", StringComparison.Ordinal) ? origin["file:".Length..] : origin;
        return file == ImageGitConfigFile ? $"{ImageGitConfigFile} (the image default)" : file;
    }

    public static ReadinessCheck CommitterIdentity(CommitProbeFact fact)
    {
        const string id = "committer-identity";
        const string title = "Committer identity (git commit-tree probe)";
        const string manual = "Run the probe by hand in the API container and fix what it prints.";
        var probe = "docker compose exec api sh -c "
                    + $"'cd \"$(mktemp -d)\" && git init -q && git commit-tree {EmptyTree} -m probe'";
        if (!fact.Ran)
            return Line(id, title, ReadinessScopes.Server, null, ReadinessStates.Unknown,
                $"The probe could not run: {fact.Output ?? "no scratch repository"}.", manual, probe);
        if (fact.Succeeded)
            return Line(id, title, ReadinessScopes.Server, null, ReadinessStates.Pass,
                "A probe commit in a scratch repository succeeded, so merges and artifact commits can record a committer.");
        var output = FirstLine(fact.Output) ?? "no output";
        var identity = fact.Output is { } text
            && (text.Contains("identity unknown", StringComparison.OrdinalIgnoreCase)
                || text.Contains("auto-detect", StringComparison.OrdinalIgnoreCase)
                || text.Contains("empty ident", StringComparison.OrdinalIgnoreCase));
        return Line(id, title, ReadinessScopes.Server, null, ReadinessStates.Fail,
            $"git commit-tree failed in a scratch repository ({output}). Direct merges and artifact commits fail the same way.",
            identity ? IdentityRemedy : manual,
            identity ? IdentityCommand() : probe);
    }

    private static string? FirstLine(string? text)
        => text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('*'));

    public static ReadinessCheck DataFolder(DataFolderFact fact, bool runsInContainer,
        string scope = ReadinessScopes.Server, string? subject = null)
    {
        const string id = "data-volume";
        const string title = "Data folder on the data volume";
        subject ??= fact.Name;
        const string mount = "Mount it from the data directory: add the volume line below under services.api.volumes "
                             + "in compose.override.yaml";
        // The volume line is YAML for the compose file, not a shell step: it
        // travels as a config entry; the command holds only what is run.
        var volume = new ReadinessConfigEntry("compose.override.yaml", "services.api.volumes",
            $"- ${{STUDIO_DATA_DIR}}/{fact.HostFolder}:{fact.Path}");
        const string recreate = "docker compose up -d";
        if (!fact.Exists)
            return Line(id, title, scope, subject, ReadinessStates.Fail,
                $"{fact.Path} does not exist, so Studio cannot keep its {fact.Name} data.",
                mount + ", create that folder owned by UID 10001, and recreate the stack.",
                $"sudo install -d -o 10001 -g 10001 \"$STUDIO_DATA_DIR/{fact.HostFolder}\"\n{recreate}",
                configEntry: volume);
        if (!runsInContainer)
            return Line(id, title, scope, subject, ReadinessStates.Pass,
                $"{fact.Path} is on the host file system; Studio does not run in a container.");
        if (fact.MountTableError is { } error)
            return Line(id, title, scope, subject, ReadinessStates.Unknown,
                $"The mount table could not be read ({error}), so it is unknown whether {fact.Path} survives a container rebuild.",
                "Check the mounts of the API container by hand.",
                "docker compose exec api cat /proc/self/mountinfo");
        if (fact.MountPoint is null or "/")
            return Line(id, title, scope, subject, ReadinessStates.Fail,
                $"{fact.Path} is inside the container layer: whatever Studio writes there is lost when the container is recreated.",
                mount + " and recreate the stack.",
                recreate, configEntry: volume);
        return Line(id, title, scope, subject, ReadinessStates.Pass,
            $"{fact.Path} is on the mount {fact.MountPoint} ({fact.FileSystemType ?? "unknown type"}), outside the container layer.");
    }

    public static ReadinessCheck StateFolder(StateFolderFact fact)
    {
        const string id = "state-writable";
        const string title = "State folder writable";
        return fact.Writable
            ? Line(id, title, ReadinessScopes.Server, fact.Name, ReadinessStates.Pass,
                $"Studio created and removed a probe file in {fact.Path}.")
            : Line(id, title, ReadinessScopes.Server, fact.Name, ReadinessStates.Fail,
                $"Studio cannot write to {fact.Path} ({fact.Error ?? "unknown error"}), so cards, sessions and evidence there cannot be saved.",
                $"Give the host folder behind {fact.Path} to the service user (UID 10001), then restart the API container.",
                $"sudo chown -R 10001:10001 \"$STUDIO_DATA_DIR/{fact.Name}\" && docker compose restart api");
    }

    public static ReadinessCheck GitLocks(string scope, string subject, IReadOnlyList<GitLockFact> locks)
    {
        const string id = "git-locks";
        const string title = "No stale Git locks";
        var stale = locks.Where(item => item.Age >= StaleLockAge).OrderByDescending(item => item.Age).ToArray();
        if (stale.Length == 0)
            return Line(id, title, scope, subject, ReadinessStates.Pass,
                $"No Git lock file older than {StaleLockAge.TotalMinutes:0} minutes.");
        var files = string.Join(" ", stale.Select(item => Quote(item.Path)));
        return Line(id, title, scope, subject, ReadinessStates.Warn,
            $"{stale.Length} Git lock file(s) older than {StaleLockAge.TotalMinutes:0} minutes, the oldest {Age(stale[0].Age)}: "
            + $"{string.Join(", ", stale.Select(item => item.Path))}. Git writes in this repository fail while they exist.",
            "Make sure no git process runs in the repository, then remove the stale lock files.",
            $"ps -ef | grep git\nrm -f {files}");
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    public static ReadinessCheck GateWorker(GateWorkerFact fact, DateTimeOffset now)
    {
        const string id = "gate-worker";
        const string title = "Gate worker reachable";
        if (fact.PullWorkers.FirstOrDefault(worker => worker.Healthy) is { } healthy)
            return Line(id, title, ReadinessScopes.Server, healthy.Name, ReadinessStates.Pass,
                $"Pull gate worker {healthy.Name} sent a heartbeat {Age(now - healthy.LastHeartbeat)} ago ({healthy.Slots} slot(s)).");
        if (!string.IsNullOrWhiteSpace(fact.SshHost))
        {
            var probe = $"docker compose exec api ssh -o BatchMode=yes {fact.SshHost} true";
            return fact.SshReachability switch
            {
                "reachable" => Line(id, title, ReadinessScopes.Server, fact.SshHost, ReadinessStates.Pass,
                    $"SSH gate host {fact.SshHost} answered {Age(now - (fact.LastReachedAt ?? now))} ago."),
                "unreachable" => Line(id, title, ReadinessScopes.Server, fact.SshHost, ReadinessStates.Fail,
                    $"The last gate could not reach SSH gate host {fact.SshHost}"
                    + (fact.LastFailureAt is { } failed ? $" ({Age(now - failed)} ago)" : "")
                    + ", so build gates cannot run and integration waits.",
                    "Run the SSH probe in the stack directory and fix the key, host key or address it reports.",
                    probe),
                _ => Line(id, title, ReadinessScopes.Server, fact.SshHost, ReadinessStates.Unknown,
                    $"No gate reached SSH gate host {fact.SshHost} in the last 5 minutes, so reachability is not proven yet.",
                    "The next gate proves it; to check now, run the SSH probe in the stack directory.",
                    probe),
            };
        }
        if (fact.PullWorkers.Count > 0)
        {
            var last = fact.PullWorkers.MaxBy(worker => worker.LastHeartbeat)!;
            return Line(id, title, ReadinessScopes.Server, last.Name, ReadinessStates.Fail,
                $"No pull gate worker is alive: {last.Name} sent its last heartbeat {Age(now - last.LastHeartbeat)} ago. "
                + "Build gates wait until a worker is back.",
                $"On the gate host, restart the gate worker of {last.Name} and check in its log that it reaches this server's URL.");
        }
        return Line(id, title, ReadinessScopes.Server, null, ReadinessStates.Warn,
            "No gate worker is configured: no pull worker registered and RemoteGate:SshHost is not set, "
            + "so build gates run on this server or not at all.",
            "Start a pull gate worker against this server, or set RemoteGate__SshHost and RemoteGate__WorkerPath "
            + "for the api service and recreate the stack.");
    }

    public static IEnumerable<ReadinessCheck> ExecutionHosts(ExecutionHostsFact fact, DateTimeOffset now)
    {
        const string id = "execution-host";
        const string title = "Execution host enrolled and seen";
        if (fact.Hosts.Count == 0)
        {
            yield return Line(id, title, ReadinessScopes.Host, null, ReadinessStates.Warn,
                "No execution host is enrolled, so no card can run on a remote host"
                + (fact.PendingEnrollments > 0 ? $"; {fact.PendingEnrollments} enrolment code(s) are issued but not redeemed." : "."),
                "Create an enrolment under Execution Hosts and run the enrolment command it shows on the host.",
                action: RemedyAction.Route(RemedyRoutes.ExecutionHosts));
            yield break;
        }
        var hosts = RemedyAction.Route(RemedyRoutes.ExecutionHosts);
        foreach (var host in fact.Hosts.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var repoint = $"On {host.Name}, check that the runner service runs and its RUNNER_SERVER_URL points at this server, "
                          + "then restart it; re-enrol it if its credential was revoked.";
            if (host.LastSeenAt is not { } seen)
            {
                yield return Line(id, title, ReadinessScopes.Host, host.Name, ReadinessStates.Fail,
                    $"Enrolled {host.EnrolledAt:yyyy-MM-dd} but has never contacted this server.", repoint,
                    action: hosts);
                continue;
            }
            var age = now - new DateTimeOffset(DateTime.SpecifyKind(seen, DateTimeKind.Utc));
            yield return age <= HostSeenWindow
                ? Line(id, title, ReadinessScopes.Host, host.Name, ReadinessStates.Pass,
                    $"Enrolled and last seen {Age(age)} ago.")
                : Line(id, title, ReadinessScopes.Host, host.Name, ReadinessStates.Fail,
                    $"Enrolled, but not seen for {Age(age)}: it claims no cards.", repoint, action: hosts);
        }
    }

    public static ReadinessCheck RepositoryVisibility(ProjectReadinessFact project)
    {
        const string id = "repository-visibility";
        const string title = "Repository visibility known";
        return project.RepositoryVisibility switch
        {
            "private" => Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Pass,
                "Recorded as private: Remote Review clones it with a read credential scoped to this repository."),
            "public" => Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Pass,
                "Recorded as public: Remote Review clones it without credentials."),
            _ => Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Warn,
                "Not recorded. Remote Review then treats the repository as public and clones it without credentials, "
                + "which fails for a private repository.",
                "Record it as private or public in the project's repository visibility setting.",
                action: RemedyAction.Setting(ProjectSettingKeys.RepositoryVisibility, project.Name)),
        };
    }

    public static ReadinessCheck AppCoverage(string project, GitHubAppFact app)
    {
        const string id = "app-coverage";
        const string title = "GitHub App coverage";
        var github = RemedyAction.Route(RemedyRoutes.GitHubSettings);
        if (!app.Connected)
            return Line(id, title, ReadinessScopes.Project, project, ReadinessStates.Warn,
                "No GitHub App is connected, so hosts reach this repository only with credentials configured on them.",
                "Connect the App under Settings > GitHub, or keep a credential for this repository on every host.",
                action: github);
        if (!app.Coverage.TryGetValue(project, out var coverage))
            return Line(id, title, ReadinessScopes.Project, project, ReadinessStates.Unknown,
                $"The App status lists no coverage for this project{(app.Problem is { } problem ? $" ({problem})" : "")}.",
                "Open Settings > GitHub and refresh the App status, then Recheck.",
                action: github);
        var repository = coverage.RepositoryUrl ?? "the repository";
        return coverage.State switch
        {
            GitHubAppCoverageStates.Covered => Line(id, title, ReadinessScopes.Project, project, ReadinessStates.Pass,
                $"The App reaches {repository}" + (coverage.InstallationId is { } installation ? $" through installation {installation}." : ".")),
            GitHubAppCoverageStates.NotGitHub => Line(id, title, ReadinessScopes.Project, project, ReadinessStates.Pass,
                $"{repository} is not on github.com; the App does not apply."),
            GitHubAppCoverageStates.NotInstalled => Line(id, title, ReadinessScopes.Project, project, ReadinessStates.Fail,
                $"The App is not installed on {repository}, so hosts get no token for it and clones and pushes fail.",
                "Install the App on this repository" + (app.InstallUrl is { } url ? $": {url}" : " under Settings > GitHub") + ", then Recheck.",
                action: github),
            GitHubAppCoverageStates.NotAdopted => Line(id, title, ReadinessScopes.Project, project, ReadinessStates.Fail,
                $"An App installation reaches {repository}, but this Studio has not adopted it, so it issues no token for it.",
                "Adopt the installation under Settings > GitHub, then Recheck.",
                action: github),
            GitHubAppCoverageStates.NoRepositoryUrl => Line(id, title, ReadinessScopes.Project, project, ReadinessStates.Warn,
                "The project registers no repository URL, so neither the App nor Remote Review can find the repository.",
                "Add the repository URL as the project's \"Repository\" link, then Recheck."),
            _ => Line(id, title, ReadinessScopes.Project, project, ReadinessStates.Unknown,
                $"GitHub could not be asked about {repository}{(coverage.Detail is { } detail ? $": {detail}" : ".")}",
                "Recheck in a few minutes; if it stays unknown, check the App connection under Settings > GitHub.",
                action: github),
        };
    }

    /// <summary>The integration branch line, and the branch it resolved (null when it did not).</summary>
    public static (ReadinessCheck Check, string? Branch) IntegrationBranch(ProjectReadinessFact project)
    {
        const string id = "integration-branch";
        const string title = "Integration branch set";
        var path = project.RepositoryPath;
        var refs = project.Refs;
        var setting = RemedyAction.Setting(ProjectSettingKeys.IntegrationBranch, project.Name);
        ReadinessCheck Fail(string meaning, string remedy, string? command = null, RemedyAction? action = null)
            => Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Fail, meaning, remedy, command, action);
        if (!refs.CheckoutExists)
            return (Fail($"The checkout {path} does not exist, so no card of this project can run or integrate.",
                "Clone the repository there, then Recheck.",
                $"git clone <repository URL> {Quote(path)}"), null);
        if (!refs.IsGitCheckout)
            return (Fail($"{path} is not a Git checkout.",
                "Replace it with a clone of the repository, then Recheck.",
                $"git clone <repository URL> {Quote(path)}"), null);
        if (refs.Error is { } error)
            return (Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Unknown,
                $"Git could not list the refs of {path}: {error}",
                "List the refs by hand and fix the error Git prints.",
                $"git -C {Quote(path)} for-each-ref refs/remotes/origin"), null);
        var configured = string.IsNullOrWhiteSpace(project.ConfiguredIntegrationBranch)
            ? null : project.ConfiguredIntegrationBranch.Trim();
        var branch = configured ?? refs.OriginHead;
        if (branch is null)
            return (refs.HasOrigin
                ? Fail("No integration branch is set and origin/HEAD is unknown in this checkout, so integration has no target branch.",
                    "Set the integration branch in the project setting, or let Git record origin/HEAD in the checkout.",
                    $"git -C {Quote(path)} remote set-head origin --auto", setting)
                : Fail("No integration branch is set and the checkout has no origin remote to take it from.",
                    "Set the integration branch in the project setting.", action: setting), null);
        var source = configured is null ? "from origin/HEAD" : "set for the project";
        if (refs.HasOrigin)
        {
            return refs.Refs.Contains($"refs/remotes/origin/{branch}")
                ? (Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Pass,
                    $"Integrates into {branch} ({source}); origin/{branch} is present."), branch)
                : (Fail($"The integration branch {branch} ({source}) has no origin/{branch} in this checkout, so every integration fails.",
                    "Fetch the branch into the checkout; if the branch has another name, set it in the project setting.",
                    $"git -C {Quote(path)} fetch origin {branch}", setting), null);
        }
        return refs.Refs.Contains($"refs/heads/{branch}")
            ? (Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Pass,
                $"Integrates into the local branch {branch} ({source}); the checkout has no origin remote."), branch)
            : (Fail($"The integration branch {branch} ({source}) does not exist in this checkout.",
                "Create the branch in the checkout, or set the right one in the project setting.",
                $"git -C {Quote(path)} branch {branch}", setting), null);
    }

    public static ReadinessCheck IntegrationLane(ProjectReadinessFact project, string branch)
    {
        const string id = "integration-lane";
        const string title = "Integration lane present";
        var lane = $"refs/agent-studio/integration/{branch}";
        if (project.Refs.Refs.Contains(lane))
            return Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Pass, $"{lane} is present.");
        var source = project.Refs.HasOrigin ? $"origin/{branch}" : branch;
        return Line(id, title, ReadinessScopes.Project, project.Name, ReadinessStates.Warn,
            $"The integration lane {lane} is missing, as in a fresh clone. The first integration has to seed it, "
            + $"and fails the card when {source} is not available at that moment.",
            "Seed it from the published branch.",
            $"git -C {Quote(project.RepositoryPath)} update-ref {lane} {source}");
    }

    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromMinutes(1)) return "less than a minute";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromDays(2)) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} days";
    }
}
