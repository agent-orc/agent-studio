using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Server.Features.Access;

public static class OperationsBootstrap
{
    public static void Run(string principalsDirectory, string agentDirectory, string clientDirectory)
    {
        var path = Path.Combine(principalsDirectory, "principals.json");
        var agentPath = Path.Combine(agentDirectory, "token");
        var clientPath = Path.Combine(clientDirectory, "token");
        if (File.Exists(path))
        {
            var access = new OperationsAccess(path);
            ValidateCredential(access, agentPath, "container-agent", "agent");
            ValidateCredential(access, clientPath, "diagnostics-client", "service");
            return;
        }

        foreach (var directory in new[] { principalsDirectory, agentDirectory, clientDirectory })
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var agent = Credential(agentPath);
        var client = Credential(clientPath);
        var principals = new OperationsAccessDocument([
            new("diagnostics-client", OperationsProtocol.Audience, OperationsProtocol.Digest(client), "service",
                ["host.inspect", "maintenance.execute", "operations.read"], ["container-agent"], []),
            new("container-agent", OperationsProtocol.Audience, OperationsProtocol.Digest(agent), "agent",
                ["agents.connect", "host.inspect"], ["container-agent"], []),
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
}
