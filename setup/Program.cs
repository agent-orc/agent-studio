using AgentStudio.Setup;
using System.Diagnostics;

if (args.FirstOrDefault() == "serve-native")
    return await NativeWebHost.RunAsync(args.Skip(1).ToArray());

const string launchDirectoryOption = "--setup-launch-directory";
if (args.Length >= 2 && args[^2] == launchDirectoryOption)
{
    Directory.SetCurrentDirectory(args[^1]);
    args = args[..^2];
}

if (OperatingSystem.IsWindows() && ProductSetup.IsProductCommand(args)
    && !Environment.IsPrivilegedProcess &&
    !args.Contains("--dry-run") && !args.Contains("--help") && !args.Contains("-h") &&
    !args.Contains("--version") && args.FirstOrDefault() is not ("preflight" or "accept"))
{
    var elevated = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = true,
        Verb = "runas",
        CreateNoWindow = true,
        WorkingDirectory = Environment.SystemDirectory,
    };
    foreach (var argument in args) elevated.ArgumentList.Add(argument);
    elevated.ArgumentList.Add(launchDirectoryOption);
    elevated.ArgumentList.Add(Environment.CurrentDirectory);
    try
    {
        using var child = Process.Start(elevated)
            ?? throw new InvalidOperationException("Windows did not start elevated setup.");
        await child.WaitForExitAsync();
        return child.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
    {
        Console.Error.WriteLine("Setup needs administrator rights to install boot-time Windows services. UAC was cancelled.");
        return 1;
    }
}

var exitCode = ProductSetup.IsProductCommand(args)
    ? await ProductSetup.RunAsync(args)
    : await SetupApplication.RunAsync(args);
if (OperatingSystem.IsWindows() && !Console.IsInputRedirected
    && !args.Contains("--unattended") && !args.Contains("--non-interactive")
    && !args.Contains("--help") && !args.Contains("-h") && !args.Contains("--version"))
{
    Console.WriteLine("Press Enter to close setup.");
    Console.ReadLine();
}
return exitCode;
