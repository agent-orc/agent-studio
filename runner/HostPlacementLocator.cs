using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// AGT-3954: detects once where this daemon runs (container, virtual machine
/// or physical machine) and keeps the answer for every telemetry sample. The
/// file markers answer immediately; <c>systemd-detect-virt</c> refines the
/// answer in the background on hosts that have it, so a sample never waits
/// for a process.
/// </summary>
public static class HostPlacementLocator
{
    private static readonly object Gate = new();
    private static HostPlacementDto? _current;
    private static string? _key;
    private static Task? _refinement;

    public static HostPlacementDto? Current(RunnerOptions? options)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var key = $"{options?.ParentMachine}\n{options?.ContainerName}\n{options?.Hostname}";
        lock (Gate)
        {
            if (_current is not null && _key == key) return _current;
            _key = key;
            _current = Detect(options, null, null);
            _refinement = RefineAsync(options, key);
            return _current;
        }
    }

    private static HostPlacementDto Detect(RunnerOptions? options, string? vm, string? container)
        => HostPlacementProbe.DetectLocal(vm, container, options?.ContainerName,
            options?.ParentMachine, options?.Hostname is { Length: > 0 } host ? host : null);

    private static async Task RefineAsync(RunnerOptions? options, string key)
    {
        var vm = await DetectVirtAsync("--vm").ConfigureAwait(false);
        var container = await DetectVirtAsync("--container").ConfigureAwait(false);
        if (vm is null && container is null) return;
        var refined = Detect(options, vm, container);
        lock (Gate)
            if (_key == key) _current = refined;
    }

    /// <summary>Null when the tool is absent or fails; <c>none</c> is a real answer.</summary>
    private static async Task<string?> DetectVirtAsync(string scope)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var result = await ProcessRunner.RunAsync("systemd-detect-virt", [scope], ct: deadline.Token)
                .ConfigureAwait(false);
            var answer = result.StdOut.Trim();
            // The tool exits non-zero with "none" when it detects nothing.
            return answer.Length > 0 && answer.Length <= 40 && !answer.Contains(' ') ? answer : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException
                                              or OperationCanceledException or InvalidOperationException)
        { return null; }
    }
}
