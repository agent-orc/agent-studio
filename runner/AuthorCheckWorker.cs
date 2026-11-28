using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal sealed class AuthorCheckWorker(TaskServerClient client, PostWorkHostOptions options, string baseRepository)
{
    private static readonly PostWorkStep Build = new("author-check-build", "dotnet", [], 1200);

    public static bool Configured
        => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RUNNER_AUTHOR_CHECK_BASE"));

    public async Task<PostWorkClaimRequest> AdvertiseAsync(PostWorkClaimRequest request, string stateDir, CancellationToken ct)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(stateDir))!);
        var image = await PostWorkProcess.RunAsync(options.ContainerRuntime, ["image", "inspect", "--format", "{{.Id}}", options.Image!],
            stateDir, TimeSpan.FromSeconds(5), ct);
        return image.ExitCode == 0 && Directory.Exists(baseRepository)
            ? request with
            {
                AcceptAuthorCheck = true, AuthorCheckImage = options.Image, AuthorCheckDiskFreeBytes = drive.AvailableFreeSpace,
            }
            : request;
    }

    public async Task RunAsync(AuthorCheckWork work, string grant, PostWorkLease lease, string directory, CancellationToken shutdown)
    {
        var authority = Authority(lease);
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var watch = Stopwatch.StartNew();
        var safetyMargin = TimeSpan.FromSeconds(3);
        SetLocalDeadline(lease.ExpiresAt);
        using var stopRenewal = CancellationTokenSource.CreateLinkedTokenSource(lost.Token);
        var renew = RenewAsync();
        var output = new AuthorCheckOutput();
        string status;
        int? exit = null;
        try
        {
            Directory.CreateDirectory(directory);
            if (work.Profile.Image != options.Image || work.Profile.HalfCores != lease.GrantHalfCores)
                throw new InvalidDataException("The host's author-check image or grant differs from the approved profile.");
            var bundle = Path.Combine(directory, "source.bundle");
            await client.DownloadAuthorCheckSourceAsync(lease.AttemptId, new(authority, grant), work.Snapshot.Bytes, bundle, lost.Token);
            await AuthorCheckGit.VerifyAndMaterializeAsync(baseRepository, directory, work.Snapshot, lost.Token);
            // Refresh authority after import, immediately before a command can start.
            var renewed = await client.RenewPostWorkAsync(lease.AttemptId, new(authority, AuthorCheckProtocol.LeaseSeconds), lost.Token);
            SetLocalDeadline(renewed.ExpiresAt);
            exit = await BuildAsync(work.Profile, lease, directory, output, lost.Token);
            status = exit switch { 0 => "passed", 124 or 137 => "timed-out", 125 or 126 or 127 => "unavailable", _ => "failed" };
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested) { status = "unavailable"; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TaskServerException or System.ComponentModel.Win32Exception)
        {
            output.Add(Encoding.UTF8.GetBytes(ex is InvalidDataException ? ex.Message + "\n" : "Author-check infrastructure failed.\n"));
            status = "unavailable";
        }
        finally
        {
            await stopRenewal.CancelAsync();
            try { await renew; }
            catch (OperationCanceledException) when (stopRenewal.IsCancellationRequested) { }
            if (!await CleanupAsync(lease, directory, CancellationToken.None))
                throw new IOException("Author-check container cleanup needs retry.");
        }
        if (lost.IsCancellationRequested)
        {
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ReleaseContainedAsync(lease, release.Token);
            return;
        }
        var (log, omitted, redacted) = output.Complete();
        await client.SettleAuthorCheckAsync(lease.AttemptId,
            new(authority, status, exit, watch.ElapsedMilliseconds, log, omitted, redacted), lost.Token);

        void SetLocalDeadline(DateTime expiry)
        {
            var remaining = expiry - DateTime.UtcNow - safetyMargin;
            lost.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }

        async Task RenewAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(7));
            while (await timer.WaitForNextTickAsync(stopRenewal.Token))
            {
                try
                {
                    var renewed = await client.RenewPostWorkAsync(lease.AttemptId,
                        new(authority, AuthorCheckProtocol.LeaseSeconds), stopRenewal.Token);
                    SetLocalDeadline(renewed.ExpiresAt);
                }
                catch (Exception) when (!stopRenewal.IsCancellationRequested)
                {
                    await lost.CancelAsync();
                    return;
                }
            }
        }
    }

    private static PostWorkAuthority Authority(PostWorkLease lease)
        => new(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch);

    public Task ReleaseContainedAsync(PostWorkLease lease, CancellationToken ct)
        => client.ReleaseContainedAuthorCheckAsync(lease.AttemptId, Authority(lease), ct);

    public async Task<bool> CleanupAsync(PostWorkLease lease, string directory, CancellationToken ct)
    {
        var name = PostWorkContainerCommand.Name(lease, Build);
        await PostWorkProcess.RunAsync(options.ContainerRuntime, ["rm", "-f", name],
            Path.GetTempPath(), TimeSpan.FromSeconds(15), ct);
        var remaining = await PostWorkProcess.RunAsync(options.ContainerRuntime, PostWorkContainerCommand.RemoveByAttempt(lease),
            Path.GetTempPath(), TimeSpan.FromSeconds(10), ct);
        return remaining.ExitCode == 0 && string.IsNullOrWhiteSpace(remaining.Output)
            && PostWorkProcess.TryDeleteDirectory(directory, _ => { });
    }

    private async Task<int> BuildAsync(AuthorCheckProfile profile, PostWorkLease lease, string directory,
        AuthorCheckOutput output, CancellationToken ct)
    {
        var path = Path.Combine(directory, "source");
        if (path.Contains(',') || !File.Exists(Path.Combine(path, profile.Solution)))
            throw new InvalidDataException("The approved solution is absent from the snapshot.");
        var start = new ProcessStartInfo(options.ContainerRuntime)
        {
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "run", "--pull=never", "--rm", "--name", PostWorkContainerCommand.Name(lease, Build),
            "--label", "agent-studio.post-work.attempt=" + lease.AttemptId,
            "--label", "agent-studio.post-work.fence=" + lease.Fence,
            "--network=none", "--read-only", "--user=65532:65532", "--cap-drop=ALL",
            "--security-opt=no-new-privileges", "--pids-limit=256", "--log-driver=none",
            "--cpus", (profile.HalfCores / 2.0).ToString("0.0", CultureInfo.InvariantCulture),
            "--memory", profile.MemoryMiB + "m", "--memory-swap", profile.MemoryMiB + "m",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=4g", "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp",
            "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "--env", "DOTNET_CLI_USE_MSBUILD_SERVER=0",
            "--env", "MSBUILDDISABLENODEREUSE=1", "--env", "NUGET_PACKAGES=/tmp/packages",
            "--mount", $"type=bind,src={path},dst=/snapshot,readonly",
            "--entrypoint", "/usr/bin/timeout", profile.Image,
            "--kill-after=5s", profile.TimeoutSeconds + "s", "/bin/sh", "-ec",
            "exec 2>&1; mkdir /tmp/work; cp -R /snapshot/. /tmp/work; cd /tmp/work; " +
            "exec dotnet build \"$1\" --source /opt/author-check/feed -p:NuGetAudit=false --disable-build-servers",
            "author-check", "./" + profile.Solution,
        }) start.ArgumentList.Add(arg);
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Container runtime could not start.");
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await Task.WhenAll(CopyAsync(process.StandardOutput.BaseStream), CopyAsync(process.StandardError.BaseStream),
            process.WaitForExitAsync(ct));
        return process.ExitCode;

        async Task CopyAsync(Stream stream)
        {
            var buffer = new byte[8192];
            while (await stream.ReadAsync(buffer, ct) is var count && count > 0)
                output.Add(buffer.AsSpan(0, count));
        }
    }
}

