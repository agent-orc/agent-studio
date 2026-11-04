using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Dispatch;

namespace AgentStudio.Operations.Server.Features.Access;

/// <summary>The Studio publishes one monotonically versioned KRL for host agents to install.</summary>
public sealed class HostRevocationStore(string directory)
{
    private readonly object gate = new();
    private string PathName => Path.Combine(directory, "host-revocations.json");

    public HostRevocationList? Read()
    {
        lock (gate)
        {
            if (!File.Exists(PathName)) return null;
            if (new FileInfo(PathName).Length > 32768)
                throw new InvalidDataException("Host revocation store is too large.");
            return JsonSerializer.Deserialize<HostRevocationList>(File.ReadAllText(PathName), OperationsProtocol.Json);
        }
    }

    public void Publish(HostRevocationList list)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(list.Base64Krl); }
        catch (FormatException) { throw new OperationRejected("invalid-revocation-list", 400); }
        if (list.Version < 1 || bytes.Length is < 8 or > 16 * 1024
            || !bytes.AsSpan(0, 8).SequenceEqual("SSHKRL\n\0"u8)
            || Convert.ToHexStringLower(SHA256.HashData(bytes)) != list.Sha256)
            throw new OperationRejected("invalid-revocation-list", 400);
        lock (gate)
        {
            var current = Read();
            if (current is not null)
            {
                if (list.Version == current.Version && list.Sha256 == current.Sha256) return;
                if (list.Version <= current.Version) throw new OperationRejected("stale-revocation-list");
            }
            var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    JsonSerializer.Serialize(stream, list, OperationsProtocol.Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, PathName, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
