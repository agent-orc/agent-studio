using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>A process-held, cross-process fenced lease for one project's integration ref.</summary>
public sealed class ProjectRefMutationLease : IDisposable
{
    private readonly FileStream _guard;
    private readonly string _recordPath;
    internal ProjectRefMutationLease(FileStream guard, string recordPath, long fence)
    {
        _guard = guard;
        _recordPath = recordPath;
        Fence = fence;
    }

    public long Fence { get; }
    public bool IsCurrent
    {
        get
        {
            try
            {
                if (!_guard.CanRead) return false;
                return long.TryParse(File.ReadAllText(_recordPath), out var current)
                    && current == Fence;
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public void Dispose() => _guard.Dispose();
}

public sealed class ProjectRefMutationLeaseService
{
    private readonly string _root;

    public ProjectRefMutationLeaseService(string? root = null)
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _root = root ?? Path.Combine(data, "agentstudio", "ref-mutation-leases");
    }

    public async Task<ProjectRefMutationLease> AcquireAsync(
        string project, string repository, string integrationBranch, CancellationToken ct)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { project, repository, integrationBranch }))));
        var folder = Path.Combine(_root, key);
        Directory.CreateDirectory(folder);
        var guardPath = Path.Combine(folder, "lock");
        var recordPath = Path.Combine(folder, "fence");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var guard = new FileStream(guardPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                try
                {
                    var previous = File.Exists(recordPath)
                        ? long.Parse(File.ReadAllText(recordPath)) : 0;
                    var fence = checked(previous + 1);
                    var temp = recordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temp, FileMode.CreateNew,
                               FileAccess.Write, FileShare.None))
                    {
                        var bytes = Encoding.UTF8.GetBytes(fence.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                        stream.Write(bytes);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temp, recordPath, overwrite: true);
                    return new ProjectRefMutationLease(guard, recordPath, fence);
                }
                catch
                {
                    guard.Dispose();
                    throw;
                }
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }
}
