namespace AgentRunner;

/// <summary>One host-owned verification command uses the same envelope as a coding worker.</summary>
internal sealed class PostMergeCommandEnvelope : IDisposable
{
    private readonly string _workerDirectory;
    private readonly WorkerCgroup _cgroup;
    private readonly Action<string> _log;

    internal PostMergeCommandEnvelope(string workerDirectory, WorkerCgroup cgroup, Action<string> log)
    {
        _workerDirectory = workerDirectory;
        _cgroup = cgroup;
        _log = log;
    }

    internal static PostMergeCommandEnvelope? Prepare(RunnerOptions? hostOptions, Action<string> log)
    {
        if (hostOptions is null || !OperatingSystem.IsLinux()) return null;
        var directory = Path.Combine(Path.GetTempPath(), "agent-post-merge-envelope", Guid.NewGuid().ToString("N"));
        var cgroup = WorkerCgroup.TryPrepare(hostOptions, directory, log);
        if (cgroup is null)
            throw new IOException("Host-owned post-merge commands require a worker cgroup; execution in the daemon leaf is refused.");
        log($"post-merge command envelope cgroup={cgroup.CgroupDirectory} {cgroup.Envelope.Describe()}");
        return new PostMergeCommandEnvelope(directory, cgroup, log);
    }

    /// <summary>The leaf's <c>cpu.max</c> when readable (RT-3 envelope fingerprint).</summary>
    internal string? CpuMax => PostMergeEnvelopeProbe.ReadCpuMax(_cgroup.CgroupDirectory);

    internal WorkerLaunch Launch(string fileName, IReadOnlyList<string> arguments)
        => WorkerCgroup.Wrap(_cgroup.ProcsPath, fileName, arguments, requireAttachment: true);

    internal IReadOnlyDictionary<string, string?> ChildEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        var environment = new Dictionary<string, string?>(values, StringComparer.Ordinal);
        // A host-shared compiler would perform the build outside this cgroup.
        ReviewBuildServerIsolation.ApplyTo(environment, _workerDirectory);
        return environment;
    }

    public void Dispose()
    {
        var leftovers = WorkerCgroup.ReleaseFor(_workerDirectory, _log);
        _log($"post-merge command envelope released leftovers={leftovers}");
        Directory.Delete(_workerDirectory, recursive: true);
    }
}
