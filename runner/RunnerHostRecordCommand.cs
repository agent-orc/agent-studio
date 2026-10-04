using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// <c>agent-host host-record migrate|check|render|enrolment</c>. Offline tooling
/// for the owned host record; it never contacts the Task Server or reads tokens.
/// </summary>
public static class RunnerHostRecordCommand
{
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var verb = args.Count > 0 ? args[0] : string.Empty;
        if (args.Count > 0 && args.Count % 2 == 0)
        {
            error.WriteLine($"error: {args[^1]} needs a value.");
            return 2;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index + 1 < args.Count; index += 2) options[args[index]] = args[index + 1];
        string? Read(string key)
        {
            if (!options.TryGetValue(key, out var path)) return null;
            if (!File.Exists(path))
                throw new ArgumentException($"{key} input file '{path}' does not exist or is not a regular file.");
            return File.ReadAllText(path);
        }
        var recordPath = options.GetValueOrDefault("--record", RunnerHostRecord.DefaultPath);
        try
        {
            switch (verb)
            {
                case "migrate":
                {
                    var migration = RunnerHostRecordPolicy.Migrate(
                        Read("--runner-env"), Read("--review-env"), Read("--profile"),
                        options.GetValueOrDefault("--host-class", "linux"));
                    foreach (var note in migration.Notes) error.WriteLine($"note: {note}");
                    if (migration.Record is null)
                    {
                        foreach (var item in migration.Errors) error.WriteLine($"error: {item}");
                        return 2;
                    }
                    var json = JsonSerializer.Serialize(migration.Record, RunnerHostRecord.Json) + "\n";
                    if (options.TryGetValue("--out", out var target)) WritePrivate(target, json);
                    else output.Write(json);
                    return 0;
                }
                case "check":
                {
                    var record = Load(recordPath);
                    var errors = RunnerHostRecordPolicy.Validate(record);
                    foreach (var item in errors) error.WriteLine($"error: {item}");
                    if (errors.Count == 0)
                        output.WriteLine($"ok {record.HostId} sha256:{RunnerHostRecordPolicy.Digest(record)} "
                            + $"roles={string.Join(',', record.Roles.Select(role => role.Role))} "
                            + $"slots={record.Envelope.TotalSlots}/{record.Envelope.CodingSlots}/{record.Envelope.ReviewSlots}");
                    return errors.Count == 0 ? 0 : 2;
                }
                case "render":
                {
                    var rendering = RunnerHostRecordPolicy.Render(Load(recordPath));
                    var directory = options.GetValueOrDefault("--out-dir")
                                    ?? throw new ArgumentException("--out-dir is required.");
                    Directory.CreateDirectory(directory);
                    foreach (var (name, content) in rendering.Files) WritePrivate(Path.Combine(directory, name), content);
                    foreach (var service in rendering.Services)
                        output.WriteLine($"{service.Role}\t{service.UnitName}\t{service.EnvFile}");
                    return 0;
                }
                case "enrolment":
                {
                    var generation = long.Parse(options.GetValueOrDefault("--expected-generation", "0"));
                    var record = Load(recordPath);
                    var errors = RunnerHostRecordPolicy.Validate(record);
                    if (errors.Count > 0)
                    {
                        foreach (var item in errors) error.WriteLine($"error: {item}");
                        return 2;
                    }
                    output.WriteLine(JsonSerializer.Serialize(record.ToEnrolment(generation), RunnerHostRecord.Json));
                    return 0;
                }
                default:
                    error.WriteLine("Usage: agent-host host-record <migrate|check|render|enrolment> [--record <host.json>] "
                        + "[--runner-env f] [--review-env f] [--profile f] [--host-class c] [--out f] [--out-dir d] "
                        + "[--expected-generation n]");
                    return 2;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or FormatException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static RunnerHostRecord Load(string path)
        => JsonSerializer.Deserialize<RunnerHostRecord>(File.ReadAllText(path), RunnerHostRecord.Json)
           ?? throw new ArgumentException($"{path} is empty.");

    private static void WritePrivate(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }
}
