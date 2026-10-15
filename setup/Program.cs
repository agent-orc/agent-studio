using AgentStudio.Setup;
using System.Diagnostics;

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
        WorkingDirectory = Environment.CurrentDirectory,
    };
    foreach (var argument in args) elevated.ArgumentList.Add(argument);
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
