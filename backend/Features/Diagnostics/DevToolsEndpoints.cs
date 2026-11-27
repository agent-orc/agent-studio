using System.Diagnostics;
using System.Text;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Tool-development-only routes guarded by per-checkout config flags
/// in <c>appsettings.Local.json</c>:
///
/// <list type="bullet">
///   <item><c>DevTools:UpdateStableEnabled</c> -> POST startup and SSE output of
///   <c>update-stable.sh</c>. Only meaningful in the dev checkout, since
///   the script restarts stable.</item>
///   <item><c>DevTools:DeleteE2EJobsEnabled</c> -> list and bulk-delete
///   jobs whose id or title contains "E2E" (case-insensitive), across
///   every configured watch path.</item>
/// </list>
///
/// Both flags default to false. The frontend reads them via
/// <c>/api/environment</c> and only renders the corresponding header
/// buttons when the flags are set.
/// </summary>
public static class DevToolsEndpoints
{
    public static void MapDevToolsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/devtools");

        // Keep only the latest operation in this application's lifetime. GET never
        // creates one; reconnects and multiple readers share the same process.
        var updates = new UpdateStableOperations();
        var runner = app.Services.GetService<IUpdateStableProcessRunner>() ?? new UpdateStableProcessRunner();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DevTools.UpdateStable");

        group.MapPost("/update-stable", (IConfiguration config) =>
        {
            if (!config.GetValue<bool>("DevTools:UpdateStableEnabled"))
                return Results.Text("DevTools:UpdateStableEnabled is false", statusCode: StatusCodes.Status403Forbidden);
            var scriptPath = ResolveUpdateStableScript(config);
            if (scriptPath is null)
                return Results.Problem("update-stable.sh not found. Set DevTools:UpdateStableScriptPath or use scripts/update-stable.sh from the dev checkout.");

            var operation = updates.Start(scriptPath, runner, logger);
            if (operation is null)
                return Results.Conflict(new { error = "update-stable-running" });
            var streamUrl = $"/api/devtools/update-stable/stream?operationId={operation.Id}";
            return Results.Accepted(streamUrl, new { operationId = operation.Id, streamUrl });
        }).WithMetadata(new LocalOperationEffectMetadata());

