using System.Diagnostics;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Isolated on-disk layout that lets the Update Service integration suite
/// drive the orchestrator's real bash + git pipeline without ever touching
/// the real <c>agent-taskboard-stable</c> checkout (a hard rule of the
/// ADR-0031 follow-up task).
///
/// Layout under a temp root:
///
///   <root>/remote.git/                   bare git remote
///   <root>/stable/                       working clone with one prepared commit + VERSION
///   <root>/devspace/                     where stop-stable.sh + start-stable.sh live
///   <root>/runs/                         RunsDirectory
///   <root>/stable-updates.jsonl          HistoryFile
///
/// The fake scripts just touch a marker file inside <c>devspace/</c> so the
/// orchestrator's stop+start sequence is observable but doesn't fork a real
/// backend. The real fake backend is the parallel <see cref="FakeBackendHarness"/>
/// Kestrel host the orchestrator talks to via <see cref="UpdateServiceOptions.BackendUrl"/>.
/// </summary>
public sealed class FakeStableCheckout : IDisposable
{
    public string Root { get; }
    public string RemoteDir { get; }
    public string StableDir { get; }
    public string DevspaceDir { get; }
    public string RunsDir { get; }
    public string HistoryFile { get; }
    public string VersionFile { get; }
    public string StopMarkerPath { get; }
    public string StartMarkerPath { get; }
    public string BashPath { get; }
    public string GitPath { get; }

    /// <summary>Checkout-root manifest, i.e. the installed release identity.</summary>
    public string InstalledManifestPath { get; }

    /// <summary>
    /// AGT-2847 restart drill: what the "restarted backend" reports. The fake
    /// start script writes it exactly the way <c>BuildIdentity.Load</c>
    /// resolves its manifest, so the suite observes the identity handoff
    /// rather than asserting on it indirectly.
    /// </summary>
    public string RuntimeIdentityPath { get; }

    /// <summary>
    /// Value of <c>ATP_BUILD_MANIFEST</c> as the start script saw it, or an
    /// empty file when the variable was not passed through.
    /// </summary>
    public string StartEnvPath { get; }

    /// <summary>Workspace-side candidate manifest (UpdateServiceOptions.CandidateManifestFile).</summary>
    public string CandidateManifestPath { get; }

    /// <summary>Workspace-side approved tag file (UpdateServiceOptions.ApprovedTagFile).</summary>
    public string ApprovedTagPath { get; }

    private FakeStableCheckout(string root, string bashPath, string gitPath)
    {
        Root = root;
        RemoteDir = Path.Combine(root, "remote.git");
        StableDir = Path.Combine(root, "stable");
        DevspaceDir = Path.Combine(root, "devspace");
        RunsDir = Path.Combine(root, "runs");
        HistoryFile = Path.Combine(root, "stable-updates.jsonl");
        VersionFile = Path.Combine(StableDir, "VERSION");
        StopMarkerPath = Path.Combine(DevspaceDir, ".stop-stable.marker");
        StartMarkerPath = Path.Combine(DevspaceDir, ".start-stable.marker");
        InstalledManifestPath = Path.Combine(StableDir, "build-manifest.json");
        RuntimeIdentityPath = Path.Combine(root, "runtime-identity.json");
        StartEnvPath = Path.Combine(root, "start-build-manifest-env.txt");
        CandidateManifestPath = Path.Combine(root, "metadata", "stable-candidate-manifest.json");
        ApprovedTagPath = Path.Combine(root, "metadata", "stable-approved-tag");
        BashPath = bashPath;
        GitPath = gitPath;
    }

