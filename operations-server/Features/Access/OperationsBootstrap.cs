using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Server.Features.Access;

public static class OperationsBootstrap
{
    public static void Run(string principalsDirectory, string agentDirectory, string clientDirectory)
    {
        var path = Path.Combine(principalsDirectory, "principals.json");
        var agentPath = Path.Combine(agentDirectory, "token");
        var clientPath = Path.Combine(clientDirectory, "token");
        var principalsExist = File.Exists(path);
        if (principalsExist)
        {
            var access = new OperationsAccess(path);
            ValidateCredential(access, agentPath, "container-agent", "agent");
            ValidateCredential(access, clientPath, "diagnostics-client", "service");
        }

        foreach (var directory in new[] { principalsDirectory, agentDirectory, clientDirectory })
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(clientDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        if (principalsExist && !OperatingSystem.IsWindows()) File.SetUnixFileMode(clientPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        GrantStudioGroup(clientDirectory, clientPath, principalsExist);
        if (principalsExist) return;

        var agent = Credential(agentPath);
        var client = Credential(clientPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(clientPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        GrantStudioGroup(clientDirectory, clientPath, true);
        var principals = new OperationsAccessDocument([
            new("diagnostics-client", OperationsProtocol.Audience, OperationsProtocol.Digest(client), "service",
                ["host.inspect", "service.restart", "host.drain", "host.resume", "host.logs.collect",
                    "host.configuration.apply", "maintenance.execute", "operations.read", "certificates.revoke"], ["container-agent"], []),
            new("container-agent", OperationsProtocol.Audience, OperationsProtocol.Digest(agent), "agent",
                ["agents.connect", "host.inspect", "service.restart", "host.drain", "host.resume",
                    "host.logs.collect", "host.configuration.apply"], ["container-agent"], []),
        ]);
        WriteNew(path, JsonSerializer.Serialize(principals, OperationsProtocol.Json));
    }

    private static void ValidateCredential(OperationsAccess access, string path, string principalId, string kind)
    {
        var token = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        var principal = string.IsNullOrEmpty(token) ? null : access.Authenticate(token);
        if (principal?.Id != principalId || principal.Kind != kind)
            throw new InvalidDataException($"Operations credential for '{principalId}' is missing or does not match the persisted principal store. Restore the matching secret volume from backup before restarting bootstrap. No credentials were changed.");
    }

    private static string Credential(string path)
    {
        if (!File.Exists(path)) WriteNew(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        return File.ReadAllText(path).Trim();
    }

    private static void WriteNew(string path, string content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void GrantStudioGroup(string directory, string tokenPath, bool tokenExists)
    {
        if (OperatingSystem.IsWindows() || GetEffectiveUserId() != 0) return;
        // The API container runs with this credential group.
        // Only that group can traverse the volume and read the service token.
        if (Chown(directory, 0, 1654) != 0 || (tokenExists && Chown(tokenPath, 0, 1654) != 0))
            throw new IOException("Cannot assign the Operations service credential group.");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);
}
