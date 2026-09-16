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
///   <root>/metadata/                     candidate manifest + approved tag cache
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
    /// <summary>
    /// File the fake start script writes <c>ATP_BUILD_MANIFEST</c> into, so a
    /// test can prove which build manifest the orchestrator handed to the
    /// backend it restarted (AGT-2847).
    /// </summary>
    public string StartManifestEnvPath { get; }
    public string MetadataDir { get; }
    /// <summary>Immutable candidate manifest cache the outer updater fills.</summary>
    public string CandidateManifestFile { get; }
    /// <summary>Cached latest-approved release tag.</summary>
    public string ApprovedTagFile { get; }
    /// <summary>The installed manifest in the checkout root.</summary>
    public string RootManifestFile { get; }
    public string BashPath { get; }
    public string GitPath { get; }

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
        StartManifestEnvPath = Path.Combine(DevspaceDir, ".start-stable.build-manifest-env");
        MetadataDir = Path.Combine(root, "metadata");
        CandidateManifestFile = Path.Combine(MetadataDir, "stable-candidate-manifest.json");
        ApprovedTagFile = Path.Combine(MetadataDir, "stable-approved-tag");
        RootManifestFile = Path.Combine(StableDir, "build-manifest.json");
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
        Directory.CreateDirectory(checkout.MetadataDir);

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

        // fake start/stop scripts: touch a marker and exit 0, so the stop+
        // start sequence is observable without forking a real backend.
        WriteScript(Path.Combine(checkout.DevspaceDir, "stop-stable.sh"),
            $"#!/bin/bash\ntouch \"$(dirname \"$0\")/.stop-stable.marker\"\nexit 0\n");
        // The start script records the build-manifest environment it was
        // handed. The real start-stable.sh / api.sh pair has to pass that
        // variable through to `dotnet run`; recording it here is how the
        // restart drill proves the orchestrator sets it (AGT-2847).
        WriteScript(Path.Combine(checkout.DevspaceDir, "start-stable.sh"),
            "#!/bin/bash\n"
            + "dir=\"$(dirname \"$0\")\"\n"
            + "touch \"$dir/.start-stable.marker\"\n"
            + "printf '%s' \"${ATP_BUILD_MANIFEST:-}\" > \"$dir/.start-stable.build-manifest-env\"\n"
            + "exit 0\n");

        return checkout;
    }

    private static void WriteScript(string path, string body)
    {
        // Normalize to LF so Git Bash on Windows doesn't reject a CRLF shebang.
        File.WriteAllText(path, body.Replace("\r\n", "\n"));
        // The orchestrator starts the stack as `./start-stable.sh`, which needs
        // the execute bit on a POSIX host (Git Bash on Windows does not care).
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public bool StopRan() => File.Exists(StopMarkerPath);
    public bool StartRan() => File.Exists(StartMarkerPath);

    /// <summary>
    /// The <c>ATP_BUILD_MANIFEST</c> value the last start-script run saw, or
    /// null when the script never ran or the variable was not set.
    /// </summary>
    public string? ReadStartManifestEnv()
    {
        if (!File.Exists(StartManifestEnvPath)) return null;
        var value = File.ReadAllText(StartManifestEnvPath).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Publishes one release commit carrying <paramref name="files"/> on
    /// origin/main and tags it, mirroring how an immutable release reaches the
    /// remote the Update Service fetches from. Returns the full commit SHA so
    /// the caller can write a candidate manifest that names it.
    /// </summary>
    public string PublishReleaseCommit(string tag, IReadOnlyDictionary<string, string> files)
    {
        var cloneDir = Path.Combine(Root, "release-work-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Run(GitPath, Root, "clone", "--branch", "main", RemoteDir, cloneDir);
        Run(GitPath, cloneDir, "config", "user.email", "test@example.com");
        Run(GitPath, cloneDir, "config", "user.name", "Update Service Test");
        foreach (var file in files)
        {
            var path = Path.Combine(cloneDir, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value.Replace("\r\n", "\n"));
        }
        Run(GitPath, cloneDir, "add", ".");
        Run(GitPath, cloneDir, "commit", "-m", $"release: {tag}");
        Run(GitPath, cloneDir, "tag", tag);
        Run(GitPath, cloneDir, "push", "origin", "main");
        Run(GitPath, cloneDir, "push", "origin", tag);
        return RunCapture(GitPath, cloneDir, "rev-parse", "HEAD");
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
