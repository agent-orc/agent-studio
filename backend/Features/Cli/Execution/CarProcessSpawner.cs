using System.Diagnostics;
using CodingAgentRunner.Abstractions;

namespace AgentStudio.Cli;

/// <summary>
/// Host adapter for CAR's documented <see cref="ICliProcessSpawner"/> seam.
/// CAR has already built and hardened the launch. The decorator may add the
/// one host-owned argument that CAR 0.7.0 cannot express, then delegates to an
/// injected spawner or to the same redirected-pipe launch used by CAR's
/// built-in fallback. The spawned process is retained only long enough for the
/// host to attach its active-job and Windows job-object bookkeeping.
/// </summary>
internal sealed class DecoratingCliProcessSpawner(
    Action<ProcessStartInfo>? decorate,
    ICliProcessSpawner? inner = null,
    Action<Process>? onSpawned = null,
    bool networked = false) : ICliProcessSpawner
{
    private Process? _spawnedProcess;

    public Process? SpawnedProcess => Volatile.Read(ref _spawnedProcess);

    public CliSpawn Spawn(ProcessStartInfo startInfo)
    {
        decorate?.Invoke(startInfo);
        HostCliSpawnGuard.Validate(startInfo, networked);
        if (networked)
            throw new InvalidOperationException("Host task-agent execution is disabled in the networked profile.");

        CliSpawn spawn;
        if (inner != null)
        {
            spawn = inner.Spawn(startInfo);
        }
        else
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            var stdin = startInfo.RedirectStandardInput
                ? process.StandardInput.BaseStream
                : Stream.Null;
            spawn = new CliSpawn(
                process,
                stdin,
                process.StandardOutput,
                process.StandardError);
        }

        Volatile.Write(ref _spawnedProcess, spawn.Process);
        onSpawned?.Invoke(spawn.Process);
        return spawn;
    }
}
