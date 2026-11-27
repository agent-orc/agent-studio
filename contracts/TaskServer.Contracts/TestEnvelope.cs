using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>A named scheduling shape; each process receives one shared CPU token.</summary>
public sealed record TestEnvelopeDefinition(string Name, int Processes);

/// <summary>RT-D10 B: the process boundary shared by review, gate and post-merge tests.</summary>
public static class TestEnvelope
{
    public const int DefaultProcessorCount = 3;
    public const string CanaryName = "TestEnvelopeCanaryTests";
    public static readonly IReadOnlyDictionary<string, string> EmptyEnvironment = new Dictionary<string, string>();
    private static readonly string[] ToolVariables =
    [
        "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "LANG", "LC_ALL", "LC_CTYPE",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "PLAYWRIGHT_BROWSERS_PATH",
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT",
    ];

    // The same setting is read by all hosts, and never inherited by the child.
    public static int ProcessorCount => int.TryParse(Environment.GetEnvironmentVariable("TEST_ENVELOPE_CORES"),
        NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 64 ? count : DefaultProcessorCount;
    public static string CpuMax => $"{(long)ProcessorCount * 100000} 100000";
    // Keep room for the test host's ats-* child root and Unix socket names.
    public static string NewRunRoot() => Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp",
        "at-" + Guid.NewGuid().ToString("N")[..12]);
    public const string ReferenceShardPlan = "reference=1";
    public static TestEnvelopeDefinition Standard { get; } = new("standard", 1);
    public static TestEnvelopeDefinition Reference { get; } = new("reference", 1);

    public static Dictionary<string, string?> BuildEnvironment(IReadOnlyDictionary<string, string> declared,
        string home, string temp, Func<string, string?>? host = null)
    {
        host ??= Environment.GetEnvironmentVariable;
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ToolVariables)
            if (host(name) is { } value) result[name] = value;
        result["LANG"] = result.GetValueOrDefault("LANG") ?? "C.UTF-8";
        result["LC_ALL"] = result.GetValueOrDefault("LC_ALL") ?? "C.UTF-8";
        // Declared daemon settings remain visible so the canary diagnoses a bad project declaration.
        // Credentials are never supplied, even by a project declaration.
        foreach (var (name, value) in declared)
            if (!IsCredential(name)) result[name] = value;
        result["HOME"] = home;
        result["USERPROFILE"] = home;
        result["TMPDIR"] = result["TMP"] = result["TEMP"] = temp;
        result["DOTNET_CLI_HOME"] = Path.Combine(home, "dotnet");
        result["XDG_CACHE_HOME"] = Path.Combine(home, ".cache");
        result["MSBUILDDISABLENODEREUSE"] = "1";
        result["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        result["UseSharedCompilation"] = "false";
        result["MSBUILDDEBUGPATH"] = temp;
        result["DOTNET_PROCESSOR_COUNT"] = ProcessorCount.ToString(CultureInfo.InvariantCulture);
        result["TEST_ENVELOPE_HOME"] = home;
        result["TEST_ENVELOPE_TEMP"] = temp;
        result["TEST_ENVELOPE_SHAPE"] = Standard.Name;
        return result;
    }