    /// <summary>
    /// Build the layout. Returns null when bash or git are not available on
    /// this host so the caller can mark the test skipped.
    /// </summary>
    public static FakeStableCheckout? TryCreate()
    {
        var bashPath = PosixShell.Path;
        var gitPath = Executables.FindOnPath("git");
        if (bashPath == null || gitPath == null) return null;

        var root = Path.Combine(Path.GetTempPath(), "atp-update-svc-it-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        var checkout = new FakeStableCheckout(root, bashPath, gitPath);

        Directory.CreateDirectory(checkout.RemoteDir);
        Directory.CreateDirectory(checkout.DevspaceDir);
        Directory.CreateDirectory(checkout.RunsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(checkout.CandidateManifestPath)!);

        // bare remote
        Run(gitPath, checkout.RemoteDir, "init", "--bare", "--initial-branch=main");

        // working clone with one commit + VERSION file. We init locally
        // (instead of cloning) because an empty bare has no main ref yet.
        Directory.CreateDirectory(checkout.StableDir);
        Run(gitPath, checkout.StableDir, "init", "--initial-branch=main");
        Run(gitPath, checkout.StableDir, "config", "user.email", "test@example.com");
        Run(gitPath, checkout.StableDir, "config", "user.name", "Update Service Test");
        Run(gitPath, checkout.StableDir, "remote", "add", "origin", checkout.RemoteDir);
        File.WriteAllText(checkout.VersionFile, "0.0.1-test\n");
        var frontendDir = Path.Combine(checkout.StableDir, "frontend");
        Directory.CreateDirectory(frontendDir);
        File.WriteAllText(Path.Combine(frontendDir, "package.json"),
            "{\n  \"name\": \"fake-update-service-frontend\",\n  \"version\": \"0.0.0\",\n  \"private\": true\n}\n");
        File.WriteAllText(Path.Combine(frontendDir, "package-lock.json"),
            "{\n  \"name\": \"fake-update-service-frontend\",\n  \"version\": \"0.0.0\",\n  \"lockfileVersion\": 3,\n  \"requires\": true,\n  \"packages\": { \"\": { \"name\": \"fake-update-service-frontend\", \"version\": \"0.0.0\" } }\n}\n");
        Run(gitPath, checkout.StableDir, "add", "VERSION");
        Run(gitPath, checkout.StableDir, "add", "frontend");
        Run(gitPath, checkout.StableDir, "commit", "-m", "test: initial");
        Run(gitPath, checkout.StableDir, "push", "-u", "origin", "main");

        // fake stop script: just touch a marker, exit 0.
        WriteScript(Path.Combine(checkout.DevspaceDir, "stop-stable.sh"),
            "#!/bin/bash\ntouch \"$(dirname \"$0\")/.stop-stable.marker\"\nexit 0\n");

        // fake start script. Beyond the marker it resolves the runtime
        // identity the launched backend would report, mirroring
        // BuildIdentity.Load: ATP_BUILD_MANIFEST wins, and the manifest the
        // build copies out of the checkout root is the fallback. That makes
        // the Update Service's identity handoff observable end-to-end instead
        // of only asserting that some environment variable was set.
        WriteScript(Path.Combine(checkout.DevspaceDir, "start-stable.sh"),
            $$"""
              #!/bin/bash
              touch "$(dirname "$0")/.start-stable.marker"
              printf '%s' "${ATP_BUILD_MANIFEST:-}" > "{{checkout.StartEnvPath}}"
              if [ -n "${ATP_BUILD_MANIFEST:-}" ] && [ -f "${ATP_BUILD_MANIFEST}" ]; then
                cp "${ATP_BUILD_MANIFEST}" "{{checkout.RuntimeIdentityPath}}"
              elif [ -f "{{checkout.InstalledManifestPath}}" ]; then
                cp "{{checkout.InstalledManifestPath}}" "{{checkout.RuntimeIdentityPath}}"
              fi
              exit 0
              """ + "\n");

        return checkout;
    }

    private static void WriteScript(string path, string body)
    {
        // Normalize to LF so Git Bash on Windows doesn't reject a CRLF shebang.
        File.WriteAllText(path, body.Replace("\r\n", "\n"));
        // The start wrapper is invoked as `./start-stable.sh`, which needs the
        // execute bit on POSIX hosts (Windows ignores it).
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public bool StopRan() => File.Exists(StopMarkerPath);
    public bool StartRan() => File.Exists(StartMarkerPath);

    /// <summary>
    /// The <c>ATP_BUILD_MANIFEST</c> the last start saw, or null when the
    /// start ran without the identity handoff.
    /// </summary>
    public string? StartBuildManifestEnv()
    {
        if (!File.Exists(StartEnvPath)) return null;
        var value = File.ReadAllText(StartEnvPath).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Raw JSON the fake backend would report after the last start.</summary>
    public string? ReadRuntimeIdentityJson() =>
        File.Exists(RuntimeIdentityPath) ? File.ReadAllText(RuntimeIdentityPath) : null;

    /// <summary>
    /// Prepares the immutable-release inputs for a restart drill: commits a
    /// <c>.agent-studio/project.yml</c> carrying the release identity rules
    /// and restore commands, tags that commit, and pushes the tag to the bare
    /// remote so the orchestrator's candidate preflight can fetch it. The
    /// working checkout is left on the PREVIOUS commit, which is what an
    /// upgrade starts from: the run has to move it to the candidate, and a
    /// failure before the mutation boundary has to move it back.
    /// Returns the tagged commit SHA.
    /// </summary>
    public string TagRelease(string tag, IReadOnlyList<string> restoreCommands)
    {
        var definitionDir = Path.Combine(StableDir, ".agent-studio");
        Directory.CreateDirectory(definitionDir);
        var restore = string.Join("\n", restoreCommands.Select(command => $"    - {command}"));
        File.WriteAllText(Path.Combine(definitionDir, "project.yml"),
            $"""
             schemaVersion: 1
             stack: [dotnet, node]
             toolVersions:
             commands:
               prepare: .agent-studio/prepare
               build:
               test:
               lint:
             testSuites:
             cachePaths: [frontend/node_modules]
             capabilities: [linux]
             environment:
               CI: "true"
             release:
               identity:
                 - package: CodingAgentRunner
                   ecosystem: nuget
                   version: 0.5.0
                   integrity: sha512-package
                 - package: coding-agent-chat
                   ecosystem: npm
                   version: 0.1.0
                   integrity: sha512-package
               restore:
             {restore}
             """.Replace("\r\n", "\n") + "\n");

        var previous = RunCapture(GitPath, StableDir, "rev-parse", "HEAD");
        Run(GitPath, StableDir, "add", ".agent-studio");
        Run(GitPath, StableDir, "commit", "-m", $"release: {tag}");
        Run(GitPath, StableDir, "tag", tag);
        Run(GitPath, StableDir, "push", "origin", "main");
        Run(GitPath, StableDir, "push", "origin", tag);
        var released = RunCapture(GitPath, StableDir, "rev-parse", "HEAD");
        Run(GitPath, StableDir, "checkout", "--detach", "--force", previous);
        return released;
    }

    /// <summary>Current HEAD of the working stable checkout.</summary>
    public string ReadStableHead() => RunCapture(GitPath, StableDir, "rev-parse", "HEAD");

    /// <summary>HEAD of the bare remote's main branch, i.e. the run's fetch target.</summary>
    public string ReadRemoteMainHead() => RunCapture(GitPath, RemoteDir, "rev-parse", "refs/heads/main");

    public void AdvanceOriginMain(string message = "test: remote update")
    {
        var cloneDir = Path.Combine(Root, "remote-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Run(GitPath, Root, "clone", "--branch", "main", RemoteDir, cloneDir);
        Run(GitPath, cloneDir, "config", "user.email", "test@example.com");
        Run(GitPath, cloneDir, "config", "user.name", "Update Service Test");
        File.WriteAllText(Path.Combine(cloneDir, $"remote-{Guid.NewGuid():N}.txt"), DateTime.UtcNow.ToString("O"));
        Run(GitPath, cloneDir, "add", ".");
        Run(GitPath, cloneDir, "commit", "-m", message);
        Run(GitPath, cloneDir, "push", "origin", "main");
    }

    public void AdvanceLocalMain(string message = "test: local divergent update")
    {
        File.WriteAllText(Path.Combine(StableDir, $"local-{Guid.NewGuid():N}.txt"), DateTime.UtcNow.ToString("O"));
        Run(GitPath, StableDir, "add", ".");
        Run(GitPath, StableDir, "commit", "-m", message);
    }

    private static void Run(string exe, string workingDir, params string[] args)
    {
        RunCapture(exe, workingDir, args);
    }

    private static string RunCapture(string exe, string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} {string.Join(' ', args)} exited {p.ExitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
