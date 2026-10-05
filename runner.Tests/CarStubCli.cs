using System.Text;

namespace AgentRunner.Tests;

internal static class CarStubCli
{
    public static string Write(string directory, string body)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The CAR shell stub is used only by Linux runner-host tests.");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"car-stub-{Guid.NewGuid():N}.sh");
        File.WriteAllText(
            path,
            "#!/bin/sh\n"
            + "if [ \"$1\" = \"--version\" ]; then printf 'claude-test 1.0\\n'; exit 0; fi\n"
            + body
            + "\n",
            new UTF8Encoding(false));
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
