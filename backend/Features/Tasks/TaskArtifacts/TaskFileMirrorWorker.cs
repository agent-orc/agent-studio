using System.Security.Cryptography;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>Outcome of the last mirror cycle, for logs and tests.</summary>
public sealed record TaskFileMirrorCycle(
    DateTimeOffset At,
    int TasksChecked,
    int FilesSent,
    long BytesSent,
    int FilesUnchanged,
    int FilesRemoved,
    int FilesDeferred,
    IReadOnlyList<string> Rejected,
    IReadOnlyList<string> MissingProjects,
    string? Error);

/// <summary>
/// Copies task-folder files into the Task Server's <c>task_files</c> and
/// <c>task_file_revisions</c> while the workspace is still the write authority
/// (pipeline delivery target, section 05 "Task-folder artefacts", AGT-3320).
/// It runs off the request path on its own cadence, sends hashes first and
/// content only for paths the Task Server asks for, and spends at most
/// <c>TaskArtifacts:Mirror:MaxBytesPerCycle</c> per cycle, so a large backlog
/// trickles in rather than competing with the board. A card the Task Server
/// does not hold yet is retried on the next cycle; the board mirror creates it.
/// </summary>
public sealed class TaskFileMirrorWorker : BackgroundService
{
    private static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(30);
    private const int HashOnlyBatchTasks = 200;

