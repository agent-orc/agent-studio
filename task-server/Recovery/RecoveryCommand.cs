using System.Globalization;
using System.Text.Json;

namespace AgentStudio.TaskServer.Recovery;

/// <summary>
/// Offline boundary for <c>task-server recovery</c>. The serving process must be stopped for every
/// operation that opens the store, so the command is the only writer while it runs.
/// Exit codes: 0 success, 1 failure, 2 verification or gate refused.
/// </summary>
public static class RecoveryCommand
{
    private const string Actor = "task-server-recovery-command";

    public static async Task<int> RunAsync(RecoveryCommandLine command, TaskServerStore store, TaskServerOptions options, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var listings = command.Option("--git-refs") is { } refsPath
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(refsPath, ct))
            : null;
        var workflow = new RecoveryWorkflow(store, options, new OriginRefProbe(http, listings), TimeProvider.System);
        try
        {
            switch (command.Operation)
            {
                case "help":
                    Console.WriteLine(TaskServerCommandLine.RecoveryUsage);
                    return 0;
                case "capture":
                {
                    await store.InitializeForBackupAsync(ct);
                    var custody = command.Option("--custody") is { } custodyPath
                        ? JsonSerializer.Deserialize<RecoveryCustodyDeclaration>(await File.ReadAllTextAsync(custodyPath, ct), RecoveryJson.Options)
                          ?? throw new InvalidDataException("Custody declaration is empty.")
                        : new RecoveryCustodyDeclaration();
                    var manifest = await workflow.CaptureAsync(custody, Actor, ct);
                    Write(new { manifestPath = workflow.ManifestPath(manifest.DataSet.BackupId), manifest });
                    return 0;
                }
                case "copy":
                    await store.InitializeForBackupAsync(ct);
                    Write(await workflow.CopyAsync(command.Option("--backup")!, command.Option("--to")!, ct));
                    return 0;
                case "verify":
                {
                    var result = await workflow.VerifyCopyAsync(
                        command.Option("--from")!, command.Option("--secret-bundle"), !command.Flag("--no-git"), ct);
                    Write(new { result.Report.RestoreAllowed, result.Report.ResumeAllowed, result.Report.Findings, result.Manifest?.ManifestId });
                    return result.Report.RestoreAllowed ? 0 : 2;
                }
                case "restore":
                {
                    DateTime? lossAt = command.Option("--loss-at") is { } loss
                        ? DateTime.Parse(loss, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                        : null;
                    var result = await workflow.RestoreToEmptyAsync(command.Option("--from")!, command.Option("--secret-bundle"), lossAt, Actor, ct);
                    Write(new { result.Restored, result.Message, result.Report.Findings, result.Receipt });
                    return result.Restored ? 0 : 2;
                }
                case "fence-hosts":
                    await store.InitializeAsync(ct);
                    Write(new { revokedCredentials = await workflow.FenceHostsAsync(Actor, ct) });
                    return 0;
                case "reenrol":
                {
                    await store.InitializeAsync(ct);
                    var path = command.Option("--credential-out")!;
                    var issued = await workflow.ReenrolClientToFileAsync(command.Option("--principal")!, path, Actor, ct);
                    Write(new { issued.Principal.PrincipalId, credentialFile = Path.GetFullPath(path), issued.CreatedAt });
                    return 0;
                }
                case "resume":
                {
                    await store.InitializeAsync(ct);
                    var (decision, receipt) = await workflow.ResumeAsync(
                        command.Flag("--old-writer-closed"), command.Flag("--obligations-retained"), command.Flag("--check-only"),
                        command.Option("--secret-bundle"), Actor, ct);
                    Write(new { decision.Allowed, resumed = decision.Allowed && !command.Flag("--check-only"), decision.Blockers, receipt });
                    return decision.Allowed ? 0 : 2;
                }
                default:
                    throw new ArgumentException($"Unknown recovery operation '{command.Operation}'.");
            }
        }
        catch (Exception exception)
        {
            var code = exception is TaskServerConflictException conflict ? $" [{conflict.Code}]" : string.Empty;
            Console.Error.WriteLine($"Task Server recovery {command.Operation} failed{code}: {exception.Message}");
            return 1;
        }
    }

    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, RecoveryJson.Options));
}