        group.MapGet("/update-stable/stream", async (
            HttpContext http, IConfiguration config, Guid? operationId, CancellationToken ct) =>
        {
            if (!config.GetValue<bool>("DevTools:UpdateStableEnabled"))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("DevTools:UpdateStableEnabled is false", ct);
                return;
            }
            var operation = updates.Find(operationId);
            if (operation is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "update-stable-not-started" }, ct);
                return;
            }
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            try { await operation.StreamAsync(http, ct); }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                // A subscriber disconnects without cancelling the updater. It may
                // itself be restarting this backend; never kill its process tree.
                logger.LogDebug(ex, "Update stream subscriber disconnected");
            }
        });

        group.MapGet("/e2e-jobs", (IConfiguration config, TaskScannerService scanner) =>
        {
            if (!config.GetValue<bool>("DevTools:DeleteE2EJobsEnabled"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var matches = scanner.ScanAllJobs()
                .Where(IsE2EJob)
                .Select(j => new
                {
                    jobKey = j.TaskKey,
                    id = j.Id,
                    title = j.Title,
                    state = j.State,
                    projectName = j.ProjectName,
                    watchPath = j.WatchPath
                })
                .OrderBy(j => j.projectName)
                .ThenBy(j => j.id)
                .ToList();

            return Results.Ok(matches);
        });

        // Fixture-marker migration: scan the workspace for fixture-shaped
        // job folders that pre-date the `fixture: true` flag and report what
        // would be marked. Dry-run by default; pass apply=true to rewrite
        // the task.json files. Reuses the DeleteE2EJobs gate since it is the
        // same one-shot fixture maintenance surface.
        group.MapGet("/fixtures/scan", (
            IConfiguration config,
            FixtureMigrationService migration,
            bool? apply) =>
        {
            if (!config.GetValue<bool>("DevTools:DeleteE2EJobsEnabled"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var report = migration.Scan(apply == true);
            return Results.Ok(report);
        }).WithMetadata(new LocalOperationEffectMetadata());

        group.MapPost("/e2e-jobs/delete", (
            IConfiguration config,
            TaskScannerService scanner,
            TaskStateMachine states,
            DeleteE2EJobsRequest req) =>
        {
            if (!config.GetValue<bool>("DevTools:DeleteE2EJobsEnabled"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var requested = req.TaskKeys ?? new List<string>();
            var keySet = new HashSet<string>(requested, StringComparer.Ordinal);

            var candidates = scanner.ScanAllJobs()
                .Where(IsE2EJob)
                .Where(j => keySet.Count == 0 || keySet.Contains(j.TaskKey))
                .ToList();

            var deleted = new List<string>();
            var failed = new List<string>();
            foreach (var job in candidates)
            {
                if (states.DeleteJob(job.Id, job.WatchPath))
                    deleted.Add(job.TaskKey);
                else
                    failed.Add(job.TaskKey);
            }

            return Results.Ok(new
            {
                deletedCount = deleted.Count,
                failedCount = failed.Count,
                deleted,
                failed
            });
        });
    }

    private static bool IsE2EJob(TaskInfo job)
    {
        return ContainsE2E(job.Id) || ContainsE2E(job.Title);
    }

    private static bool ContainsE2E(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && value.Contains("E2E", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveUpdateStableScript(IConfiguration config)
    {
        var configured = config["DevTools:UpdateStableScriptPath"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // Prefer the versioned updater in a checkout. Keep the outer devspace
        // copy as a compatibility fallback for existing local installations.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
        {
            var versionedCandidate = Path.Combine(dir.FullName, "scripts", "update-stable.sh");
            if (File.Exists(versionedCandidate)) return versionedCandidate;

            var legacyCandidate = Path.Combine(dir.FullName, "update-stable.sh");
            if (File.Exists(legacyCandidate)) return legacyCandidate;
        }
        return null;
    }

    private sealed class UpdateStableProcessRunner : IUpdateStableProcessRunner
    {
        public async Task<int> RunAsync(string scriptPath, Action<string, string> output)
        {
            var bash = ResolveBashExecutable();
            var psi = new ProcessStartInfo
            {
                FileName = bash,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add(ToBashPath(scriptPath));
            output("log", $"$ {bash} {string.Join(' ', psi.ArgumentList)}");
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) throw new InvalidOperationException("Failed to start update-stable.sh");
            var stdout = PumpStreamAsync(proc.StandardOutput, "stdout", output);
            var stderr = PumpStreamAsync(proc.StandardError, "stderr", output);
            // Request cancellation must not stop the updater or its output drains.
            await proc.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return proc.ExitCode;
        }
    }

    private sealed class UpdateStableOperations
    {
        private readonly object _gate = new();
        private UpdateStableOperation? _latest;

        public UpdateStableOperation? Start(string script, IUpdateStableProcessRunner runner, ILogger logger)
        {
            lock (_gate)
            {
                if (_latest is { Completed: false }) return null;
                var operation = new UpdateStableOperation();
                _latest = operation;
                _ = operation.RunAsync(script, runner, logger);
                return operation;
            }
        }

        public UpdateStableOperation? Find(Guid? id)
        {
            lock (_gate) return id is null || _latest?.Id == id ? _latest : null;
        }
    }

    private sealed class UpdateStableOperation
    {
        private readonly object _gate = new();
        private readonly Queue<(long Sequence, string Kind, string Data)> _events = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _sequence;
        private bool _completed;
        public Guid Id { get; } = Guid.NewGuid();
        public bool Completed { get { lock (_gate) return _completed; } }

        private void Publish(string kind, string data)
        {
            lock (_gate)
            {
                // Bound replay memory, including output from noisy scripts.
                _events.Enqueue((++_sequence, kind, data.Length > 16384 ? data[..16384] : data));
                if (_events.Count > 2048) _events.Dequeue();
                _changed.TrySetResult();
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public async Task RunAsync(string script, IUpdateStableProcessRunner runner, ILogger logger)
        {
            try
            {
                var exitCode = await runner.RunAsync(script, Publish);
                Publish("done", $"exit code {exitCode}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to run update-stable.sh");
                Publish("error", $"Failed to run: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _completed = true;
                    _changed.TrySetResult();
                }
            }
        }

        public async Task StreamAsync(HttpContext http, CancellationToken ct)
        {
            long after = 0;
            while (true)
            {
                (long Sequence, string Kind, string Data)[] batch;
                Task changed;
                bool completed;
                lock (_gate)
                {
                    batch = _events.Where(item => item.Sequence > after).ToArray();
                    completed = _completed;
                    changed = _changed.Task;
                }
                foreach (var item in batch)
                {
                    await WriteSseAsync(http, item.Kind, item.Data, ct);
                    after = item.Sequence;
                }
                if (completed) return;
                await changed.WaitAsync(ct);
            }
        }
    }

    private static string ResolveBashExecutable()
    {
        // Order: env override -> the shared Git Bash resolution (Git Bash on
        // Windows, then plain bash).
        var fromEnv = Environment.GetEnvironmentVariable("DEVTOOLS_BASH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        return BashExecutable.Path;
    }

    private static string ToBashPath(string path)
    {
        // Git Bash accepts the native Windows path on the command line as long
        // as it's quoted; ProcessStartInfo.ArgumentList quotes for us.
        return path;
    }

    private static async Task PumpStreamAsync(StreamReader reader, string kind, Action<string, string> output)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null) output(kind, line);
    }

    private static async Task WriteSseAsync(HttpContext http, string evt, string data, CancellationToken ct)
    {
        // SSE wire format: each "data:" line is one piece of the event payload.
        // We send single-line payloads only, which keeps parsing on the client
        // trivial (one event = one log line).
        var sanitized = data.Replace("\r", "").Replace("\n", "\ndata: ");
        var payload = $"event: {evt}\ndata: {sanitized}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await http.Response.Body.WriteAsync(bytes, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}

public sealed class DeleteE2EJobsRequest
{
    public List<string>? TaskKeys { get; set; }
}

/// <summary>An updater runs independently of the HTTP trigger and its subscribers.</summary>
internal interface IUpdateStableProcessRunner
{
    Task<int> RunAsync(string scriptPath, Action<string, string> output);
}