    private readonly TaskScannerService _scanner;
    private readonly TaskArtifactsSourceSettings _settings;
    private readonly TaskServerBoardClient _client;
    private readonly TimeProvider _clock;
    private readonly ILogger<TaskFileMirrorWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly long _maximumFileBytes;
    private readonly long _maximumBytesPerCycle;
    private readonly long _batchBytes;
    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, string>> _accepted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _archivedDone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Length, long WriteTicks, string Sha)> _hashes = new(StringComparer.Ordinal);
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public TaskFileMirrorWorker(
        TaskScannerService scanner,
        TaskArtifactsSourceSettings settings,
        TaskServerBoardClient client,
        TimeProvider clock,
        IConfiguration configuration,
        ILogger<TaskFileMirrorWorker> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _client = client;
        _clock = clock;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("TaskArtifacts:Mirror:IntervalSeconds", 30), 1, 3600));
        _maximumFileBytes = Math.Clamp(
            configuration.GetValue("TaskArtifacts:Mirror:MaxFileBytes", TaskFileMirrorPolicy.DefaultMaximumFileBytes),
            1024, 16L * 1024 * 1024);
        _maximumBytesPerCycle = Math.Max(1024,
            configuration.GetValue("TaskArtifacts:Mirror:MaxBytesPerCycle", 64L * 1024 * 1024));
        _batchBytes = Math.Clamp(
            configuration.GetValue("TaskArtifacts:Mirror:BatchBytes", 8L * 1024 * 1024), 64 * 1024, 16L * 1024 * 1024);
    }

    public TaskFileMirrorCycle? LastCycle { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Task file mirror cycle failed");
            }
            try
            {
                await Task.Delay(_interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One mirror cycle over every card of the newest published index generation; serialized.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        await _cycle.WaitAsync(ct);
        try
        {
            if (!_settings.MirrorsToTaskServer || !_client.IsConfigured)
            {
                Reset();
                return;
            }
            if (_clock.GetUtcNow() < _retryAfter) return;
            var publication = await _scanner.AcquireBoardReadAsync(null, ct);
            if (publication is null) return;
            var cycle = new CycleCounters(_maximumBytesPerCycle);
            var cards = publication.Live.Concat(publication.Archive)
                .Where(task => !string.IsNullOrWhiteSpace(task.ProjectName) && !task.Fixture)
                .ToList();
            try
            {
                foreach (var project in cards
                             .GroupBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                    await MirrorProjectAsync(project.Key, project.ToList(), cycle, ct);
                Prune(cards, cycle.SeenPaths);
            }
            catch (Exception exception) when (exception is HttpRequestException
                                                  || (exception is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _retryAfter = _clock.GetUtcNow() + FailureRetry;
                cycle.Error = exception.Message;
                _logger.LogWarning(exception, "Task file mirror could not reach the Task Server");
            }
            LastCycle = cycle.ToRecord(_clock.GetUtcNow());
            if (cycle.FilesSent > 0 || cycle.Rejected.Count > 0)
                _logger.LogInformation(
                    "task-file-mirror sent={Sent} bytes={Bytes} unchanged={Unchanged} removed={Removed} deferred={Deferred} rejected={Rejected}",
                    cycle.FilesSent, cycle.BytesSent, cycle.FilesUnchanged, cycle.FilesRemoved, cycle.FilesDeferred,
                    cycle.Rejected.Count);
        }
        finally
        {
            _cycle.Release();
        }
    }

    private async Task MirrorProjectAsync(string project, IReadOnlyList<TaskInfo> tasks, CycleCounters cycle, CancellationToken ct)
    {
        var deltas = new List<(string Id, TaskInfo Task, TaskFileMirrorTaskDelta Delta)>();
        foreach (var task in tasks)
        {
            cycle.TasksChecked++;
            var key = BoardCardCore.From(task).TaskKey;
            var id = project.ToUpperInvariant() + "\n" + key;
            var archived = string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal);
            if (archived && _archivedDone.TryGetValue(id, out var ticks) && ticks == task.TaskJsonWriteTicks) continue;
            if (string.IsNullOrWhiteSpace(task.FolderPath) || !Directory.Exists(task.FolderPath)) continue;
            IReadOnlyList<TaskFolderFile> files;
            try
            {
                files = ReadFolder(task.FolderPath, cycle.SeenPaths);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed now is skipped, never read as "every file removed".
                _logger.LogDebug(exception, "Task file mirror skipped {Folder} this cycle", task.FolderPath);
                continue;
            }
            var delta = TaskFileMirrorPolicy.Plan(project, key, _accepted.GetValueOrDefault(id), files);
            if (delta.Changed.Count == 0 && delta.Removed.Count == 0)
            {
                if (archived) _archivedDone[id] = task.TaskJsonWriteTicks;
                continue;
            }
            deltas.Add((id, task, delta));
        }

        foreach (var chunk in deltas.Chunk(HashOnlyBatchTasks))
        {
            var request = new TaskFileMirrorRequest(chunk.Select(item => new TaskFileMirrorTask(
                item.Delta.TaskKey,
                item.Delta.Changed.Select(file => new TaskFileMirrorEntry(file.Path, file.Sha256, file.SizeBytes)).ToList(),
                item.Delta.Removed)).ToList());
            var response = await _client.MirrorTaskFilesAsync(project, request, ct);
            if (response is null)
            {
                cycle.MissingProjects.Add(project);
                return;
            }
            var rejected = response.Rejected.Select(item => item.TaskKey).ToHashSet(StringComparer.Ordinal);
            cycle.Rejected.AddRange(rejected.Select(key => project + "/" + key));
            var needed = response.Needed.Select(item => (item.TaskKey, item.Path)).ToHashSet();
            var send = new List<(string Id, string TaskKey, TaskFolderFile File)>();
            foreach (var (id, task, delta) in chunk)
            {
                if (rejected.Contains(delta.TaskKey)) continue;
                var accepted = AcceptedFor(id);
                foreach (var file in delta.Changed)
                {
                    if (needed.Contains((delta.TaskKey, file.Path))) send.Add((id, delta.TaskKey, file));
                    else
                    {
                        accepted[file.Path] = file.Sha256;
                        cycle.FilesUnchanged++;
                    }
                }
                foreach (var path in delta.Removed) accepted.Remove(path);
                cycle.FilesRemoved += delta.Removed.Count;
            }
            await SendContentAsync(project, send, cycle, ct);
            foreach (var (id, task, delta) in chunk)
            {
                if (!string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal) || rejected.Contains(delta.TaskKey)) continue;
                var accepted = _accepted.GetValueOrDefault(id);
                if (accepted is not null && delta.Changed.All(file => accepted.TryGetValue(file.Path, out var sha) && sha == file.Sha256))
                    _archivedDone[id] = task.TaskJsonWriteTicks;
            }
        }
    }

    private async Task SendContentAsync(
        string project, IReadOnlyList<(string Id, string TaskKey, TaskFolderFile File)> files, CycleCounters cycle, CancellationToken ct)
    {
        var batch = new List<(string Id, string TaskKey, TaskFolderFile File, string Content)>();
        long batchBytes = 0;
        foreach (var item in files)
        {
            if (cycle.BudgetLeft < item.File.SizeBytes)
            {
                cycle.FilesDeferred++;
                continue;
            }
            byte[] content;
            try
            {
                content = await File.ReadAllBytesAsync(item.File.FullPath, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Task file mirror skipped unreadable {Path}", item.File.FullPath);
                cycle.FilesDeferred++;
                continue;
            }
            if (!string.Equals(Sha256(content), item.File.Sha256, StringComparison.Ordinal))
            {
                // Changed since it was hashed: the next cycle hashes and sends it again.
                cycle.FilesDeferred++;
                continue;
            }
            if (batch.Count > 0 && batchBytes + content.LongLength > _batchBytes)
            {
                await SendBatchAsync(project, batch, cycle, ct);
                batch.Clear();
                batchBytes = 0;
            }
            batch.Add((item.Id, item.TaskKey, item.File, Convert.ToBase64String(content)));
            batchBytes += content.LongLength;
            cycle.BudgetLeft -= content.LongLength;
        }
        if (batch.Count > 0) await SendBatchAsync(project, batch, cycle, ct);
    }

    private async Task SendBatchAsync(
        string project, IReadOnlyList<(string Id, string TaskKey, TaskFolderFile File, string Content)> batch,
        CycleCounters cycle, CancellationToken ct)
    {
        var request = new TaskFileMirrorRequest(batch.GroupBy(item => item.TaskKey, StringComparer.Ordinal)
            .Select(group => new TaskFileMirrorTask(group.Key, group.Select(item => new TaskFileMirrorEntry(
                item.File.Path, item.File.Sha256, item.File.SizeBytes, MediaType(item.File.Path), item.Content)).ToList()))
            .ToList());
        var response = await _client.MirrorTaskFilesAsync(project, request, ct);
        if (response is null)
        {
            cycle.MissingProjects.Add(project);
            return;
        }
        var rejected = response.Rejected.Select(item => item.TaskKey).ToHashSet(StringComparer.Ordinal);
        foreach (var item in batch.Where(item => !rejected.Contains(item.TaskKey)))
        {
            AcceptedFor(item.Id)[item.File.Path] = item.File.Sha256;
            cycle.FilesSent++;
            cycle.BytesSent += item.File.SizeBytes;
        }
    }

    private IReadOnlyList<TaskFolderFile> ReadFolder(string folder, HashSet<string> seen)
    {
        var files = new List<TaskFolderFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            seen.Add(path);
            var relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists || !TaskFileMirrorPolicy.Include(relative, info.Length, _maximumFileBytes)) continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Task file mirror skipped {Path}", path);
                continue;
            }
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (!_hashes.TryGetValue(path, out var cached) || cached.Length != info.Length || cached.WriteTicks != ticks)
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    cached = (info.Length, ticks, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(exception, "Task file mirror could not hash {Path}", path);
                    continue;
                }
                _hashes[path] = cached;
            }
            files.Add(new TaskFolderFile(relative, cached.Sha, cached.Length, path));
        }
        return files;
    }

    /// <summary>Drops ledger entries of cards that left the board and hashes of files no longer seen.</summary>
    private void Prune(IReadOnlyList<TaskInfo> cards, HashSet<string> seenPaths)
    {
        var ids = cards.Select(task => task.ProjectName.ToUpperInvariant() + "\n" + BoardCardCore.From(task).TaskKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in _accepted.Keys.Where(id => !ids.Contains(id)).ToList()) _accepted.Remove(id);
        foreach (var id in _archivedDone.Keys.Where(id => !ids.Contains(id)).ToList()) _archivedDone.Remove(id);
        foreach (var path in _hashes.Keys.Where(path => !seenPaths.Contains(path)).ToList())
        {
            // Skipped archived cards keep their hashes: their files were not listed this cycle.
            if (!File.Exists(path)) _hashes.Remove(path);
        }
    }

    private Dictionary<string, string> AcceptedFor(string id)
    {
        if (!_accepted.TryGetValue(id, out var accepted))
            _accepted[id] = accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        return accepted;
    }

    private void Reset()
    {
        _accepted.Clear();
        _archivedDone.Clear();
        _hashes.Clear();
        _retryAfter = DateTimeOffset.MinValue;
    }

    private static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".jsonl" => "application/x-ndjson",
        ".md" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".txt" or ".log" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    private sealed class CycleCounters(long budget)
    {
        public long BudgetLeft = budget;
        public int TasksChecked;
        public int FilesSent;
        public long BytesSent;
        public int FilesUnchanged;
        public int FilesRemoved;
        public int FilesDeferred;
        public string? Error;
        public readonly List<string> Rejected = [];
        public readonly List<string> MissingProjects = [];
        public readonly HashSet<string> SeenPaths = new(StringComparer.Ordinal);

        public TaskFileMirrorCycle ToRecord(DateTimeOffset at) => new(at, TasksChecked, FilesSent, BytesSent,
            FilesUnchanged, FilesRemoved, FilesDeferred, Rejected.ToList(), MissingProjects.ToList(), Error);
    }
}
