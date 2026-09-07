using AgentStudio.Scenario;

var parsed = ScenarioCommandLine.Parse(args, Environment.GetEnvironmentVariable);
if (parsed.HelpRequested)
{
    Console.WriteLine(ScenarioCommandLine.Usage);
    return ScenarioExitCode.Passed;
}
if (parsed.Error is { } usageError)
{
    Console.Error.WriteLine($"scenario: {usageError}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(ScenarioCommandLine.Usage);
    return ScenarioExitCode.UsageError;
}

var options = parsed.Options!;
var repositoryRoot = Path.GetFullPath(options.RepositoryRoot);
var documentPath = Path.IsPathRooted(options.DocumentPath)
    ? options.DocumentPath
    : Path.Combine(repositoryRoot, options.DocumentPath);
if (!File.Exists(documentPath))
{
    Console.Error.WriteLine($"scenario: scenario document not found: {documentPath}");
    return ScenarioExitCode.DocumentInvalid;
}

var loaded = ScenarioDocumentLoader.Parse(await File.ReadAllTextAsync(documentPath));
if (loaded.Document is null)
{
    Console.Error.WriteLine($"scenario: {loaded.Error}");
    return ScenarioExitCode.DocumentInvalid;
}

var document = loaded.Document;
var plan = ScenarioPlanner.Plan(document, options.Target, options.Level);
var outputDirectory = Path.GetFullPath(
    Path.IsPathRooted(options.OutputDirectory)
        ? options.OutputDirectory
        : Path.Combine(repositoryRoot, options.OutputDirectory));
var evidenceDirectory = Path.Combine(outputDirectory, "evidence");
Directory.CreateDirectory(evidenceDirectory);

var targetName = ScenarioDocumentLoader.Render(options.Target);
var levelName = ScenarioDocumentLoader.Render(options.Level);
Console.WriteLine($"scenario: {document.Id} target={targetName} level={levelName}");
Console.WriteLine(
    $"scenario: {plan.Count(step => step.Disposition == ScenarioStepDisposition.Run)} "
    + $"of {plan.Count} steps planned");

var workRoot = Path.Combine(
    Path.GetTempPath(), $"agent-studio-scenario-{Environment.ProcessId}");
Directory.CreateDirectory(workRoot);
var startedAt = DateTimeOffset.UtcNow;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

ScenarioFixture? fixture = null;
IScenarioTarget? target = null;
try
{
    fixture = await ScenarioFixture.CreateAsync(
        Path.Combine(workRoot, "fixture"), cancellation.Token);
    target = options.Target switch
    {
        ScenarioTargetKind.InProc => new InProcScenarioTarget(
            repositoryRoot, fixture, Path.Combine(workRoot, "inproc")),
        ScenarioTargetKind.Compose => new ComposeScenarioTarget(
            repositoryRoot,
            fixture,
            Path.Combine(workRoot, "compose"),
            $"agent-studio-scenario-{Environment.ProcessId}"),
        _ => new RemoteScenarioTarget(
            options.RemoteTaskServerUrl!, options.RemoteStudioUrl, options.RemoteToken!),
    };

    var endpoints = await target.StartAsync(cancellation.Token);
    using var context = new ScenarioRunContext(target, endpoints, fixture, evidenceDirectory)
    {
        Server = new ScenarioHttpClient(
            endpoints.TaskServerUrl, endpoints.ManagementToken, "agent-studio-scenario"),
        Studio = endpoints.StudioUrl is null
            ? null
            : new ScenarioHttpClient(endpoints.StudioUrl, null, "agent-studio-scenario"),
    };

    var result = await ScenarioRunner.RunAsync(
        document, plan, context, options.Target, options.Level, startedAt, cancellation.Token);

    var reportPath = Path.Combine(outputDirectory, $"scenario-{targetName}-{levelName}.md");
    var junitPath = Path.Combine(outputDirectory, $"scenario-{targetName}-{levelName}.junit.xml");
    await File.WriteAllTextAsync(reportPath, ScenarioMarkdownReport.Render(result));
    await File.WriteAllTextAsync(junitPath, ScenarioJUnitReport.Render(result));

    Console.WriteLine();
    Console.WriteLine(
        $"scenario: {(result.Passed ? "passed" : "FAILED")} "
        + $"({result.PassedCount} passed, {result.Failures} failed, {result.SkippedCount} skipped) "
        + $"in {result.Duration.TotalSeconds:0.0} s");
    Console.WriteLine($"scenario: report {reportPath}");
    Console.WriteLine($"scenario: junit  {junitPath}");
    if (!result.Passed)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(target.Diagnostics());
    }
    return result.Passed ? ScenarioExitCode.Passed : ScenarioExitCode.StepFailed;
}
catch (ScenarioTargetException exception)
{
    Console.Error.WriteLine($"scenario: target unavailable: {exception.Message}");
    return ScenarioExitCode.TargetUnavailable;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("scenario: interrupted.");
    return ScenarioExitCode.TargetUnavailable;
}
finally
{
    if (target is not null) await target.DisposeAsync();
    fixture?.Dispose();
    if (!options.KeepEvidence && Directory.Exists(workRoot))
    {
        try
        {
            Directory.Delete(workRoot, recursive: true);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"scenario: work directory cleanup skipped: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"scenario: work directory cleanup skipped: {exception.Message}");
        }
    }
}
