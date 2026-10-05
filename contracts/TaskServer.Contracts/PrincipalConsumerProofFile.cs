namespace AgentStudio.TaskServer.Contracts;

/// <summary>Host-owned proof installed beside a rotated service bearer.</summary>
public static class PrincipalConsumerProofFile
{
    public sealed record Proof(string ConsumerId, string Value);

    public static Proof? ReadForBearerFile(string bearerFile)
    {
        var path = bearerFile + ".consumer-proof";
        if (!File.Exists(path)) return null;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            || !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                 UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("The principal consumer proof must be a private regular file.");
        var lines = File.ReadAllLines(path);
        if (lines.Length != 2 || lines[0].Length is < 1 or > 128 ||
            lines[1].Length != 64 || !lines[1].All(Uri.IsHexDigit))
            throw new InvalidOperationException("The principal consumer proof file is invalid.");
        return new Proof(lines[0], lines[1]);
    }
}
