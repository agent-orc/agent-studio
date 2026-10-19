using AgentStudio.TaskServer.Contracts;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/project-deployment-import -- <repository-path>");
    return 2;
}

var result = ProjectDeploymentImport.ImportWorkspace(Path.GetFullPath(args[0]));
foreach (var issue in result.Issues)
    Console.Error.WriteLine($"{issue.Path}: {issue.Message} ({issue.Code})");
if (result.Issues.Count > 0) return 1;
Console.WriteLine(result.Changed
    ? $"Imported {result.ImportedFiles.Count} deployment descriptor(s) into .agent-studio/project.yml. Review and commit the change."
    : "No legacy descriptors need import.");
return 0;