internal sealed class AuthorCheckOutput
{
    private readonly MemoryStream _retained = new();
    private long _omitted;

    public void Add(ReadOnlySpan<byte> block)
    {
        lock (_retained)
        {
            var count = Math.Min(block.Length, AuthorCheckProtocol.LogLimit - (int)_retained.Length);
            _retained.Write(block[..count]);
            _omitted += block.Length - count;
        }
    }

    public (byte[] Log, long Omitted, bool Redacted) Complete()
    {
        var text = Encoding.UTF8.GetString(_retained.ToArray());
        // Discard a partial line at the ceiling so a truncated secret is never published.
        if (_omitted > 0 && !text.EndsWith('\n'))
        {
            var end = text.LastIndexOf('\n') + 1;
            _omitted += Encoding.UTF8.GetByteCount(text.AsSpan(end));
            text = text[..end];
        }
        var lines = text.Split('\n');
        var redacted = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var original = lines[index];
            if (original.Length > 16 * 1024)
            {
                _omitted += Encoding.UTF8.GetByteCount(original);
                lines[index] = "[oversized line withheld]";
            }
            else
                lines[index] = ReviewCommandExitDiagnosis.RedactOutput(original);
            redacted |= lines[index] != original;
        }
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        if (bytes.Length > AuthorCheckProtocol.LogLimit)
            return (Encoding.UTF8.GetBytes("[log withheld after redaction exceeded the retention limit]\n"),
                _omitted + _retained.Length, true);
        return (bytes, _omitted, redacted);
    }
}
