namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3792: probes the primary gate host (<c>RemoteGate:SshHost</c>) with one
/// bounded <c>ssh host true</c>, the same connection the gate transport opens.
/// It sends no bundle and starts no worker, so an outage costs one short SSH
/// connection per backoff step instead of a batch formed and abandoned.
/// </summary>
public sealed class SshGateHostProbe : IGateHostProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly IConfiguration _configuration;
    private readonly Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task> _runProcess;

    public SshGateHostProbe(IConfiguration configuration)
        : this(configuration, RemoteGateProcess.RunAsync)
    {
    }

    internal SshGateHostProbe(IConfiguration configuration,
        Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task> runProcess)
    {
        _configuration = configuration;
        _runProcess = runProcess;
    }

    public string? Host
    {
        get
        {
            try { return RemoteGateOptions.Read(_configuration).Host; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public async Task<GateHostProbeResult> ProbeAsync(CancellationToken ct)
    {
        if (Host is not { } host) return new GateHostProbeResult(false, "No gate host is configured.");
        try
        {
            await _runProcess("ssh", RemoteGateTransport.SshArguments(host, "true"), Path.GetTempPath(),
                ProbeTimeout, ct, null).ConfigureAwait(false);
            return new GateHostProbeResult(true);
        }
        catch (Exception error) when (error is IOException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            return new GateHostProbeResult(false, error.Message);
        }
    }
}
