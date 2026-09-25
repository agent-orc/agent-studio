using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Server.Features.Access;

public static class OperationsBootstrap
{
    public static void Run(string principalsDirectory, string agentDirectory, string clientDirectory)
    {
        foreach (var directory in new[] { principalsDirectory, agentDirectory, clientDirectory })
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var agent = Credential(Path.Combine(agentDirectory, "token"));
        var client = Credential(Path.Combine(clientDirectory, "token"));
        var path = Path.Combine(principalsDirectory, "principals.json");
        if (File.Exists(path)) return;
        var principals = new OperationsAccessDocument([
            new("diagnostics-client", OperationsProtocol.Audience, OperationsProtocol.Digest(client), "service",
                ["host.inspect", "maintenance.execute", "operations.read"], ["container-agent"], []),
            new("container-agent", OperationsProtocol.Audience, OperationsProtocol.Digest(agent), "agent",
                ["agents.connect", "host.inspect"], ["container-agent"], []),
        ]);
        WriteNew(path, JsonSerializer.Serialize(principals, OperationsProtocol.Json));
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