    public static bool IsCredential(string name)
        => name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
           || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
           || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("API_KEY", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("GOOGLE_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("GEMINI_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("COPILOT_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("GH_", StringComparison.OrdinalIgnoreCase);

    public static string NamesDigest(IEnumerable<string> names) => Digest(string.Join("\n",
        names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    public static string DeclaredDigest(IReadOnlyDictionary<string, string> declared) => Digest(string.Join("\n",
        declared.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)));
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    public static bool MatchesReference(PostMergeEnvelopeFingerprint actual, PostMergeEnvelopeFingerprint reference)
        => actual.AllowlistDigest == reference.AllowlistDigest
           && actual.DeclaredEnvironmentDigest == reference.DeclaredEnvironmentDigest
           && actual.CgroupKind == reference.CgroupKind && actual.CpuMax == reference.CpuMax
           && actual.ProcessorCount == reference.ProcessorCount && actual.SdkVersion == reference.SdkVersion;

    public static bool CanaryFailed(IEnumerable<string> failedTests)
        => failedTests.Any(test => test.Contains(CanaryName, StringComparison.Ordinal));

    // Older post-merge workers and direct dotnet test invocations have no envelope
    // declaration. Any marker makes the canary enforce the complete contract.
    public static bool HasEnvelopeDeclaration(IReadOnlyDictionary<string, string?> environment)
        => environment.Keys.Any(name => name.StartsWith("TEST_ENVELOPE_", StringComparison.OrdinalIgnoreCase));

    /// <summary>Inside-process assertions, also usable by fixture tests without exposing values.</summary>
    public static IReadOnlyList<string> Violations(IReadOnlyDictionary<string, string?> environment, string? cgroup)
    {
        var errors = new List<string>();
        foreach (var name in environment.Keys)
            if (name.StartsWith("RUNNER_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("AGENT_HOST_", StringComparison.OrdinalIgnoreCase) || IsCredential(name))
                errors.Add("Forbidden variable: " + name);
        if (!environment.TryGetValue("TEST_ENVELOPE_HOME", out var home) || string.IsNullOrEmpty(home)
            || environment.GetValueOrDefault("HOME") != home) errors.Add("HOME is not owned by this run.");
        if (!int.TryParse(environment.GetValueOrDefault("DOTNET_PROCESSOR_COUNT"), out var count) || count < 1)
            errors.Add("Processor count is missing.");
        if (OperatingSystem.IsLinux() && environment.GetValueOrDefault("TEST_ENVELOPE_CGROUP") == "worker"
            && !(cgroup?.Split('/').Any(part => part.StartsWith("worker-", StringComparison.Ordinal)) ?? false))
            errors.Add("The process is outside its worker cgroup.");
        return errors;
    }
}

/// <summary>Fresh per-command writable roots and Linux cgroup. No delegation is measured, not fatal.</summary>
public sealed class TestCommandEnvelope : IDisposable
{
    private readonly string _root;
    private readonly string? _cgroup;
    private readonly Action<string>? _log;
    public Dictionary<string, string?> Environment { get; }
    private readonly double? _startLoad = LoadPerCore();
    private double? _peakLoad;
    private readonly Timer _sampler;
    private string? _sdkVersion;
    public string CgroupKind => _cgroup is null ? (OperatingSystem.IsLinux() ? "unavailable" : "none") : "worker";

    public TestCommandEnvelope(IReadOnlyDictionary<string, string> declared, Action<string>? log = null,
        string? home = null, string? temp = null, TestEnvelopeDefinition? definition = null)
        : this(declared, log, home, temp, definition, null, null) { }

    internal TestCommandEnvelope(IReadOnlyDictionary<string, string> declared, Action<string>? log,
        string? home, string? temp, TestEnvelopeDefinition? definition, string? mountRoot, string? selfCgroup)
    {
        _log = log;
        _peakLoad = _startLoad;
        _sampler = new Timer(_ => { var load = LoadPerCore(); if (load is { } value)
            _peakLoad = Math.Max(_peakLoad ?? value, value); }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        _root = TestEnvelope.NewRunRoot();
        home ??= Path.Combine(_root, "home");
        temp ??= Path.Combine(_root, "t");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(temp);
        Environment = TestEnvelope.BuildEnvironment(declared, home, temp);
        _cgroup = TryCreateWorker(log, mountRoot ?? "/sys/fs/cgroup", selfCgroup);
        Environment["TEST_ENVELOPE_CGROUP"] = CgroupKind;
        Environment["TEST_ENVELOPE_SHAPE"] = (definition ?? TestEnvelope.Standard).Name;
    }

    private static string? TryCreateWorker(Action<string>? log, string mountRoot, string? selfCgroup)
    {
        if (!OperatingSystem.IsLinux()) return null;
        string? leaf = null;
        try
        {
            var relative = selfCgroup ?? File.ReadAllLines("/proc/self/cgroup").First(line => line.StartsWith("0::", StringComparison.Ordinal))[3..];
            var own = new DirectoryInfo(Path.Combine(mountRoot, relative.TrimStart('/')));
            // Only create siblings under an already delegated service; never reshape an arbitrary host cgroup.
            var service = own.Parent;
            if (service is null || !service.Name.EndsWith(".service", StringComparison.Ordinal)
                || !File.ReadAllText(Path.Combine(service.FullName, "cgroup.subtree_control")).Contains("cpu", StringComparison.Ordinal))
                throw new IOException("No delegated service subtree.");
            leaf = Path.Combine(service.FullName, "worker-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(leaf);
            if (!File.Exists(Path.Combine(leaf, "cpu.max"))) throw new IOException("CPU controller unavailable.");
            File.WriteAllText(Path.Combine(leaf, "cpu.max"), TestEnvelope.CpuMax);
            if (File.Exists(Path.Combine(leaf, "pids.max"))) File.WriteAllText(Path.Combine(leaf, "pids.max"), "4096");
            return leaf;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke("Test envelope cgroup unavailable: " + ex.Message);
            if (leaf is not null) try { Directory.Delete(leaf); } catch (IOException cleanup) { log?.Invoke("Test envelope cleanup: " + cleanup.Message); }
            return null;
        }
    }

    public (string FileName, IReadOnlyList<string> Arguments) Launch(string fileName, IReadOnlyList<string> arguments)
        => _cgroup is null ? (fileName, arguments) : ("/bin/sh", new[] { "-c",
            "printf '%s\\n' \"$$\" >\"$1\" || { echo 'test envelope attach failed' >&2; exit 125; }; shift; exec \"$@\"", "test-envelope",
            Path.Combine(_cgroup, "cgroup.procs"), fileName }.Concat(arguments).ToArray());

    public PostMergeEnvelopeFingerprint Fingerprint(IReadOnlyDictionary<string, string> declared,
        string shardPlan, string? sdkVersion = null, double? startLoad = null, double? peakLoad = null)
        => new(TestEnvelope.NamesDigest(TestEnvelope.BuildEnvironment(declared, "home", "temp").Keys.Append("TEST_ENVELOPE_CGROUP")), TestEnvelope.DeclaredDigest(declared), CgroupKind,
            _cgroup is null ? null : File.ReadAllText(Path.Combine(_cgroup, "cpu.max")).Trim(),
            int.Parse(Environment["DOTNET_PROCESSOR_COUNT"]!, CultureInfo.InvariantCulture), sdkVersion ?? _sdkVersion, shardPlan.Length <= 512 ? shardPlan : shardPlan[..512], startLoad ?? _startLoad, peakLoad ?? _peakLoad);

    public async Task ProbeSdkAsync(string workingDirectory, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("dotnet")
            { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--version");
            info.Environment.Clear();
            foreach (var (name, value) in Environment) info.Environment[name] = value;
            using var process = System.Diagnostics.Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
            var version = (await stdout).Trim();
            await stderr;
            if (process.ExitCode == 0 && version.Length is > 0 and <= 64) _sdkVersion = version;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
            || ex is OperationCanceledException && !ct.IsCancellationRequested) { _log?.Invoke("Test SDK probe unavailable: " + ex.Message); }
    }

    private static double? LoadPerCore()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            // Host load is independent of the command's cgroup quota and processor setting.
            var cores = File.ReadLines("/proc/stat").Count(line => line.Length > 3
                && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(line[3]));
            return cores == 0 ? null : double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], CultureInfo.InvariantCulture) / cores;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    public void Dispose()
    {
        _sampler.Dispose();
        if (_cgroup is not null)
        {
            try
            {
                File.WriteAllText(Path.Combine(_cgroup, "cgroup.kill"), "1");
                Directory.Delete(_cgroup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.Invoke("Test envelope cleanup: " + ex.Message); }
        }
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException ex) { _log?.Invoke("Test envelope cleanup: " + ex.Message); }
    }
}

/// <summary>Writable roots shared by prepare, build and tests of one run, removed on completion.</summary>
public sealed class TestEnvelopeWorkspace : IDisposable
{
    private readonly string _root = TestEnvelope.NewRunRoot();
    public string Home => Path.Combine(_root, "home");
    public string Temp => Path.Combine(_root, "t");
    public TestEnvelopeWorkspace() { Directory.CreateDirectory(Home); Directory.CreateDirectory(Temp); }
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Test envelope workspace cleanup: " + ex.Message); }
    }
}
